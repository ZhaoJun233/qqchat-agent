using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S27 数据迁移：老版本的一堆 JSON（settings/conversations/member_profiles/mood/music/stickers/secrets + archive/*.jsonl）
/// 在启动时被一次性导入 SQLite，并且：
///   • 数据一条不少（会话、消息、档案、画像、心情、听过的歌、表情包、密钥都在库里）；
///   • 旧文件被**移到 legacy-json/ 留档**而不是删掉（导入逻辑万一有 bug，人工还能翻出来）；
///   • 重启不会重复导入（幂等），也不会把面板刚改的东西覆盖回去；
///   • 导入后的数据**真的在起作用**（模型请求里能看到旧的画像/档案）。
///
/// 为什么这条必须测：这是唯一一次“动用户已有数据”的操作 —— 迁丢了就再也回不来。
/// </summary>
public static partial class Program
{
    private static async Task RunMigrationScenarioAsync()
    {
        Section("S27 老 JSON → SQLite 迁移（不丢数据 / 旧文件留档 / 幂等 / 真的生效）");

        const int openAiPort = 17835;
        const int botWsPort = 13042;
        const int panelPort = 18106;
        const long groupId = 66693;

        var dataDir = NewDataDir("s27");
        var dataSub = Path.Combine(dataDir, "data");
        var profileDir = Path.Combine(dataSub, "member_profiles");
        var archiveDir = Path.Combine(dataSub, "archive");
        var musicDir = Path.Combine(dataSub, "music");
        var stickerDir = Path.Combine(dataDir, "stickers");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(musicDir);
        Directory.CreateDirectory(stickerDir);

        var now = DateTimeOffset.Now;
        var stamp = now.ToUnixTimeSeconds();

        // ---- 造假的老数据（完全按老版本的格式写）----
        await File.WriteAllTextAsync(Path.Combine(dataSub, "settings.json"),
            $$"""
            {
              "BotPersona": "我是迁移测试用的机器人",
              "MessageWhitelist": "{{groupId}}",
              "AiDesire": 77,
              "EnableMusic": true
            }
            """, Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(dataSub, "secrets.json"),
            """{ "apiKey": "sk-migrated-key" }""", Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(dataSub, "conversations.json"),
            $$"""
            [
              {
                "Id": "conv-migrated",
                "SourceKey": "group:{{groupId}}",
                "Kind": "GroupChat",
                "Name": "迁移测试群",
                "AvatarText": "迁",
                "LastTimeUnix": {{stamp}},
                "UnreadCount": 2,
                "Messages": [
                  { "Role": "Peer", "Text": "这是迁移前的一条老消息", "TimeUnix": {{stamp - 600}}, "SenderName": "老群友", "SenderId": 20002, "QqMessageId": 8801, "Seq": 1 },
                  { "Role": "Self", "Text": "这是迁移前机器人的回复", "TimeUnix": {{stamp - 590}}, "Seq": 2 }
                ]
              }
            ]
            """, Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(profileDir, "20002.json"),
            $$"""
            {
              "Uid": "20002",
              "Name": "老王",
              "Messages": [
                { "Text": "迁移前我说过一句很有用的话", "TimeUnix": {{stamp - 3000}}, "GroupName": "迁移测试群", "GroupId": {{groupId}}, "Seq": 1 }
              ],
              "Summaries": [
                { "Scope": "group:{{groupId}}", "Text": "老王：后端开发，爱聊编译问题。", "ThroughSeq": 0, "UpdatedUnix": {{stamp}}, "FoldedCount": 1 }
              ]
            }
            """, Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(dataSub, "mood.json"),
            $$"""{ "text": "迁移过来的心情", "updatedAt": {{stamp}}, "pokeTimes": [{{stamp - 60}}] }""", Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(musicDir, "listened.json"),
            $$"""
            [
              { "Key": "netease:999", "Platform": "netease", "SongId": "999", "Title": "迁移前的歌", "Artist": "老歌手",
                "DurationSeconds": 200, "Features": "波形实测：中速", "LyricExcerpt": "歌词",
                "FirstHeard": "{{now.AddDays(-2):O}}", "LastHeard": "{{now.AddHours(-1):O}}", "HeardCount": 3 }
            ]
            """, Encoding.UTF8);

        await File.WriteAllTextAsync(Path.Combine(stickerDir, "index.json"),
            $$"""
            [
              { "Id": "abc12345", "Hash": "abc12345deadbeef", "File": "abc12345-deadbeef.png", "Ext": "png",
                "Bytes": 100, "AddedAt": {{stamp}}, "LastUsedAt": 0, "Uses": 0,
                "Desc": "迁移过来的表情包", "Tags": ["测试"], "IsSticker": true, "Described": true, "DescribeAttempts": 1 }
            ]
            """, Encoding.UTF8);
        // 图片本体也要在（否则库里的索引会被当成“文件丢失”而跳过）
        await File.WriteAllBytesAsync(Path.Combine(stickerDir, "abc12345-deadbeef.png"), new byte[] { 1, 2, 3, 4 });

        await File.WriteAllTextAsync(Path.Combine(archiveDir, "group_66693.jsonl"),
            $$"""
            {"t":{{stamp - 9000}},"role":"Peer","sender":"老群友","uid":20002,"mid":8701,"text":"归档里的老消息一"}
            {"t":{{stamp - 8900}},"role":"Self","sender":null,"uid":null,"mid":null,"text":"归档里的老消息二"}
            """, Encoding.UTF8);

        // ---- 启动机器人（第一件事就是导入）----
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "迁移后照常说话"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);
        await Task.Delay(1500); // 等导入与首次加载

        // ---- 导入结果：库文件出现，且各表条数对得上 ----
        Check("★ 启动时建了 SQLite 库并完成了导入", DbProbe.Exists(dataDir),
            $"库文件：{DbProbe.DbPath(dataDir)}");
        Check("★ 配置导进来了（老 settings.json 的值生效）",
            (DbProbe.Text(dataDir, "SELECT json FROM settings WHERE id = 1") ?? string.Empty).Contains("我是迁移测试用的机器人"));
        Check("★ 密钥导进来了", DbProbe.Text(dataDir, "SELECT value FROM secrets WHERE name = 'apiKey'") == "sk-migrated-key");
        Check("★ 会话导进来了", DbProbe.Count(dataDir, "SELECT COUNT(1) FROM conversations") == 1);
        Check("★ 消息导进来了（含发送者与 QQ 消息号）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE archived = 0") == 2 &&
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE qq_message_id = 8801") == 1);
        Check("★ 归档消息也导进来了（旧的 .jsonl → archived=1）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE archived = 1") == 2,
            DbProbe.Dump(dataDir, "SELECT text FROM messages WHERE archived = 1"));
        Check("★ 人物档案导进来了（发言 + 画像）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM member_messages WHERE uid = '20002'") == 1 &&
            (DbProbe.Text(dataDir, "SELECT text FROM member_summaries WHERE uid = '20002'") ?? string.Empty).Contains("后端开发"));
        Check("★ 心情导进来了", DbProbe.Text(dataDir, "SELECT text FROM mood WHERE id = 1") == "迁移过来的心情");
        Check("★ 听过的歌导进来了", DbProbe.Text(dataDir, "SELECT title FROM heard_songs WHERE key = 'netease:999'") == "迁移前的歌");
        Check("★ 表情包索引导进来了", DbProbe.Count(dataDir, "SELECT COUNT(1) FROM stickers WHERE id = 'abc12345'") == 1);

        // ---- 旧文件留档，不是删掉 ----
        var legacyRoot = Path.Combine(dataDir, "legacy-json");
        Check("★ 旧 JSON 被移到 legacy-json/ 留档（不是删掉）",
            File.Exists(Path.Combine(legacyRoot, "data", "conversations.json")) &&
            File.Exists(Path.Combine(legacyRoot, "data", "settings.json")) &&
            File.Exists(Path.Combine(legacyRoot, "data", "member_profiles", "20002.json")),
            Directory.Exists(legacyRoot)
                ? string.Join(", ", Directory.GetFiles(legacyRoot, "*.json", SearchOption.AllDirectories).Select(Path.GetFileName))
                : "(没有 legacy-json 目录)");
        Check("★ 原地不再留旧的 settings.json / conversations.json",
            !File.Exists(Path.Combine(dataSub, "settings.json")) &&
            !File.Exists(Path.Combine(dataSub, "conversations.json")));

        // ---- 迁移后的数据真的在起作用：旧画像进了提示词 ----
        openAi.ClearRequests();
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "迁移之后我再说一句", 8802, mentionBot: true, ct: cts.Token);

        // 注意匹配“聊天请求”而不是“画像摘要请求”：摘要请求里也会出现这句话（它要压缩的原文）——
        // 聊天请求里对方发言一律是 {昵称}{内容--时间} 的格式，用它当判据。
        var prompt = await WaitForRequestAsync(openAi,
            r => r.Contains("迁移之后我再说一句") && r.Contains("{老王}"), TimeSpan.FromSeconds(40));
        var (profStatus, profBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/profiles/20002");
        Check("★ 迁移过来的画像真的被注入提示词（不是只躺在库里）",
            prompt is not null && prompt.Contains("后端开发"),
            prompt is null
                ? $"(没等到聊天请求)；面板接口({profStatus})：{Truncate(profBody, 200)}"
                : $"参与人段：{ParticipantsSection(prompt)}；诊断：{string.Join(" | ", bot.OutputLines.Where(l => l.Contains("[Profiles]")).TakeLast(3))}");
        Check("★ 迁移过来的历史上下文也在",
            prompt is not null && prompt.Contains("这是迁移前的一条老消息"));

        await bot.StopAsync();

        // ---- 幂等：再启动一次，不会重复导入、也不会覆盖面板改过的值 ----
        var settingsPersonaBefore = DbProbe.Text(dataDir, "SELECT json_extract(json, '$.BotPersona') FROM settings WHERE id = 1");
        using var bot2 = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = "http://0.0.0.0:13043",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "18107"
        });
        await WaitForPortAsync(13043, cts.Token, bot2);
        await Task.Delay(1500);

        Check("★ 重启不重复导入（会话/消息条数不变）",
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM conversations") == 1 &&
            DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE archived = 0") == 4,
            $"会话 {DbProbe.Count(dataDir, "SELECT COUNT(1) FROM conversations")}，" +
            $"活动消息 {DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE archived = 0")}（期望 4：迁移 2 + 新消息 1 + 机器人回复 1）");
        Check("★ 重启不覆盖配置（值不变，而且能用 SQL 直接查）",
            DbProbe.Text(dataDir, "SELECT json_extract(json, '$.BotPersona') FROM settings WHERE id = 1") == settingsPersonaBefore &&
            DbProbe.Text(dataDir, "SELECT json_extract(json, '$.AiDesire') FROM settings WHERE id = 1") == "77",
            $"persona={DbProbe.Text(dataDir, "SELECT json_extract(json, '$.BotPersona') FROM settings WHERE id = 1")}");

        await bot2.StopAsync();
    }

    /// <summary>把系统提示词里“[会话参与者档案]”那一段截出来（定位画像到底注入没注入）。</summary>
    private static string ParticipantsSection(string requestDescription)
    {
        var system = SystemTextOf(requestDescription);
        var start = system.IndexOf("[会话参与者档案", StringComparison.Ordinal);
        if (start < 0)
        {
            return "(没有参与者档案段)";
        }

        var end = Math.Min(system.Length, start + 300);
        return Truncate(system[start..end].Replace("\n", " "), 280);
    }

    /// <summary>把系统提示词里“[会话参与者档案]”那一段截出来（定位画像到底注入没注入）。</summary>
    private static string ParticipantsSection(JsonObject request)
    {
        var system = SystemText(request);
        var start = system.IndexOf("[会话参与者档案", StringComparison.Ordinal);
        if (start < 0)
        {
            return "(没有参与者档案段)";
        }

        var end = Math.Min(system.Length, start + 400);
        return Truncate(system[start..end].Replace("\n", " "), 380);
    }

    /// <summary>把请求里的角色与开头拼成一行，便于看清匹配到的是哪种请求。</summary>
    private static string RequestOverview(string requestDescription)
    {
        try
        {
            var node = JsonNode.Parse(requestDescription);
            var parts = new List<string>();
            foreach (var m in node?["messages"]?.AsArray() ?? new JsonArray())
            {
                var role = m?["role"]?.GetValue<string>() ?? "?";
                var text = (m?["content"]?.GetValue<string>() ?? string.Empty).Replace("\n", " ");
                parts.Add(role + ":" + (text.Length <= 60 ? text : text[..60]));
            }

            return string.Join(" / ", parts);
        }
        catch (Exception)
        {
            return Truncate(requestDescription, 120);
        }
    }

    /// <summary>把 MockOpenAi 的请求全文里 system 消息的内容取出来（断言失败时看提示词方便）。</summary>
    private static string SystemTextOf(string requestDescription)
    {
        try
        {
            var node = JsonNode.Parse(requestDescription);
            var system = node?["messages"]?.AsArray()
                .FirstOrDefault(m => m?["role"]?.GetValue<string>() == "system")?["content"]?.GetValue<string>();
            return system ?? "(没找到 system)";
        }
        catch (Exception)
        {
            return requestDescription;
        }
    }
}
