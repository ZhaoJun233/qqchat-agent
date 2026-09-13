using System.Text.Encodings.Web;
using System.Text.Json;
using QQChatAgent.Models;
using QQChatAgent.Services.Data;

namespace QQChatAgent.Services;

/// <summary>
/// 会话与消息的持久化（SQLite：<c>conversations</c> + <c>messages</c> 两张表）。
///
/// 与老 JSON 版的区别（为什么换）：
///   • JSON 版是**整份重写**（几千条消息时每次都序列化全部内容），现在是按会话增量 upsert；
///   • 归档从“另开一堆 .jsonl 文件”改成同一张表里 <c>archived=1</c> 的行 ——
///     翻旧账、按时间查、清理都变成一句 SQL；
///   • 撤回只是把 <c>recalled</c> 改成 1（不删行），符合“内容保留但标出来”的语义。
/// 对外 API 保持原样（LoadAsync / RequestSave），上层不用改。
/// </summary>
public sealed class ConversationStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>加载全部会话（不含归档消息；归档由面板的 /api/archive 单独查）。</summary>
    public List<ConversationRecord> LoadAsync()
    {
        try
        {
            return LoadCore();
        }
        catch (Exception ex)
        {
            FileLog.Warn("Store", "读取会话失败（按空处理）：" + ex.Message);
            return new List<ConversationRecord>();
        }
    }

    /// <summary>同步加载（关停刷盘等场景用）。</summary>
    public List<ConversationRecord> LoadAll() => LoadAsync();

    private static List<ConversationRecord> LoadCore()
    {
        {
            var conversations = AppDatabase.Query("""
                SELECT id, source_key, kind, name, avatar_text, avatar_url, avatar_index,
                       last_time_unix, unread_count, next_seq
                FROM conversations
                ORDER BY last_time_unix DESC
                """, r => new ConversationRecord
            {
                Id = AppDatabase.Str(r, "id"),
                SourceKey = AppDatabase.Str(r, "source_key"),
                Kind = AppDatabase.Str(r, "kind") ?? "GroupChat",
                Name = AppDatabase.Str(r, "name") ?? string.Empty,
                AvatarText = AppDatabase.Str(r, "avatar_text") ?? "?",
                AvatarUrl = AppDatabase.Str(r, "avatar_url"),
                AvatarIndex = AppDatabase.Int(r, "avatar_index"),
                LastTimeUnix = AppDatabase.Long(r, "last_time_unix"),
                UnreadCount = AppDatabase.Int(r, "unread_count"),
                NextSeq = AppDatabase.Long(r, "next_seq")
            });

            // 消息一次查完再按会话分：避免“会话数 × 一次查询”的 N+1
            var byKey = AppDatabase.Query("""
                SELECT source_key, seq, role, text, time_unix, sender_name, sender_id, qq_message_id, recalled, images
                FROM messages
                WHERE archived = 0
                ORDER BY source_key, seq
                """, r => (
                    Key: AppDatabase.Str(r, "source_key") ?? string.Empty,
                    Msg: new MessageRecord
                    {
                        Seq = AppDatabase.Long(r, "seq"),
                        Role = AppDatabase.Str(r, "role") ?? "Peer",
                        Text = AppDatabase.Str(r, "text") ?? string.Empty,
                        TimeUnix = AppDatabase.Long(r, "time_unix"),
                        SenderName = AppDatabase.Str(r, "sender_name"),
                        SenderId = AppDatabase.LongOrNull(r, "sender_id"),
                        QqMessageId = AppDatabase.LongOrNull(r, "qq_message_id"),
                        Recalled = AppDatabase.Bool(r, "recalled"),
                        ImageUrls = ParseImages(AppDatabase.Str(r, "images"))
                    })).ToLookup(x => x.Key, x => x.Msg);

            foreach (var conversation in conversations)
            {
                var key = conversation.SourceKey ?? string.Empty;
                if (byKey.Contains(key))
                {
                    conversation.Messages = byKey[key].ToList();
                }
            }

            return conversations;
        }
    }

    /// <summary>请求保存（节流：1 秒内多次变更合并为一次写库）。</summary>
    public void RequestSave(IEnumerable<ConversationRecord> records)
    {
        _ = SaveCoreAsync(records.ToList());
    }

    /// <summary>
    /// 写库（同步，但跑在线程池上）。
    /// 这里**不做 1 秒节流延迟**：上层 BotAgent.SaveLoopAsync 已经有 150ms 合并窗口，
    /// 而额外那 1 秒会把“刚写进来的消息”拖到进程可能已经被杀之后 ——
    /// 线上/测试都验过：重启后活动消息丢空，然后序号从 0 重开、把老消息覆盖掉。
    /// </summary>
    private async Task SaveCoreAsync(List<ConversationRecord> records)
    {
        await _lock.WaitAsync();
        try
        {
            await AppDatabase.WriteAsync(conn =>
            {
                foreach (var c in records)
                {
                    var key = string.IsNullOrWhiteSpace(c.SourceKey) ? $"local:{c.Id}" : c.SourceKey!;
                    AppDatabase.Exec(conn, """
                        INSERT INTO conversations(id, source_key, kind, name, avatar_text, avatar_url, avatar_index,
                                                  last_time_unix, unread_count, history_loaded, max_messages, next_seq, updated_unix)
                        VALUES($id, $key, $kind, $name, $at, $au, $ai, $lt, $uc, 0, 500, $next, $now)
                        ON CONFLICT(source_key) DO UPDATE SET
                            name = excluded.name, avatar_text = excluded.avatar_text, avatar_url = excluded.avatar_url,
                            avatar_index = excluded.avatar_index, last_time_unix = excluded.last_time_unix,
                            unread_count = excluded.unread_count, next_seq = excluded.next_seq,
                            updated_unix = excluded.updated_unix
                        """,
                        ("$id", string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id!),
                        ("$key", key),
                        ("$kind", c.Kind),
                        ("$name", c.Name ?? string.Empty),
                        ("$at", c.AvatarText),
                        ("$au", c.AvatarUrl),
                        ("$ai", c.AvatarIndex),
                        ("$lt", c.LastTimeUnix),
                        ("$uc", c.UnreadCount),
                        ("$next", c.NextSeq),
                        ("$now", DateTimeOffset.Now.ToUnixTimeSeconds()));

                    // 消息：**只 upsert，不删**。
                    // 为什么：保存是异步的、快照可能比另一次保存旧（“较晚入队、较早执行”），
                    // 一旦按快照做 DELETE，就会把对方刚写进去的新消息删掉 —— 线上验过：
                    // 重启后整会话的活消息被清空（陈旧快照里消息少）。
                    // 从活动列表里“消失”的语义由 archived 标记表达（滚动窗口淘汰 → AppendArchive），
                    // 真要删就走 DeleteConversation（面板删会话）。
                    foreach (var m in c.Messages)
                    {
                        AppDatabase.Exec(conn, """
                            INSERT INTO messages(source_key, seq, role, text, time_unix, sender_name, sender_id,
                                                 qq_message_id, recalled, images, archived)
                            VALUES($key, $seq, $role, $text, $t, $sn, $sid, $mid, $rec, $img, 0)
                            ON CONFLICT(source_key, seq) DO UPDATE SET
                                role = excluded.role, text = excluded.text, time_unix = excluded.time_unix,
                                sender_name = excluded.sender_name, sender_id = excluded.sender_id,
                                qq_message_id = excluded.qq_message_id, recalled = excluded.recalled,
                                images = excluded.images
                            """,
                            ("$key", key), ("$seq", m.Seq), ("$role", m.Role), ("$text", m.Text),
                            ("$t", m.TimeUnix), ("$sn", m.SenderName), ("$sid", m.SenderId),
                            ("$mid", m.QqMessageId), ("$rec", m.Recalled ? 1 : 0),
                            ("$img", m.ImageUrls is { Count: > 0 } ? JsonSerializer.Serialize(m.ImageUrls) : null));
                    }
                }
            });
        }
        catch (Exception ex)
        {
            FileLog.Warn("Store", "保存会话失败：" + ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>删掉某会话（面板删会话时显式调用；平时保存不会删任何消息）。</summary>
    public void DeleteConversation(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn, "DELETE FROM messages WHERE source_key = $key", ("$key", sourceKey));
            AppDatabase.Exec(conn, "DELETE FROM conversations WHERE source_key = $key", ("$key", sourceKey));
        });
    }

    /// <summary>把被滚动窗口挤出去的消息写进归档（同一张表，archived=1）。</summary>
    public void AppendArchive(string sourceKey, IReadOnlyList<ChatMessage> evicted)
    {
        if (string.IsNullOrEmpty(sourceKey) || evicted.Count == 0)
        {
            return;
        }

        AppDatabase.Write(conn =>
        {
            foreach (var m in evicted)
            {
                AppDatabase.Exec(conn, """
                    INSERT INTO messages(source_key, seq, role, text, time_unix, sender_name, sender_id,
                                         qq_message_id, recalled, images, archived)
                    VALUES($key, $seq, $role, $text, $t, $sn, $sid, $mid, $rec, $img, 1)
                    ON CONFLICT(source_key, seq) DO UPDATE SET archived = 1
                    """,
                    ("$key", sourceKey), ("$seq", SeqOf(m)), ("$role", m.Role.ToString()), ("$text", m.Text),
                    ("$t", m.Timestamp.ToUnixTimeSeconds()), ("$sn", m.SenderName), ("$sid", m.SenderId),
                    ("$mid", m.QqMessageId), ("$rec", m.Recalled ? 1 : 0),
                    ("$img", m.ImageUrls is { Count: > 0 } ? JsonSerializer.Serialize(m.ImageUrls) : null));
            }
        });
    }

    /// <summary>读某会话的归档尾部（面板“翻旧账”用）。</summary>
    public List<ArchivedMessage> ReadArchive(string sourceKey, int limit)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return new List<ArchivedMessage>();
        }

        try
        {
            return AppDatabase.Query("""
                SELECT seq, role, text, time_unix, sender_name, sender_id, qq_message_id, recalled
                FROM messages
                WHERE source_key = $key AND archived = 1
                ORDER BY seq DESC
                LIMIT $limit
                """, r => new ArchivedMessage(
                    AppDatabase.Long(r, "seq"),
                    AppDatabase.Str(r, "role") ?? "Peer",
                    AppDatabase.Str(r, "text") ?? string.Empty,
                    AppDatabase.Long(r, "time_unix"),
                    AppDatabase.Str(r, "sender_name"),
                    AppDatabase.LongOrNull(r, "sender_id"),
                    AppDatabase.LongOrNull(r, "qq_message_id"),
                    AppDatabase.Bool(r, "recalled")),
                ("$key", sourceKey), ("$limit", Math.Clamp(limit, 1, 2000)));
        }
        catch (Exception ex)
        {
            FileLog.Warn("Store", "读取归档失败：" + ex.Message);
            return new List<ArchivedMessage>();
        }
    }

    /// <summary>归档总条数（面板显示用）。</summary>
    public long ArchiveCount(string sourceKey)
        => AppDatabase.Scalar<long>("SELECT COUNT(1) FROM messages WHERE source_key = $key AND archived = 1", ("$key", sourceKey));

    /// <summary>删掉某会话的全部消息（面板删会话时用）。</summary>
    public void DeleteMessages(string sourceKey)
        => AppDatabase.Write(conn => AppDatabase.Exec(conn, "DELETE FROM messages WHERE source_key = $key", ("$key", sourceKey)));

    /// <summary>ChatMessage 没有公开 Seq，这里从“内部序号”取；取不到时用时间戳兜底（负数段，避免撞车）。</summary>
    private static long SeqOf(ChatMessage m)
        => m.Seq != 0 ? m.Seq : -Math.Abs(m.Timestamp.ToUnixTimeMilliseconds());

    private static List<string>? ParseImages(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>一条归档消息（面板展示用）。</summary>
public readonly record struct ArchivedMessage(
    long Seq,
    string Role,
    string Text,
    long TimeUnix,
    string? SenderName,
    long? SenderId,
    long? QqMessageId,
    bool Recalled);

/// <summary>磁盘上的会话快照（与 UI 模型解耦）。</summary>
public sealed class ConversationRecord
{
    public string? Id { get; set; }

    /// <summary>QQ 映射键："private:{QQ号}" 或 "group:{群号}"。null = 本地会话。</summary>
    public string? SourceKey { get; set; }

    public string Kind { get; set; } = "LocalTest";

    public string Name { get; set; } = string.Empty;

    public string AvatarText { get; set; } = "?";

    public string? AvatarUrl { get; set; }

    public int AvatarIndex { get; set; }

    public List<MessageRecord> Messages { get; set; } = new();

    public long LastTimeUnix { get; set; }

    public int UnreadCount { get; set; }

    /// <summary>
    /// 下一个待分配的会话内序号（由 <c>_nextSeq</c> 导出）。
    /// 必须持久化：否则重启后序号从 0 重新计数，会同时搞坏两件事 ——
    ///   ① 画像的折叠边界（through_seq）比新序号大 → 记忆静默冻结；
    ///   ② (source_key, seq) 主键重号 → 新消息把老消息覆盖掉。
    /// </summary>
    public long NextSeq { get; set; }
}

public sealed class MessageRecord
{
    public string Role { get; set; } = "Peer";

    public string Text { get; set; } = string.Empty;

    public long TimeUnix { get; set; }

    public string? SenderName { get; set; }

    /// <summary>发送者 QQ 号（headless 新增：重启后仍能建人物档案）。</summary>
    public long? SenderId { get; set; }

    /// <summary>消息图片 URL（headless 新增：重启后仍可识图）。</summary>
    public List<string>? ImageUrls { get; set; }

    /// <summary>QQ 原始消息 ID（headless 新增：重启后历史去重仍生效）。</summary>
    public long? QqMessageId { get; set; }

    /// <summary>这条消息后来被撤回了（内容保留，只是标记）。</summary>
    public bool Recalled { get; set; }

    /// <summary>
    /// 会话内单调序号（同时被人物档案/画像用作“这条是否已在上下文里”的判据）。
    /// **必须持久化**：不存的话重启后序号从头计数，会小于画像里已记录的 ThroughSeq，
    /// 导致该成员的记忆静默停止更新（既不注入也不再摘要）。
    /// 可为负数 —— 拉取到的群历史会插到现有序号之前。
    /// </summary>
    public long Seq { get; set; }
}
