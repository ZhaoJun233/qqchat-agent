using System.IO;
using System.Text;
using System.Text.Json;
using QQChatAgent.Services.Music;
using QQChatAgent.Services.Stickers;

namespace QQChatAgent.Services.Data;

/// <summary>
/// 把老版本留下的 JSON 数据一次性搬进 SQLite（见 <see cref="AppDatabase"/>）。
///
/// 三条纪律：
///   ① **不丢数据**：导入成功后，旧的 json/jsonl 会被**移到** `{RuntimeRoot}/legacy-json/` 留档，
///      而不是删除 —— 万一导入逻辑有 bug，人工还能翻出来；
///   ② **幂等**：靠 meta 里的标记 + “目标表为空才导”，重复启动不会导两遍、也不会把面板刚改的东西覆盖回去；
///   ③ **不阻塞启动**：任何一步失败都只记日志，机器人照常起（拿不到旧数据也比起不来强）。
/// </summary>
public static class LegacyJsonImporter
{
    private const string DoneKey = "legacy_import_done";
    private const string LegacyDirName = "legacy-json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>需要时执行导入（AppDatabase.Initialize() 之后、读配置之前调用）。</summary>
    public static void ImportIfNeeded()
    {
        try
        {
            if (AppDatabase.HasMeta(DoneKey))
            {
                return;
            }

            var moved = new List<string>();
            var counts = new List<string>();

            ImportSettings(moved, counts);
            ImportSecrets(moved, counts);
            ImportConversations(moved, counts);
            ImportArchive(counts);
            ImportMemberProfiles(moved, counts);
            ImportMood(moved, counts);
            ImportHeardSongs(moved, counts);
            ImportStickers(moved, counts);

            AppDatabase.SetMeta(DoneKey, DateTimeOffset.Now.ToString("O"));

            if (counts.Count > 0)
            {
                Console.WriteLine($"[DB] 已把老数据导入 SQLite（{string.Join("，", counts)}）");
                Console.WriteLine($"[DB] 旧文件已移到 {Path.Combine(AppPaths.RuntimeRoot, LegacyDirName)}/ 留档");
            }

            if (moved.Count > 0)
            {
                FileLog.Write("DB", $"老 JSON 已归档 {moved.Count} 个文件：{string.Join(", ", moved.Take(8))}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn("DB", $"老数据导入失败（不影响启动，下次启动会重试）：{ex.Message}");
        }
    }

    // ─────────────────────────── 各项导入 ───────────────────────────

    private static void ImportSettings(List<string> moved, List<string> counts)
    {
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path) || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM settings") > 0)
        {
            return;
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        // 先解析一次确认是合法 JSON（坏文件不写库，留着让人看）
        _ = JsonSerializer.Deserialize<AppSettings>(json, ReadOptions);

        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "INSERT INTO settings(id, json, updated_unix) VALUES(1, $json, $now)",
            ("$json", json), ("$now", DateTimeOffset.Now.ToUnixTimeSeconds())));

        moved.Add(Archive(path));
        counts.Add("设置 1 份");
    }

    private static void ImportSecrets(List<string> moved, List<string> counts)
    {
        var path = Path.Combine(AppPaths.DataDir, "secrets.json");
        if (!File.Exists(path) || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM secrets") > 0)
        {
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var apiKey = doc.RootElement.TryGetProperty("apiKey", out var v) ? v.GetString() : null;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            moved.Add(Archive(path));
            return;
        }

        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "INSERT INTO secrets(name, value, updated_unix) VALUES('apiKey', $v, $now)",
            ("$v", apiKey.Trim()), ("$now", DateTimeOffset.Now.ToUnixTimeSeconds())));

        moved.Add(Archive(path));
        counts.Add("密钥 1 条");
    }

    private static void ImportConversations(List<string> moved, List<string> counts)
    {
        var path = Path.Combine(AppPaths.DataDir, "conversations.json");
        if (!File.Exists(path) || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM conversations") > 0)
        {
            return;
        }

        var records = JsonSerializer.Deserialize<List<ConversationRecord>>(File.ReadAllText(path, Encoding.UTF8), ReadOptions);
        if (records is not { Count: > 0 })
        {
            moved.Add(Archive(path));
            return;
        }

        var messageCount = 0;
        AppDatabase.Write(conn =>
        {
            foreach (var c in records)
            {
                var sourceKey = string.IsNullOrWhiteSpace(c.SourceKey) ? $"local:{c.Id}" : c.SourceKey!;
                AppDatabase.Exec(conn, """
                    INSERT INTO conversations(id, source_key, kind, name, avatar_text, avatar_url, avatar_index,
                                              last_time_unix, unread_count, history_loaded, max_messages, updated_unix)
                    VALUES($id, $key, $kind, $name, $at, $au, $ai, $lt, $uc, 0, 500, $now)
                    ON CONFLICT(source_key) DO UPDATE SET name = excluded.name, id = excluded.id
                    """,
                    ("$id", string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString("N") : c.Id!),
                    ("$key", sourceKey),
                    ("$kind", c.Kind),
                    ("$name", c.Name),
                    ("$at", c.AvatarText),
                    ("$au", c.AvatarUrl),
                    ("$ai", c.AvatarIndex),
                    ("$lt", c.LastTimeUnix),
                    ("$uc", c.UnreadCount),
                    ("$now", DateTimeOffset.Now.ToUnixTimeSeconds()));

                foreach (var m in c.Messages)
                {
                    AppDatabase.Exec(conn, """
                        INSERT INTO messages(source_key, seq, role, text, time_unix, sender_name, sender_id,
                                             qq_message_id, recalled, images, archived)
                        VALUES($key, $seq, $role, $text, $t, $sn, $sid, $mid, 0, $img, 0)
                        ON CONFLICT(source_key, seq) DO NOTHING
                        """,
                        ("$key", sourceKey),
                        ("$seq", m.Seq),
                        ("$role", m.Role),
                        ("$text", m.Text),
                        ("$t", m.TimeUnix),
                        ("$sn", m.SenderName),
                        ("$sid", m.SenderId),
                        ("$mid", m.QqMessageId),
                        ("$img", m.ImageUrls is { Count: > 0 } ? JsonSerializer.Serialize(m.ImageUrls) : null));

                    messageCount++;
                }
            }
        });

        moved.Add(Archive(path));
        counts.Add($"会话 {records.Count} 个 / 消息 {messageCount} 条");
    }

    /// <summary>归档还是 JSONL（每行一条消息），导入到 messages 表并标记 archived=1。</summary>
    private static void ImportArchive(List<string> counts)
    {
        var dir = Path.Combine(AppPaths.DataDir, "archive");
        if (!Directory.Exists(dir) || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM messages WHERE archived = 1") > 0)
        {
            return;
        }

        var files = Directory.GetFiles(dir, "*.jsonl");
        if (files.Length == 0)
        {
            return;
        }

        var total = 0;
        var moved = new List<string>();

        // 归档行里没有 seq（老的 JSONL 只记了时间/角色/文本…），按“文件里的行序”补一个负序号：
        // 负数段不会和活动消息（≥1）撞车，而且**必须递增** —— 这样“按 seq 排序”就是时间顺序，
        // 面板的“最新在前”才会对（踩过：写成递减，翻旧账时倒序了）。
        AppDatabase.Write(conn =>
        {
            foreach (var file in files)
            {
                var sourceKey = Path.GetFileNameWithoutExtension(file).Replace('_', ':');
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                var baseSeq = -2_000_000_000L;
                var seq = baseSeq;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                        var role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "Peer" : "Peer";
                        var time = root.TryGetProperty("t", out var tt) ? tt.GetInt64() : 0;
                        var sender = root.TryGetProperty("sender", out var sn) ? sn.GetString() : null;
                        var uid = root.TryGetProperty("uid", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : (long?)null;
                        var mid = root.TryGetProperty("mid", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : (long?)null;

                        AppDatabase.Exec(conn, """
                            INSERT INTO messages(source_key, seq, role, text, time_unix, sender_name, sender_id,
                                                 qq_message_id, recalled, images, archived)
                            VALUES($key, $seq, $role, $text, $t, $sn, $sid, $mid, 0, NULL, 1)
                            ON CONFLICT(source_key, seq) DO NOTHING
                            """,
                            ("$key", sourceKey), ("$seq", seq++), ("$role", role), ("$text", text),
                            ("$t", time), ("$sn", sender), ("$sid", uid), ("$mid", mid));

                        total++;
                    }
                    catch (Exception)
                    {
                        // 单行坏了不影响其余
                    }
                }

                moved.Add(file);
            }
        });

        foreach (var file in moved)
        {
            Archive(file);
        }

        if (total > 0)
        {
            counts.Add($"归档消息 {total} 条");
        }
    }

    private static void ImportMemberProfiles(List<string> moved, List<string> counts)
    {
        var dir = Path.Combine(AppPaths.DataDir, "member_profiles");
        var legacySingle = Path.Combine(AppPaths.DataDir, "member_profiles.json");

        var files = new List<string>();
        if (Directory.Exists(dir))
        {
            files.AddRange(Directory.GetFiles(dir, "*.json"));
        }

        if (File.Exists(legacySingle))
        {
            files.Add(legacySingle);
        }

        if (files.Count == 0 || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM members") > 0)
        {
            return;
        }

        var people = 0;
        var messages = 0;
        var summaries = 0;

        foreach (var file in files)
        {
            List<MemberProfileRecord> records;
            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);
                if (file == legacySingle)
                {
                    records = JsonSerializer.Deserialize<List<MemberProfileRecord>>(text, ReadOptions) ?? new();
                }
                else
                {
                    var one = JsonSerializer.Deserialize<MemberProfileRecord>(text, ReadOptions);
                    records = one is null ? new() : new List<MemberProfileRecord> { one };
                }
            }
            catch (Exception ex)
            {
                FileLog.Warn("DB", $"档案文件 {Path.GetFileName(file)} 读取失败：{ex.Message}");
                continue;
            }

            AppDatabase.Write(conn =>
            {
                foreach (var p in records)
                {
                    if (string.IsNullOrWhiteSpace(p.Uid))
                    {
                        continue;
                    }

                    var updated = p.Messages.Count > 0 ? p.Messages.Max(m => m.TimeUnix) : DateTimeOffset.Now.ToUnixTimeSeconds();
                    AppDatabase.Exec(conn,
                        "INSERT INTO members(uid, name, updated_unix) VALUES($uid, $name, $u) " +
                        "ON CONFLICT(uid) DO UPDATE SET name = excluded.name, updated_unix = excluded.updated_unix",
                        ("$uid", p.Uid), ("$name", p.Name ?? string.Empty), ("$u", updated));
                    people++;

                    foreach (var m in p.Messages)
                    {
                        AppDatabase.Exec(conn, """
                            INSERT INTO member_messages(uid, seq, group_id, group_name, text, time_unix)
                            VALUES($uid, $seq, $gid, $gn, $text, $t)
                            ON CONFLICT(uid, group_id, seq) DO NOTHING
                            """,
                            ("$uid", p.Uid), ("$seq", m.Seq), ("$gid", m.GroupId), ("$gn", m.GroupName),
                            ("$text", m.Text), ("$t", m.TimeUnix));
                        messages++;
                    }

                    foreach (var s in p.Summaries)
                    {
                        if (string.IsNullOrWhiteSpace(s.Scope) || string.IsNullOrWhiteSpace(s.Text))
                        {
                            continue;
                        }

                        AppDatabase.Exec(conn, """
                            INSERT INTO member_summaries(uid, scope, text, through_seq, updated_unix, folded_count)
                            VALUES($uid, $scope, $text, $ts, $u, $fc)
                            ON CONFLICT(uid, scope) DO UPDATE SET text = excluded.text, through_seq = excluded.through_seq,
                                                                  updated_unix = excluded.updated_unix, folded_count = excluded.folded_count
                            """,
                            ("$uid", p.Uid), ("$scope", s.Scope), ("$text", s.Text), ("$ts", s.ThroughSeq),
                            ("$u", s.UpdatedUnix), ("$fc", s.FoldedCount));
                        summaries++;
                    }
                }
            });

            moved.Add(Archive(file));
        }

        if (people > 0)
        {
            counts.Add($"档案 {people} 人 / 发言 {messages} 条 / 画像 {summaries} 份");
        }
    }

    private static void ImportMood(List<string> moved, List<string> counts)
    {
        var path = Path.Combine(AppPaths.DataDir, "mood.json");
        if (!File.Exists(path) || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM mood") > 0)
        {
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = doc.RootElement;
        var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
        long? updated = root.TryGetProperty("updatedAt", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt64() : null;
        var pokes = root.TryGetProperty("pokeTimes", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList()
            : new List<long>();

        AppDatabase.Write(conn =>
        {
            AppDatabase.Exec(conn, "INSERT INTO mood(id, text, updated_unix) VALUES(1, $t, $u)",
                ("$t", text), ("$u", updated));
            foreach (var at in pokes)
            {
                AppDatabase.Exec(conn, "INSERT INTO mood_pokes(at_unix) VALUES($a) ON CONFLICT(at_unix) DO NOTHING", ("$a", at));
            }
        });

        moved.Add(Archive(path));
        counts.Add($"心情 1 份 / 被戳 {pokes.Count} 次");
    }

    private static void ImportHeardSongs(List<string> moved, List<string> counts)
    {
        var path = Path.Combine(AppPaths.DataDir, "music", "listened.json");
        if (!File.Exists(path) || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM heard_songs") > 0)
        {
            return;
        }

        var songs = JsonSerializer.Deserialize<List<HeardSong>>(File.ReadAllText(path, Encoding.UTF8), ReadOptions);
        if (songs is not { Count: > 0 })
        {
            moved.Add(Archive(path));
            return;
        }

        AppDatabase.Write(conn =>
        {
            foreach (var s in songs)
            {
                if (string.IsNullOrWhiteSpace(s.Key))
                {
                    continue;
                }

                AppDatabase.Exec(conn, """
                    INSERT INTO heard_songs(key, platform, song_id, title, artist, album, duration_seconds,
                                            features, lyric_excerpt, first_heard_unix, last_heard_unix, heard_count)
                    VALUES($k, $p, $sid, $title, $artist, $album, $dur, $f, $l, $fh, $lh, $c)
                    ON CONFLICT(key) DO NOTHING
                    """,
                    ("$k", s.Key), ("$p", s.Platform), ("$sid", s.SongId), ("$title", s.Title),
                    ("$artist", s.Artist), ("$album", s.Album), ("$dur", s.DurationSeconds),
                    ("$f", s.Features), ("$l", s.LyricExcerpt),
                    ("$fh", s.FirstHeard.ToUnixTimeSeconds()), ("$lh", s.LastHeard.ToUnixTimeSeconds()),
                    ("$c", s.HeardCount));
            }
        });

        moved.Add(Archive(path));
        counts.Add($"听过的歌 {songs.Count} 首");
    }

    private static void ImportStickers(List<string> moved, List<string> counts)
    {
        var path = Path.Combine(AppPaths.RuntimeRoot, "stickers", "index.json");
        if (!File.Exists(path) || AppDatabase.Scalar<long>("SELECT COUNT(1) FROM stickers") > 0)
        {
            return;
        }

        var items = JsonSerializer.Deserialize<List<StickerRecord>>(File.ReadAllText(path, Encoding.UTF8), ReadOptions);
        if (items is not { Count: > 0 })
        {
            moved.Add(Archive(path));
            return;
        }

        AppDatabase.Write(conn =>
        {
            foreach (var s in items)
            {
                if (string.IsNullOrWhiteSpace(s.Id))
                {
                    continue;
                }

                AppDatabase.Exec(conn, """
                    INSERT INTO stickers(id, hash, file, ext, bytes, added_unix, last_used_unix, uses,
                                         from_uid, from_group, description, tags, is_sticker, described, describe_attempts)
                    VALUES($id, $h, $f, $ext, $b, $a, $lu, $u, $fu, $fg, $d, $tags, $is, $de, $da)
                    ON CONFLICT(id) DO NOTHING
                    """,
                    ("$id", s.Id), ("$h", s.Hash), ("$f", s.File), ("$ext", s.Ext), ("$b", s.Bytes),
                    ("$a", s.AddedAt), ("$lu", s.LastUsedAt), ("$u", s.Uses), ("$fu", s.FromUid), ("$fg", s.FromGroup),
                    ("$d", s.Desc), ("$tags", s.Tags is { Count: > 0 } ? JsonSerializer.Serialize(s.Tags) : null),
                    ("$is", s.IsSticker is null ? null : (s.IsSticker.Value ? 1 : 0)),
                    ("$de", s.Described ? 1 : 0), ("$da", s.DescribeAttempts));
            }
        });

        moved.Add(Archive(path));
        counts.Add($"表情包 {items.Count} 张");
    }

    /// <summary>把导完的旧文件移到 legacy-json/ 下（保留相对目录），返回相对路径供日志用。</summary>
    private static string Archive(string path)
    {
        try
        {
            var root = AppPaths.RuntimeRoot;
            var legacy = Path.Combine(root, LegacyDirName);
            var relative = Path.GetRelativePath(root, path);
            var target = Path.Combine(legacy, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(path, target, overwrite: true);
            return relative;
        }
        catch (Exception ex)
        {
            FileLog.Warn("DB", $"旧文件归档失败（{path}）：{ex.Message}");
            return Path.GetFileName(path);
        }
    }
}
