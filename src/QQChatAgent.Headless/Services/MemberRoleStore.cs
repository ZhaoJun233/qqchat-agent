using Microsoft.Data.Sqlite;
using QQChatAgent.Services.Data;

namespace QQChatAgent.Services;

/// <summary>
/// 群成员身份（群主 / 管理员 / 成员 + 群头衔）的存放处。
///
/// 为什么要它：模型在群里说话时应该知道“谁说了算、这个人是什么头衔” ——
/// 群主与管理员能踢人、能撤回，头衔往往是本人给自己写的梗（“传说中的群主”）。
/// 这些信息以前完全没进提示词，机器人只能从语气猜。
///
/// 两个来源，优先级从高到低：
///   ① 消息事件里的 <c>sender.role</c>（OneBot 群里每条消息都带，零成本）；
///   ② 协议端动作 <c>get_group_member_info</c>（能拿到**群头衔**，要主动去问，所以按需取、带缓存）。
///
/// 为什么落库而不是只放内存：重启后仍然要知道谁是群主；而且头衔这种东西变化很慢，
/// 存下来就不用每次发言都去问协议端（那会把协议端打爆）。
/// </summary>
public sealed class MemberRoleStore
{
    /// <summary>身份的新鲜度：超过这个时间才值得再去问一次协议端。</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(3);

    private readonly Action<string> _log;

    public MemberRoleStore(Action<string> log) => _log = log;

    /// <summary>一条身份记录。</summary>
    public sealed record RoleRecord(
        string Uid, long GroupId, string Role, string Title, string Name, bool TitleChecked, DateTimeOffset UpdatedAt)
    {
        /// <summary>群主。</summary>
        public bool IsOwner => Role == "owner";

        /// <summary>管理员。</summary>
        public bool IsAdmin => Role == "admin";

        /// <summary>有没有值得写进提示词的东西（群主/管理员/自定义头衔）。</summary>
        public bool IsNotable => IsOwner || IsAdmin || !string.IsNullOrWhiteSpace(Title);

        /// <summary>人话里的身份：群主 / 管理员 / （空）。</summary>
        public string RoleLabel => IsOwner ? "群主" : IsAdmin ? "管理员" : string.Empty;
    }

    /// <summary>
    /// 记下一条身份（来自消息事件或协议端）。
    /// 只在“真的有东西变了”时才写库 —— 群里每条消息都会走到这里，不能每条都写一次。
    /// </summary>
    /// <param name="titleChecked">
    /// 这次是不是“已经问过头衔了”（来自 get_group_member_info = true；来自消息事件 = false）。
    /// ⚠ 这个位很重要：事件里只有 role，如果因为“刚写过、很新鲜”就不再去问，头衔就永远拿不到了（踩过）。
    /// </param>
    public void Remember(string uid, long groupId, string? role, string? title, string? name, bool titleChecked = false)
    {
        if (string.IsNullOrWhiteSpace(uid) || groupId == 0 || !AppDatabase.IsReady)
        {
            return;
        }

        var normalizedRole = NormalizeRole(role);
        var normalizedTitle = (title ?? string.Empty).Trim();
        var normalizedName = (name ?? string.Empty).Trim();

        try
        {
            var existing = Find(uid, groupId);
            if (existing is not null &&
                existing.Role == normalizedRole &&
                existing.Title == normalizedTitle &&
                existing.Name == normalizedName &&
                existing.TitleChecked == titleChecked)
            {
                // 内容没变，但可以把“见过”的时间推后一点：下一次就不用再去问协议端了。
                // 只在超过半天没更新时才写，避免每条消息一次写库。
                if (DateTimeOffset.Now - existing.UpdatedAt < TimeSpan.FromHours(12))
                {
                    return;
                }
            }
            else if (existing is not null && normalizedRole.Length == 0 && normalizedTitle.Length == 0)
            {
                return;   // 新来的信息是空的（事件没带 role），别把已知的覆盖掉
            }

            AppDatabase.Write(conn => AppDatabase.Exec(conn, """
                INSERT INTO member_roles(uid, group_id, role, title, name, title_checked, updated_unix)
                VALUES($uid, $gid, $role, $title, $name, $checked, $now)
                ON CONFLICT(uid, group_id) DO UPDATE SET
                  role = excluded.role, title = excluded.title, name = excluded.name,
                  title_checked = excluded.title_checked, updated_unix = excluded.updated_unix
                """,
                ("$uid", uid), ("$gid", groupId), ("$role", normalizedRole),
                ("$title", normalizedTitle), ("$name", normalizedName),
                ("$checked", titleChecked ? 1 : 0),
                ("$now", DateTimeOffset.Now.ToUnixTimeSeconds())));
        }
        catch (Exception ex)
        {
            _log($"[Role] 身份写入失败（不影响聊天）：{ex.Message}");
        }
    }

    /// <summary>读一个人的身份（没有返回 null）。</summary>
    public RoleRecord? Find(string uid, long groupId)
    {
        if (!AppDatabase.IsReady)
        {
            return null;
        }

        try
        {
            var rows = AppDatabase.Query("""
                SELECT uid, group_id, role, title, name, title_checked, updated_unix FROM member_roles
                WHERE uid = $uid AND group_id = $gid
                """, Map, ("$uid", uid), ("$gid", groupId));
            return rows.Count > 0 ? rows[0] : null;
        }
        catch (Exception ex)
        {
            _log($"[Role] 身份读取失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 这个人在这个群里的身份是不是“该去问一下协议端了”：没记录、头衔还没问过、或记录超过 <see cref="FreshFor"/>。
    /// ⚠ “头衔还没问过”这一条不能省：消息事件只带 role，不补这一步就永远看不到自定义头衔（踩过：
    /// 写库时把 updated_unix 当成现在，结果“刚写过、很新鲜”→ 永远不再去问）。
    /// </summary>
    public bool NeedsRefresh(string uid, long groupId)
    {
        var existing = Find(uid, groupId);
        if (existing is null || !existing.TitleChecked)
        {
            return true;
        }

        return existing.UpdatedAt + FreshFor < DateTimeOffset.Now;
    }

    /// <summary>某群里已知的“值得一提”的身份（群主、管理员、有头衔的人），群主排最前。</summary>
    public List<RoleRecord> NotableForGroup(long groupId, int limit = 14)
    {
        if (!AppDatabase.IsReady || groupId == 0 || limit <= 0)
        {
            return new List<RoleRecord>();
        }

        try
        {
            return AppDatabase.Query("""
                SELECT uid, group_id, role, title, name, title_checked, updated_unix FROM member_roles
                WHERE group_id = $gid
                  AND (role IN ('owner', 'admin') OR (title IS NOT NULL AND title <> ''))
                ORDER BY CASE role WHEN 'owner' THEN 0 WHEN 'admin' THEN 1 ELSE 2 END, updated_unix DESC
                LIMIT $limit
                """, Map, ("$gid", groupId), ("$limit", limit));
        }
        catch (Exception ex)
        {
            _log($"[Role] 身份列表读取失败：{ex.Message}");
            return new List<RoleRecord>();
        }
    }

    private static RoleRecord Map(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetInt64(5) != 0, DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)));

    /// <summary>把提示词里那一段拼出来（没有值得说的就返回 null，不占 token）。</summary>
    /// <param name="people">拼进去的人数（供日志用）。</param>
    public string? DescribeForPrompt(long groupId, int limit, out int people)
    {
        var notable = NotableForGroup(groupId, limit);
        people = notable.Count;
        if (notable.Count == 0)
        {
            return null;
        }

        var lines = new List<string>();
        var owners = notable.Where(r => r.IsOwner).ToList();
        var admins = notable.Where(r => r.IsAdmin).ToList();
        var titled = notable.Where(r => !r.IsOwner && !r.IsAdmin && !string.IsNullOrWhiteSpace(r.Title)).ToList();

        if (owners.Count > 0)
        {
            lines.Add("群主：" + string.Join("、", owners.Select(r => Format(r, withTitle: false))));
        }

        if (admins.Count > 0)
        {
            lines.Add("管理员：" + string.Join("、", admins.Select(r => Format(r, withTitle: false))));
        }

        if (titled.Count > 0)
        {
            lines.Add("群头衔：" + string.Join("；", titled.Select(r => Format(r, withTitle: true))));
        }

        return string.Join("\n", lines);
    }

    /// <summary>「昵称（QQ 号）」；要头衔时再跟上头衔。</summary>
    private static string Format(RoleRecord r, bool withTitle)
    {
        var name = string.IsNullOrWhiteSpace(r.Name) ? r.Uid : r.Name;
        var text = $"{name}（{r.Uid}）";
        return withTitle && !string.IsNullOrWhiteSpace(r.Title) ? $"{text}={r.Title}" : text;
    }

    /// <summary>OneBot 的 role 只有 owner / admin / member 三种，其它写法一律当普通成员。</summary>
    private static string NormalizeRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "owner" => "owner",
        "admin" => "admin",
        _ => "member"
    };
}
