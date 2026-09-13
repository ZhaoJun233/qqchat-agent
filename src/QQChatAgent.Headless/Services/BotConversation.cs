using QQChatAgent.Models;

namespace QQChatAgent.Services;

/// <summary>
/// 一个 QQ 会话（私聊/群聊）的内存模型（headless：无 ObservableCollection / 无画刷）。
/// 所有访问都通过内部锁保护：消息可能在传输线程、历史拉取任务、Agent worker 三处并发写入。
/// </summary>
public sealed class BotConversation
{
    private readonly object _gate = new();
    private readonly List<ChatMessage> _messages = new();

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>QQ 映射键："private:{QQ号}" 或 "group:{群号}"。headless 版必为真实 QQ 会话。</summary>
    public required string SourceKey { get; init; }

    public ConversationKind Kind { get; init; }

    /// <summary>会话名（群名/好友昵称）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>群历史是否已拉取（避免重复拉取）。</summary>
    public bool HistoryLoaded { get; set; }

    /// <summary>该会话有待处理的入站消息（静默兜底轮询用；不持久化）。</summary>
    public bool HasPendingReply { get; set; }

    /// <summary>未读数（入站消息递增，Web UI 打开会话时清零；不持久化）。</summary>
    public int Unread { get; private set; }

    /// <summary>AI 是否正在为这个会话思考（Web UI 显示「正在思考…」）。</summary>
    public bool Thinking { get; set; }

    /// <summary>内存/磁盘保留的最大消息条数；超出部分从头部淘汰（由 OnEvicted 归档）。</summary>
    public int MaxMessages { get; set; } = 500;

    /// <summary>被淘汰的旧消息回调（sourceKey, 被淘汰的消息）——用于写入归档文件，不丢数据。</summary>
    public Action<string, IReadOnlyList<ChatMessage>>? OnEvicted;

    /// <summary>已淘汰的总条数（可观测）。</summary>
    public long EvictedCount { get; private set; }

    private long _nextSeq;

    /// <summary>下一个待分配的会话内序号（持久化用；赋值只增不减）。</summary>
    public long NextSeq
    {
        get
        {
            lock (_gate)
            {
                return _nextSeq;
            }
        }

        set
        {
            lock (_gate)
            {
                _nextSeq = Math.Max(_nextSeq, value);
            }
        }
    }

    /// <summary>最新一条消息的预览文本（供会话列表）。</summary>
    public string Preview
    {
        get
        {
            lock (_gate)
            {
                var last = _messages.LastOrDefault(m => m.Role != MessageRole.System) ?? _messages.LastOrDefault();
                if (last is null)
                {
                    return string.Empty;
                }

                var flat = last.Text.Replace("\r", " ").Replace("\n", " ").Trim();
                return flat.Length <= 60 ? flat : flat[..60] + "…";
            }
        }
    }

    /// <summary>标记已读（返回是否发生变化）。</summary>
    public bool MarkRead()
    {
        lock (_gate)
        {
            if (Unread == 0)
            {
                return false;
            }

            Unread = 0;
            return true;
        }
    }

    public DateTimeOffset LastTime { get; private set; } = DateTimeOffset.Now;

    /// <summary>消息快照（副本，可安全遍历）。</summary>
    public IReadOnlyList<ChatMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    public int MessageCount
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    /// <summary>追加一条消息到末尾（自动分配自增序号，供前端增量拉取）。</summary>
    public void Append(ChatMessage message)
    {
        List<ChatMessage>? evicted = null;
        lock (_gate)
        {
            message.Seq = ++_nextSeq;
            _messages.Add(message);
            if (message.Timestamp > LastTime)
            {
                LastTime = message.Timestamp;
            }

            if (message.Role == MessageRole.Peer)
            {
                Unread++;
            }

            evicted = TrimLocked();
        }

        NotifyEvicted(evicted);
    }

    /// <summary>按当前 MaxMessages 裁剪一次（运行中把上限改小时立即生效，不用等下一条消息）。</summary>
    public void TrimToMax()
    {
        List<ChatMessage>? evicted;
        lock (_gate)
        {
            evicted = TrimLocked();
        }

        NotifyEvicted(evicted);
    }

    /// <summary>超出上限时从头部移除最老的，调用方必须已持锁。返回被移除的列表。</summary>
    private List<ChatMessage>? TrimLocked()
    {
        if (MaxMessages <= 0 || _messages.Count <= MaxMessages)
        {
            return null;
        }

        var overflow = _messages.Count - MaxMessages;
        var evicted = _messages.GetRange(0, overflow);
        _messages.RemoveRange(0, overflow);
        EvictedCount += overflow;
        return evicted;
    }

    private void NotifyEvicted(List<ChatMessage>? evicted)
    {
        if (evicted is null || evicted.Count == 0 || OnEvicted is null)
        {
            return;
        }

        try
        {
            OnEvicted(SourceKey, evicted);
        }
        catch
        {
            // 归档失败不影响主流程
        }
    }

    /// <summary>
    /// 把一批历史消息（正序：旧→新）合并到会话顶部，并按 message_id 去重。
    /// 返回**实际留在会话里的**条数（已满员时补录的历史会立即被裁掉，不能谎报成功）。
    /// </summary>
    public int MergeHistoryAtTop(IEnumerable<ChatMessage> history)
    {
        List<ChatMessage>? evicted;
        List<ChatMessage> inserted;
        lock (_gate)
        {
            var existing = _messages
                .Where(m => m.QqMessageId.HasValue)
                .Select(m => m.QqMessageId!.Value)
                .ToHashSet();

            var fresh = history
                .Where(m => !m.QqMessageId.HasValue || !existing.Contains(m.QqMessageId.Value))
                .ToList();
            if (fresh.Count == 0)
            {
                return 0;
            }

            // 历史消息在时间上比现有消息**更早**，所以序号必须比当前最小值还小，
            // 而不能沿用 ++_nextSeq（那样它们在“序号=位置”的语义下会变成“最新的”，
            // 于是既进不了上下文窗口、也进不了人物档案 —— 补录就白干了）。
            // 用负数向下排：既保住现有消息（及其档案记录）的序号不变，又保证时间顺序一致。
            var minSeq = _messages.Count > 0 ? _messages.Min(m => m.Seq) : 1;
            var baseSeq = minSeq - fresh.Count;
            for (var i = 0; i < fresh.Count; i++)
            {
                fresh[i].Seq = baseSeq + i;
            }

            inserted = fresh;
            _messages.InsertRange(0, fresh);
            if (_messages.Count > 0)
            {
                LastTime = _messages[^1].Timestamp;
            }

            evicted = TrimLocked();
        }

        NotifyEvicted(evicted);

        // 只统计真正留下来的（容量已满时，插入到最前面的旧消息会被裁掉）
        lock (_gate)
        {
            return inserted.Count(m => _messages.Contains(m));
        }
    }

    /// <summary>最近 n 条消息（正序）。</summary>
    public List<ChatMessage> TakeLast(int count)
    {
        lock (_gate)
        {
            return count >= _messages.Count
                ? new List<ChatMessage>(_messages)
                : _messages.GetRange(_messages.Count - count, count);
        }
    }

    /// <summary>导出为可持久化快照。</summary>
    public ConversationRecord ToRecord()
    {
        lock (_gate)
        {
            return new ConversationRecord
            {
                Id = Id,
                SourceKey = SourceKey,
                Kind = Kind.ToString(),
                Name = Name,
                AvatarText = FirstChar(Name),
                Messages = _messages.Select(m => new MessageRecord
                {
                    Role = m.Role.ToString(),
                    Text = m.Text,
                    TimeUnix = m.Timestamp.ToUnixTimeSeconds(),
                    SenderName = m.SenderName,
                    SenderId = m.SenderId,
                    ImageUrls = m.ImageUrls is { Count: > 0 } ? m.ImageUrls.ToList() : null,
                    QqMessageId = m.QqMessageId,
                    Seq = m.Seq
                }).ToList(),
                LastTimeUnix = LastTime.ToUnixTimeSeconds(),
                UnreadCount = 0,
                NextSeq = _nextSeq
            };
        }
    }

    /// <summary>从磁盘快照还原。</summary>
    public static BotConversation FromRecord(
        ConversationRecord record,
        int maxMessages = 500,
        Action<string, IReadOnlyList<ChatMessage>>? onEvicted = null)
    {
        var kind = Enum.TryParse<ConversationKind>(record.Kind, true, out var k)
            ? k
            : (record.SourceKey?.StartsWith("group:", StringComparison.Ordinal) == true
                ? ConversationKind.GroupChat
                : ConversationKind.PrivateChat);

        var conversation = new BotConversation
        {
            Id = string.IsNullOrEmpty(record.Id) ? Guid.NewGuid().ToString("N") : record.Id,
            SourceKey = record.SourceKey!,
            Kind = kind,
            Name = string.IsNullOrEmpty(record.Name) ? record.SourceKey! : record.Name,
            HistoryLoaded = true, // 已有本地记录的会话无需再拉历史
            MaxMessages = maxMessages,
            OnEvicted = onEvicted
        };

        var restored = new List<ChatMessage>();
        foreach (var msg in record.Messages)
        {
            if (string.IsNullOrEmpty(msg.Text))
            {
                continue;
            }

            restored.Add(new ChatMessage
            {
                Role = Enum.TryParse<MessageRole>(msg.Role, true, out var role) ? role : MessageRole.Peer,
                SenderName = msg.SenderName,
                SenderId = msg.SenderId,
                Text = msg.Text,
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(msg.TimeUnix),
                ImageUrls = msg.ImageUrls,
                QqMessageId = msg.QqMessageId,
                Seq = msg.Seq
            });
        }

        conversation.AppendRange(restored);

        // 序号计数器必须“只进不退”：即使消息一条都没恢复（例如上次写库被截断），
        // 也要接着上次的号往下发 —— 否则新消息的序号会小于画像已折叠的边界（记忆冻结），
        // 还会与 (source_key, seq) 主键里的老消息撞号。
        conversation.NextSeq = record.NextSeq;
        return conversation;
    }

    private void AppendRange(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage>? evicted;
        lock (_gate)
        {
            foreach (var m in messages)
            {
                if (m.Seq != 0)
                {
                    // 从磁盘恢复的序号：原样保留，并把计数器推到最大值之后，
                    // 保证新消息的序号继续递增（否则档案/画像的判据会错位）。
                    _nextSeq = Math.Max(_nextSeq, m.Seq);
                }
                else
                {
                    m.Seq = ++_nextSeq; // 老数据没有序号：按列表顺序补一个
                }

                _messages.Add(m);
            }

            if (_messages.Count > 0)
            {
                LastTime = _messages[^1].Timestamp;
            }

            // 磁盘上的旧文件可能比当前上限大（上限可配） → 恢复时也要裁一次
            evicted = TrimLocked();
        }

        NotifyEvicted(evicted);
    }

    /// <summary>解析 SourceKey → (是否群聊, QQ号/群号)。</summary>
    public (bool IsGroup, long Id) Target
    {
        get
        {
            var parts = SourceKey.Split(':');
            return parts.Length == 2 && long.TryParse(parts[1], out var id)
                ? (parts[0] == "group", id)
                : (false, 0);
        }
    }

    private static string FirstChar(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "?" : trimmed[..1];
    }
}
