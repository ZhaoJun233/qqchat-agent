using System.Text;
using QQChatAgent.Services.Data;

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

    /// <summary>来源群号（0 = 私聊）。</summary>
    public long GroupId { get; set; }

    /// <summary>会话内单调序号（对应 ChatMessage.Seq）。旧数据为 0，回退到时间戳比较。</summary>
    public long Seq { get; set; }
}

/// <summary>
/// 人物档案（长期记忆）：SQLite 的 <c>members</c> / <c>member_messages</c> / <c>member_summaries</c> 三张表。
///
/// ⚠ 关键语义（与 JSON 版完全一致）：档案按 **会话（群/私聊）隔离** 读取。
/// 同一个人在 A 群的发言不会被喂进 B 群的上下文（避免串味与隐私泄露）。
///
/// 换成数据库之后的好处：以前一个人一个文件（几百人就是几百个小文件、每次追加都要重写整个 JSON），
/// 现在只插一行；面板查“某人在某群的画像”也是一句 SQL，而不是把全库读进内存再筛。
/// </summary>
public sealed class MemberProfileStore
{
    private const int MaxMessagesPerMember = 100; // 每个会话范围各留最近 100 条（跨群不再互相挤占）

    /// <summary>一次摘要最多折多少条（太多单次请求会过大；剩下的下一轮接着折）。</summary>
    private const int MaxMessagesPerSummaryRun = 60;

    /// <summary>会话范围的键：群用 "group:{群号}"，私聊用 "private"。</summary>
    public static string ScopeKey(long groupId) => groupId > 0 ? $"group:{groupId}" : "private";

    /// <summary>
    /// 找出需要（重新）摘要的候选：某会话范围里，自上次摘要之后又积累了足够多的新发言。
    /// </summary>
    public List<SummaryCandidate> FindSummarizable(int minNewMessages, int maxCandidates)
    {
        var result = new List<SummaryCandidate>();
        if (minNewMessages <= 0)
        {
            return result;
        }

        try
        {
            // 一次 SQL 找出“每个 (uid, group_id) 里，序号超过该范围画像边界的新发言条数”，
            // 够阈值的才继续取详情 —— 以前是“把所有档案读进内存再分组数数”。
            var candidates = AppDatabase.Query("""
                SELECT m.uid                       AS uid,
                       COALESCE(mm.name, '')       AS name,
                       m.group_id                  AS group_id,
                       COUNT(1)                    AS fresh_count
                FROM member_messages m
                LEFT JOIN members mm ON mm.uid = m.uid
                LEFT JOIN member_summaries s
                       ON s.uid = m.uid
                      AND s.scope = CASE WHEN m.group_id > 0 THEN 'group:' || m.group_id ELSE 'private' END
                WHERE m.seq > COALESCE(s.through_seq, -9223372036854775808)
                GROUP BY m.uid, m.group_id
                HAVING COUNT(1) >= $min
                ORDER BY fresh_count DESC
                LIMIT $max
                """, r => (
                    Uid: AppDatabase.Str(r, "uid") ?? string.Empty,
                    Name: AppDatabase.Str(r, "name") ?? string.Empty,
                    GroupId: AppDatabase.Long(r, "group_id")),
                ("$min", minNewMessages), ("$max", Math.Max(1, maxCandidates)));

            foreach (var c in candidates)
            {
                var scope = ScopeKey(c.GroupId);
                var throughSeq = AppDatabase.Scalar<long?>(
                    "SELECT through_seq FROM member_summaries WHERE uid = $uid AND scope = $scope",
                    ("$uid", c.Uid), ("$scope", scope)) ?? long.MinValue;

                var existingSummary = AppDatabase.Scalar<string>(
                    "SELECT text FROM member_summaries WHERE uid = $uid AND scope = $scope",
                    ("$uid", c.Uid), ("$scope", scope)) ?? string.Empty;

                // 一次只折最旧的一批（按序号升序），下一轮接着折剩下的：
                // 这样既不会单次请求过大，也不会永远跳过中间的一段。
                var batch = AppDatabase.Query("""
                    SELECT seq, text FROM member_messages
                    WHERE uid = $uid AND group_id = $gid AND seq > $through
                    ORDER BY seq
                    LIMIT $take
                    """, r => (Seq: AppDatabase.Long(r, "seq"), Text: AppDatabase.Str(r, "text") ?? string.Empty),
                    ("$uid", c.Uid), ("$gid", c.GroupId), ("$through", throughSeq), ("$take", MaxMessagesPerSummaryRun));

                if (batch.Count < minNewMessages)
                {
                    continue;
                }

                var folded = AppDatabase.Scalar<long?>(
                    "SELECT folded_count FROM member_summaries WHERE uid = $uid AND scope = $scope",
                    ("$uid", c.Uid), ("$scope", scope)) ?? 0;

                result.Add(new SummaryCandidate(
                    c.Uid,
                    c.Name,
                    scope,
                    c.GroupId,
                    batch.Select(b => b.Text).ToList(),
                    existingSummary,
                    batch.Max(b => b.Seq),
                    (int)folded + batch.Count));
            }
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "查询可摘要候选失败: " + ex.Message);
        }

        return result;
    }

    /// <summary>写入画像（摘要成功后调用）。折叠边界只前进不后退。</summary>
    public void ApplySummary(string uid, string scope, string text, long throughSeq, int foldedCount)
    {
        if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(scope))
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                INSERT INTO member_summaries(uid, scope, text, through_seq, updated_unix, folded_count)
                VALUES($uid, $scope, $text, $ts, $now, $fc)
                ON CONFLICT(uid, scope) DO UPDATE SET
                    text = excluded.text,
                    through_seq = MAX(member_summaries.through_seq, excluded.through_seq),
                    updated_unix = excluded.updated_unix,
                    folded_count = MAX(member_summaries.folded_count, excluded.folded_count)
                """,
                ("$uid", uid), ("$scope", scope), ("$text", text), ("$ts", throughSeq),
                ("$now", now), ("$fc", foldedCount)));
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "画像落库失败: " + ex.Message);
        }
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

    /// <summary>记录某 QQ 号的一条发言。</summary>
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

        try
        {
            AppDatabase.Write(conn =>
            {
                AppDatabase.Exec(conn, """
                    INSERT INTO members(uid, name, updated_unix) VALUES($uid, $name, $now)
                    ON CONFLICT(uid) DO UPDATE SET
                        name = CASE WHEN excluded.name <> '' AND excluded.name <> members.name THEN excluded.name ELSE members.name END,
                        updated_unix = excluded.updated_unix
                    """,
                    ("$uid", uid), ("$name", name), ("$now", DateTimeOffset.Now.ToUnixTimeSeconds()));

                // seq = 0 的调用（老调用方/测试）用“本会话范围内最小序号 - 1”保证唯一且不撞车
                if (seq == 0)
                {
                    var min = AppDatabase.Scalar<long?>(
                        "SELECT MIN(seq) FROM member_messages WHERE uid = $uid AND group_id = $gid",
                        ("$uid", uid), ("$gid", groupId));
                    seq = (min ?? 0) - 1;
                }

                AppDatabase.Exec(conn, """
                    INSERT INTO member_messages(uid, seq, group_id, group_name, text, time_unix)
                    VALUES($uid, $seq, $gid, $gn, $text, $t)
                    ON CONFLICT(uid, group_id, seq) DO NOTHING
                    """,
                    ("$uid", uid), ("$seq", seq), ("$gid", groupId), ("$gn", groupName ?? string.Empty),
                    ("$text", trimmed), ("$t", time.ToUnixTimeSeconds()));

                // 超出上限：**按会话范围各自**淘汰最早的（一个人活跃在多个群时，热闹的群不该把其它群挤干净）
                AppDatabase.Exec(conn, """
                    DELETE FROM member_messages
                    WHERE uid = $uid AND group_id = $gid AND seq NOT IN (
                        SELECT seq FROM member_messages
                        WHERE uid = $uid AND group_id = $gid
                        ORDER BY seq DESC
                        LIMIT $keep
                    )
                    """,
                    ("$uid", uid), ("$gid", groupId), ("$keep", MaxMessagesPerMember));
            });
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "写入发言失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 生成某 QQ 号在**指定会话范围内**的档案摘要（角色卡片）。
    /// </summary>
    /// <param name="uid">成员 QQ 号。</param>
    /// <param name="scopeGroupId">当前会话的群号；0 = 私聊。</param>
    /// <param name="limit">最多取最近几条。</param>
    /// <param name="beforeSeq">只取序号小于它的历史（传当前上下文最早一条的 Seq）——精确去重，避免与上下文重复。</param>
    /// <param name="beforeUnix">旧数据的回退判据（无 Seq 时用时间戳）。</param>
    /// <param name="allScopes">true = 不过滤会话范围（仅供面板展示，**不用于模型注入**）。</param>
    public string GetProfileSummary(
        string uid,
        long scopeGroupId = 0,
        int limit = 12,
        long beforeSeq = long.MaxValue,
        long beforeUnix = long.MaxValue,
        bool allScopes = false)
    {
        if (string.IsNullOrWhiteSpace(uid))
        {
            return string.Empty;
        }

        try
        {
            var name = AppDatabase.Scalar<string>("SELECT name FROM members WHERE uid = $uid", ("$uid", uid)) ?? uid;

            var summaries = allScopes
                ? AppDatabase.Query(
                    "SELECT scope, text FROM member_summaries WHERE uid = $uid AND text <> ''",
                    r => (Scope: AppDatabase.Str(r, "scope") ?? string.Empty, Text: AppDatabase.Str(r, "text") ?? string.Empty),
                    ("$uid", uid))
                : AppDatabase.Query(
                    "SELECT scope, text FROM member_summaries WHERE uid = $uid AND scope = $scope AND text <> ''",
                    r => (Scope: AppDatabase.Str(r, "scope") ?? string.Empty, Text: AppDatabase.Str(r, "text") ?? string.Empty),
                    ("$uid", uid), ("$scope", ScopeKey(scopeGroupId)));

            var scopedScope = ScopeKey(scopeGroupId);
            var summary = summaries.FirstOrDefault(s => s.Scope == scopedScope);
            var hasSummary = !string.IsNullOrWhiteSpace(summary.Text);

            // 画像已折叠到的序号：比它更早的原文不用再带（无画像时用 long.MinValue —— 补录的群历史序号是负数）
            var foldedThrough = AppDatabase.Scalar<long?>(
                "SELECT through_seq FROM member_summaries WHERE uid = $uid AND scope = $scope",
                ("$uid", uid), ("$scope", scopedScope)) ?? long.MinValue;

            var older = new List<MemberMessageRecord>();
            if (limit > 0)
            {
                older = AppDatabase.Query("""
                    SELECT text, time_unix, seq FROM member_messages
                    WHERE uid = $uid
                      AND (($allScopes = 1) OR ($gid > 0 AND group_id = $gid) OR ($gid <= 0 AND group_id <= 0 AND (group_name IS NULL OR group_name = '')))
                      AND seq > $through
                      AND ((seq <> 0 AND seq < $beforeSeq) OR (seq = 0 AND time_unix < $beforeUnix))
                    ORDER BY seq DESC
                    LIMIT $limit
                    """, r => new MemberMessageRecord
                {
                    Text = AppDatabase.Str(r, "text") ?? string.Empty,
                    TimeUnix = AppDatabase.Long(r, "time_unix"),
                    Seq = AppDatabase.Long(r, "seq")
                },
                    ("$uid", uid), ("$allScopes", allScopes ? 1 : 0), ("$gid", scopeGroupId),
                    ("$through", foldedThrough), ("$beforeSeq", beforeSeq), ("$beforeUnix", beforeUnix),
                    ("$limit", limit));

                older.Reverse(); // 查询是“最新在前”，展示要按时间正序
            }

            if (!hasSummary && older.Count == 0)
            {
                return string.Empty;
            }

            var header = $"{name}（QQ:{uid}）";
            var sb = new StringBuilder();

            // 第一段：长期画像（由模型压缩而来，信息密度高）
            if (hasSummary)
            {
                sb.Append(header).Append(" · 画像：").Append(summary.Text.Trim());
            }

            // 面板视图：列出各会话的画像，并标明来源，方便人工核对隔离是否生效
            if (allScopes)
            {
                foreach (var other in summaries.Where(s => s.Scope != scopedScope))
                {
                    sb.Append('\n').Append(header)
                      .Append(" · 画像[").Append(DescribeScope(other.Scope)).Append("]：")
                      .Append(other.Text.Trim());
                }
            }

            // 第二段：画像之后、且当前上下文看不到的原文（保证不重复注入，也不丢细节）
            if (limit > 0 && older.Count > 0)
            {
                var lines = older.Select(m =>
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
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "读取档案失败: " + ex.Message);
            return string.Empty;
        }
    }

    /// <summary>面板用：某人档案的原始条目数 / 画像份数（可观测）。</summary>
    public (int Messages, int Summaries) Stats(string uid)
        => (AppDatabase.Scalar<int>("SELECT COUNT(1) FROM member_messages WHERE uid = $uid", ("$uid", uid)),
            AppDatabase.Scalar<int>("SELECT COUNT(1) FROM member_summaries WHERE uid = $uid", ("$uid", uid)));

    /// <summary>库里一共多少人（面板/日志用）。</summary>
    public int Count => AppDatabase.Scalar<int>("SELECT COUNT(1) FROM members");

    /// <summary>把会话范围键转成人能看懂的描述。</summary>
    private static string DescribeScope(string scope)
        => scope.StartsWith("group:", StringComparison.Ordinal) ? "群 " + scope[6..] : "私聊";
}
