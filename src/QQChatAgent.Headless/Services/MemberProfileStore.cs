using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace QQChatAgent.Services;

/// <summary>单个 QQ 用户的档案记录。</summary>
public sealed class MemberProfileRecord
{
    public string Uid { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<MemberMessageRecord> Messages { get; set; } = new();

    /// <summary>各会话范围的长期画像（每个群/私聊一份，不混用）。</summary>
    public List<MemberSummary> Summaries { get; set; } = new();
}

/// <summary>
/// 一个人在某个会话里的长期画像：由模型把历史发言压缩而成。
/// 价值：把“几十条原文”换成“一段画像”，token 降一个数量级，信息密度反而更高。
/// </summary>
public sealed class MemberSummary
{
    /// <summary>会话范围："group:{群号}" 或 "private"。</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>画像正文。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>已折叠到的消息序号：序号不大于它的发言已被写进画像，不再重复注入。</summary>
    public long ThroughSeq { get; set; }

    /// <summary>上次摘要时间（Unix 秒）。</summary>
    public long UpdatedUnix { get; set; }

    /// <summary>已折叠的消息条数（可观测）。</summary>
    public int FoldedCount { get; set; }
}

public sealed class MemberMessageRecord
{
    public string Text { get; set; } = string.Empty;

    public long TimeUnix { get; set; }

    public string GroupName { get; set; } = string.Empty;

    /// <summary>来源群号（0 = 私聊）。旧数据缺失此字段时为 0，靠 GroupName 兼容匹配。</summary>
    public long GroupId { get; set; }

    /// <summary>会话内单调序号（对应 ChatMessage.Seq）。
    /// 用于精确判断“这条是否已在当前上下文里”——时间戳只有秒级精度，同一秒多条就会误判。
    /// 旧数据为 0，回退到时间戳比较。</summary>
    public long Seq { get; set; }
}

/// <summary>
/// 群友/好友人物档案库：每个 QQ 号一个独立 JSON 文件（member_profiles/{QQ号}.json），
/// 按 QQ 号直接定位读写。接收消息时给对应 QQ 号追加发言（满 100 条移除最老），
/// 请求模型时按在场 QQ 号读取档案（角色卡片），帮助模型认识说话者。
///
/// ⚠ 关键语义：档案按 **会话（群/私聊）隔离** 读取。
/// 同一个人在 A 群的发言不会被喂进 B 群的上下文（避免串味与隐私泄露）。
/// </summary>
public sealed class MemberProfileStore
{
    private const int MaxMessagesPerMember = 100; // 每个会话范围各留最近 100 条（跨群不再互相挤占）

    /// <summary>单次画像请求最多喂给模型的历史条数（防止一次请求过大）。</summary>
    private const int MaxMessagesPerSummaryRun = 60;

    // 不转义非 ASCII：人物档案文件应可直接阅读
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Dictionary<string, MemberProfileRecord> _cache = new();
    private readonly HashSet<string> _dirty = new();
    private readonly string _dir;
    private DateTime _lastFlush;

    public MemberProfileStore(string? baseDir = null)
    {
        baseDir ??= AppPaths.DataDir;
        _dir = Path.Combine(baseDir, "member_profiles");
        Directory.CreateDirectory(_dir);
        MigrateLegacyFile(Path.Combine(baseDir, "member_profiles.json"));
        LoadAll();
    }

    /// <summary>会话范围的键：群用 "group:{群号}"，私聊用 "private"。</summary>
    public static string ScopeKey(long groupId) => groupId > 0 ? $"group:{groupId}" : "private";

    /// <summary>
    /// 找出需要（重新）摘要的候选：某会话范围里，自上次摘要之后又积累了足够多的新发言。
    /// 返回 (uid, 昵称, scopeKey, groupId, 新发言文本, 现有画像）。
    /// </summary>
    public List<SummaryCandidate> FindSummarizable(int minNewMessages, int maxCandidates)
    {
        var result = new List<SummaryCandidate>();
        List<string> uids;
        lock (_gate)
        {
            uids = _cache.Keys.ToList();
        }

        foreach (var uid in uids)
        {
            // 锁内快照：Append 会在另一个线程上改 Messages（Add/RemoveRange），
            // 锁外直接枚举会撞上“集合已被修改”而静默丢掉这一轮画像。
            var record = LoadSnapshot(uid);
            if (record is null || record.Messages.Count == 0)
            {
                continue;
            }

            // 按会话分组，逐范围看新增
            foreach (var group in record.Messages.GroupBy(m => m.GroupId))
            {
                var scope = ScopeKey(group.Key);
                var summary = record.Summaries.FirstOrDefault(s => s.Scope == scope);

                // 没有画像时用 long.MinValue：补录的群历史序号是**负数**，
                // 若用 0 作下界会把它们全部排除在外。
                var throughSeq = summary?.ThroughSeq ?? long.MinValue;

                var fresh = group.Where(m => m.Seq > throughSeq).OrderBy(m => m.Seq).ToList();
                if (fresh.Count < minNewMessages)
                {
                    continue;
                }

                // 一次只折最旧的一批（按序号升序），下一轮接着折剩下的：
                // 这样既不会单次请求过大，也不会永远跳过中间的一段。
                var batch = fresh.Take(MaxMessagesPerSummaryRun).ToList();

                result.Add(new SummaryCandidate(
                    uid,
                    record.Name,
                    scope,
                    group.Key,
                    batch.Select(m => m.Text).ToList(),
                    summary?.Text ?? string.Empty,
                    batch.Max(m => m.Seq),
                    (summary?.FoldedCount ?? 0) + batch.Count));

                if (result.Count >= maxCandidates)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>写入画像（摘要成功后调用），并落盘。</summary>
    public void ApplySummary(string uid, string scope, string text, long throughSeq, int foldedCount)
    {
        lock (_gate)
        {
            var record = LoadLockedCore(uid);
            if (record is null)
            {
                return;
            }

            var existing = record.Summaries.FirstOrDefault(s => s.Scope == scope);
            if (existing is null)
            {
                record.Summaries.Add(new MemberSummary
                {
                    Scope = scope,
                    Text = text,
                    ThroughSeq = throughSeq,
                    UpdatedUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    FoldedCount = foldedCount
                });
            }
            else
            {
                existing.Text = text;

                // 折叠边界只允许向前推进，不允许回退（回退会导致同一批发言被重复折叠）
                existing.ThroughSeq = Math.Max(existing.ThroughSeq, throughSeq);
                existing.UpdatedUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
                existing.FoldedCount = Math.Max(existing.FoldedCount, foldedCount);
            }
        }

        WriteNow(uid);
    }

    /// <summary>立即写盘指定账号（摘要后需要立即可见）。</summary>
    private void WriteNow(string uid)
    {
        try
        {
            MemberProfileRecord? profile;
            lock (_gate)
            {
                _cache.TryGetValue(uid, out profile);
            }

            if (profile is null)
            {
                return;
            }

            var path = FileOf(uid);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(profile, ReadableJson));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "画像落盘失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 读档案快照：在锁内深拷一份，避免锁外遍历时被 Append 改动（“集合已被修改”）。
    /// </summary>
    private MemberProfileRecord? LoadSnapshot(string uid)
    {
        lock (_gate)
        {
            var live = LoadLockedCore(uid);
            if (live is null)
            {
                return null;
            }

            return new MemberProfileRecord
            {
                Uid = live.Uid,
                Name = live.Name,
                Messages = live.Messages.ToList(),
                Summaries = live.Summaries
                    .Select(s => new MemberSummary
                    {
                        Scope = s.Scope,
                        Text = s.Text,
                        ThroughSeq = s.ThroughSeq,
                        UpdatedUnix = s.UpdatedUnix,
                        FoldedCount = s.FoldedCount
                    })
                    .ToList()
            };
        }
    }

    /// <summary>读档案（不存在则尝试从磁盘读并缓存）。调用方必须已持锁。</summary>
    private MemberProfileRecord? LoadLockedCore(string uid)
    {
        if (_cache.TryGetValue(uid, out var hit))
        {
            return hit;
        }

        var loaded = TryRead(uid);
        if (loaded is not null)
        {
            _cache[uid] = loaded;
        }

        return loaded;
    }

    /// <summary>摘要候选项。</summary>
    public readonly record struct SummaryCandidate(
        string Uid,
        string Name,
        string Scope,
        long GroupId,
        IReadOnlyList<string> NewMessages,
        string ExistingSummary,
        long ThroughSeq,
        int FoldedCount);

    /// <summary>记录某 QQ 号的一条发言（无档案则创建专属文件，有则追加）。</summary>
    /// <param name="groupId">来源群号；0 = 私聊。</param>
    /// <param name="seq">会话内单调序号（可选，用于精确去重）。</param>
    public void Append(string uid, string name, string text, DateTimeOffset time, string? groupName, long groupId = 0, long seq = 0)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > 200)
        {
            trimmed = trimmed[..200] + "…";
        }

        name = (name ?? string.Empty).Trim(); // 群名片可能带 \t 等控制字符

        lock (_gate)
        {
            if (!_cache.TryGetValue(uid, out var profile))
            {
                profile = new MemberProfileRecord { Uid = uid, Name = name };
                _cache[uid] = profile;
            }

            if (!string.IsNullOrWhiteSpace(name) && !profile.Name.Equals(name, StringComparison.Ordinal))
            {
                profile.Name = name; // 更新最新昵称/群名片
            }

            profile.Messages.Add(new MemberMessageRecord
            {
                Text = trimmed,
                TimeUnix = time.ToUnixTimeSeconds(),
                GroupName = groupName ?? string.Empty,
                GroupId = groupId,
                Seq = seq
            });

            // 超出上限：**按会话范围各自**淘汰最早的（以前是全局 100 条，
            // 一个人活跃在多个群时，热闹的群会把其它群的历史挤干净）
            TrimToLimitLocked(profile);

            _dirty.Add(uid);
        }

        _ = FlushDebouncedAsync();
    }

    /// <summary>每个会话范围各保留最近 MaxMessagesPerMember 条，其余丢弃。调用方必须已持锁。</summary>
    private static void TrimToLimitLocked(MemberProfileRecord profile)
    {
        if (profile.Messages.Count <= MaxMessagesPerMember)
        {
            return; // 总量都没超，不用说
        }

        var kept = new Dictionary<long, int>();
        var survivors = new List<MemberMessageRecord>(profile.Messages.Count);

        // 从新往旧扫，每个范围留够配额
        for (var i = profile.Messages.Count - 1; i >= 0; i--)
        {
            var message = profile.Messages[i];
            kept.TryGetValue(message.GroupId, out var count);
            if (count >= MaxMessagesPerMember)
            {
                continue;
            }

            kept[message.GroupId] = count + 1;
            survivors.Add(message);
        }

        if (survivors.Count == profile.Messages.Count)
        {
            return;
        }

        survivors.Reverse();
        profile.Messages.Clear();
        profile.Messages.AddRange(survivors);
    }

    /// <summary>
    /// 生成某 QQ 号在**指定会话范围内**的档案摘要（角色卡片）。
    /// </summary>
    /// <param name="uid">成员 QQ 号。</param>
    /// <param name="scopeGroupId">当前会话的群号；0 = 私聊。</param>
    /// <param name="scopeGroupName">当前群名（用于兼容缺 GroupId 的旧数据）。</param>
    /// <param name="limit">最多取最近几条。</param>
    /// <param name="beforeSeq">只取序号小于它的历史（传当前上下文最早一条的 Seq）——精确去重，避免与上下文重复。</param>
    /// <param name="beforeUnix">旧数据的回退判据（无 Seq 时用时间戳）。</param>
    /// <param name="allScopes">
    /// true = 不过滤会话范围（仅供面板展示，**不用于模型注入**）。
    /// 默认 false：只取当前会话，避免 A 群的话被喂进 B 群。
    /// </param>
    public string GetProfileSummary(
        string uid,
        long scopeGroupId = 0,
        int limit = 12,
        long beforeSeq = long.MaxValue,
        long beforeUnix = long.MaxValue,
        bool allScopes = false)
    {
        // 锁内快照：调用方可能在回复线程，而 Append 在接收线程改 Messages
        var profile = LoadSnapshot(uid);
        if (profile is null || profile.Messages.Count == 0)
        {
            return string.Empty;
        }

        var summary = allScopes
            ? null
            : profile.Summaries.FirstOrDefault(s => s.Scope == ScopeKey(scopeGroupId));

        // 画像已折叠到的序号：比它更早的原文不用再带。
        // 无画像时用 long.MinValue：补录的群历史序号是负数，用 0 作下界会全部漏掉。
        var foldedThrough = summary?.ThroughSeq ?? long.MinValue;

        var scoped = profile.Messages
            .Where(m => allScopes || InScope(m, scopeGroupId))
            .ToList();

        var older = scoped
            .Where(m => m.Seq > foldedThrough && IsOlderThan(m, beforeSeq, beforeUnix))
            .ToList();

        var hasSummary = !string.IsNullOrWhiteSpace(summary?.Text);
        if (!hasSummary && (older.Count == 0 || limit <= 0))
        {
            return string.Empty;
        }

        var header = $"{profile.Name}（QQ:{uid}）";
        var sb = new StringBuilder();

        // 第一段：长期画像（由模型压缩而来，信息密度高）
        if (hasSummary)
        {
            sb.Append(header).Append(" · 画像：").Append(summary!.Text.Trim());
        }

        // 面板视图：列出各会话的画像，并标明来源，方便人工核对隔离是否生效
        if (allScopes)
        {
            foreach (var other in profile.Summaries.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
            {
                sb.Append('\n').Append(header)
                  .Append(" · 画像[").Append(DescribeScope(other.Scope)).Append("]：")
                  .Append(other.Text.Trim());
            }
        }

        // 第二段：画像之后、且当前上下文看不到的原文（保证不重复注入，也不丢细节）
        if (limit > 0 && older.Count > 0)
        {
            var lines = older.TakeLast(limit).Select(m =>
                $"  {DateTimeOffset.FromUnixTimeSeconds(m.TimeUnix).ToLocalTime():MM-dd HH:mm} “{m.Text}”");

            if (sb.Length > 0)
            {
                sb.Append('\n').Append(header).Append(" · 之后的发言：");
            }
            else
            {
                sb.Append(header).Append(scopeGroupId > 0 ? " · 本群更早的发言：" : " · 私聊更早的发言：");
            }

            sb.Append('\n').Append(string.Join("\n", lines));
        }

        return sb.ToString();
    }

    /// <summary>把会话范围键转成人能看懂的描述。</summary>
    private static string DescribeScope(string scope)
        => scope.StartsWith("group:", StringComparison.Ordinal) ? "群 " + scope[6..] : "私聊";

    /// <summary>记录是否早于当前上下文。用会话内单调序号精确判定（可为负数）；
    /// 旧数据无序号（0）则回退到时间戳。</summary>
    private static bool IsOlderThan(MemberMessageRecord m, long beforeSeq, long beforeUnix)
        => m.Seq != 0 ? m.Seq < beforeSeq : m.TimeUnix < beforeUnix;

    /// <summary>
    /// 记录是否属于指定会话范围。按 GroupId 精确匹配。
    ///
    /// 旧数据（没有 GroupId、只有 GroupName）**一律视为不属于任何群范围**，直接丢弃。
    /// 理由：群名不唯一（“相亲相爱一家人”遍地都是），按群名兜底会把 A 群的话
    /// 当作 B 群的历史注入 —— 正是我们要防的串味。宁可不显示，也不能串群。
    /// 这些旧记录会随着每会话 100 条的滚动窗口自然淘汰。
    /// </summary>
    private static bool InScope(MemberMessageRecord m, long scopeGroupId)
        => scopeGroupId <= 0
            ? m.GroupId <= 0 && string.IsNullOrWhiteSpace(m.GroupName) // 私聊
            : m.GroupId == scopeGroupId;

    // ---------- 内部 ----------

    private string FileOf(string uid) => Path.Combine(_dir, uid + ".json");

    private MemberProfileRecord? TryRead(string uid)
    {
        try
        {
            var path = FileOf(uid);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<MemberProfileRecord>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void LoadAll()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_dir, "*.json"))
            {
                var uid = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(uid) || _cache.ContainsKey(uid))
                {
                    continue;
                }

                var profile = TryRead(uid);
                if (profile is not null)
                {
                    _cache[uid] = profile;
                }
            }
        }
        catch
        {
            // 目录损坏则从空开始
        }
    }

    /// <summary>旧版单文件 member_profiles.json → 拆分为每号一个文件。</summary>
    private void MigrateLegacyFile(string legacyPath)
    {
        try
        {
            if (!File.Exists(legacyPath) || Directory.GetFiles(_dir, "*.json").Length > 0)
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<MemberProfileRecord>>(File.ReadAllText(legacyPath));
            if (list is null)
            {
                return;
            }

            foreach (var p in list)
            {
                if (string.IsNullOrWhiteSpace(p.Uid))
                {
                    continue;
                }

                File.WriteAllText(FileOf(p.Uid), JsonSerializer.Serialize(p, ReadableJson));
            }

            File.Delete(legacyPath);
            FileLog.Write("Profiles", $"已迁移旧档案 {list.Count} 个为独立文件");
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "旧档案迁移失败: " + ex.Message);
        }
    }

    /// <summary>节流落盘：2 秒内多次追加合并，逐号写各自文件。</summary>
    private async Task FlushDebouncedAsync()
    {
        await _flushLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if ((now - _lastFlush).TotalSeconds < 2)
            {
                await Task.Delay(2000);
            }

            _lastFlush = DateTime.UtcNow;
            List<string> uids;
            lock (_gate)
            {
                uids = _dirty.ToList();
                _dirty.Clear();
            }

            foreach (var uid in uids)
            {
                try
                {
                    MemberProfileRecord? profile;
                    lock (_gate)
                    {
                        _cache.TryGetValue(uid, out profile);
                    }

                    if (profile is null)
                    {
                        continue;
                    }

                    var path = FileOf(uid);
                    var temp = path + ".tmp";
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(profile, ReadableJson));
                    File.Move(temp, path, overwrite: true);
                }
                catch
                {
                    // 单个账号写盘失败不影响其他
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }
}