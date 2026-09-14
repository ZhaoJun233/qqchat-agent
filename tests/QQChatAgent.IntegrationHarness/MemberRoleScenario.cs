using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S28 群成员身份识别：群主 / 管理员 / 群头衔。
///
/// 为什么单独一个场景：这是“模型看到的世界”的一部分 —— 以前提示词里完全没有身份信息，
/// 机器人只能从语气猜谁说了算。现在：
///   • 群消息事件自带 <c>sender.role</c>（零成本）→ 先记下来；
///   • 群头衔只有 <c>get_group_member_info</c> 才有 → 按需去问一次（不能每条消息都问）；
///   • 身份落库（重启后仍知道谁是群主）；
///   • 出成提示词里的 [本群身份] 一段（群主/管理员/头衔），私聊不给。
/// </summary>
public static partial class Program
{
    private static async Task RunMemberRoleScenarioAsync()
    {
        Section("S28 群成员身份：群主 / 管理员 / 群头衔（进提示词 + 落库 + 重启不丢）");

        const int openAiPort = 17835;
        const int botWsPort = 13044;
        const int panelPort = 18109;
        const long groupId = 66701;
        const long privateId = 30119;
        const long ownerId = 30001;
        const long adminId = 30002;
        const long titledId = 30003;
        const long plainId = 30004;

        var dataDir = NewDataDir("s28");
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = $"{groupId},{privateId}",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        // 三个身份：群主、管理员、有自定义头衔的普通成员
        protocol.SetRole(groupId, ownerId, "owner", "传说中的群主");
        protocol.SetRole(groupId, adminId, "admin", "");
        protocol.SetRole(groupId, titledId, "member", "活跃气氛担当");

        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- 1) 群主说话：身份要进提示词 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "收到，群主。"}""");
        await protocol.SendGroupMessageAsync(groupId, ownerId, "老王", "@10001 今晚开个会", 9701, mentionBot: true, ct: cts.Token);
        var ownerPrompt = await WaitForRequestAsync(openAi, r => r.Contains("本群身份"), TimeSpan.FromSeconds(60));
        Check("★ 群主说话时，提示词里带上了本群身份段", ownerPrompt is not null,
            ownerPrompt is null ? "(没等到带身份的请求)" : Snippet(ownerPrompt, "本群身份"));
        Check("★ 身份段写明了谁是群主（带 QQ 号，便于对上发言的人）",
            ownerPrompt is not null && ownerPrompt.Contains("群主：") && ownerPrompt.Contains("老王") &&
            ownerPrompt.Contains(ownerId.ToString()),
            ownerPrompt is null ? "(无)" : Snippet(ownerPrompt, "群主："));
        Check("★ 提示词告诉了模型这些身份意味着什么（别拍马屁、也别拿身份压人）",
            ownerPrompt is not null && ownerPrompt.Contains("能踢人") && ownerPrompt.Contains("不要拿身份拍马屁"));

        // ---- 2) 群头衔：消息事件里没有，机器人要主动去问协议端 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "这头衔不错。"}""");
        await protocol.SendGroupMessageAsync(groupId, titledId, "小明", "@10001 我这个头衔怎么样", 9702, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => protocol.MemberInfoHits >= 1 && openAi.Requests.Count > 0, TimeSpan.FromSeconds(60));
        var titledPrompt = await WaitForRequestAsync(openAi,
            r => r.Contains("本群身份") && r.Contains("活跃气氛担当"), TimeSpan.FromSeconds(60));
        Check("★ 缺头衔时会主动问协议端（get_group_member_info）",
            protocol.MemberInfoHits >= 1, $"问了 {protocol.MemberInfoHits} 次");
        Check("★ 自定义头衔进了提示词", titledPrompt is not null,
            titledPrompt is null ? "(提示词里没有头衔)" : Snippet(titledPrompt, "群头衔"));

        // ---- 3) 管理员：事件里的 role=admin 直接生效（不必等协议端）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "好的管理员。"}""");
        await protocol.SendGroupMessageAsync(groupId, adminId, "小红", "@10001 帮忙看下", 9703, mentionBot: true, ct: cts.Token);
        var adminPrompt = await WaitForRequestAsync(openAi, r => r.Contains("管理员："), TimeSpan.FromSeconds(60));
        Check("★ 管理员身份进了提示词", adminPrompt is not null,
            adminPrompt is null ? "(没等到管理员身份)" : Snippet(adminPrompt, "管理员："));

        // ---- 4) 普通群友不进身份段（省 token，也别把名单泄露成一长串）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "嗯嗯。"}""");
        await protocol.SendGroupMessageAsync(groupId, plainId, "路人甲", "@10001 在吗", 9704, mentionBot: true, ct: cts.Token);
        var plainPrompt = await WaitForRequestAsync(openAi, r => r.Contains("路人甲") && r.Contains("在吗"), TimeSpan.FromSeconds(60));
        Check("★ 普通群友不会占身份段（只列群主/管理员/有头衔的人）",
            plainPrompt is not null && !plainPrompt.Contains("路人甲（" + plainId + "）"),
            plainPrompt is null ? "(没等到请求)" : "身份段里没有路人甲");

        // ---- 5) 私聊不给群身份段（私聊聊的不是那个群的事）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "在的。"}""");
        await protocol.SendPrivateMessageAsync(privateId, "路人乙", "在吗", 9705, ct: cts.Token);
        var privatePrompt = await WaitForRequestAsync(openAi, r => r.Contains("在吗"), TimeSpan.FromSeconds(30))
                            ?? (openAi.Requests.Count > 0 ? openAi.DescribeRequest(openAi.Requests.Count - 1) : null);
        Check("★ 私聊提示词里没有群身份段",
            privatePrompt is not null && !privatePrompt.Contains("本群身份") && !privatePrompt.Contains("群主："),
            privatePrompt is null ? "(没等到私聊请求)" : Snippet(privatePrompt, "在吗"));

        // ---- 6) 身份落库（重启后仍知道谁是群主）----
        var ownerRow = DbProbe.Text(dataDir,
            "SELECT role || '|' || COALESCE(title,'') FROM member_roles WHERE uid = $u AND group_id = $g",
            ("$u", ownerId.ToString()), ("$g", groupId));
        var titledRow = DbProbe.Text(dataDir,
            "SELECT role || '|' || COALESCE(title,'') FROM member_roles WHERE uid = $u AND group_id = $g",
            ("$u", titledId.ToString()), ("$g", groupId));
        Check("★ 身份落库了（群主的 role=owner）", ownerRow is not null && ownerRow.StartsWith("owner|"),
            ownerRow ?? "(库里没这条)");
        Check("★ 群头衔也落库了（不是只在内存里）",
            titledRow is not null && titledRow.Contains("活跃气氛担当"), titledRow ?? "(库里没这条)");

        // ---- 7) 重启：同一份 data 目录，新进程仍能报出群主 ----
        bot.Dispose();
        await Task.Delay(400);
        using var bot2 = StartBot(new Dictionary<string, string>
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

        await WaitForPortAsync(botWsPort, cts.Token, bot2);
        using var protocol2 = new MockProtocol { SelfId = 10001 };
        await protocol2.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol2.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "重启后也在。"}""");
        await protocol2.SendGroupMessageAsync(groupId, 30099, "群友", "@10001 说句话", 9706, mentionBot: true, ct: cts.Token);
        var afterRestart = await WaitForRequestAsync(openAi, r => r.Contains("本群身份"), TimeSpan.FromSeconds(60));
        Check("★ 重启后（同一份库）提示词里仍然有群主身份", afterRestart is not null,
            afterRestart is null ? "(重启后身份没了)" : Snippet(afterRestart, "群主："));

        // ---- 8) 上限：身份段不会无限长 ----
        var many = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            var uid = 31000 + i;
            protocol2.SetRole(groupId, uid, "admin", $"管理员头衔{i}");
            await protocol2.SendGroupMessageAsync(groupId, uid, $"管理{i}", $"@10001 在么{i}", 9800 + i, mentionBot: true, ct: cts.Token);
        }

        await Task.Delay(4000);
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "人数不少。"}""");
        await protocol2.SendGroupMessageAsync(groupId, 30098, "群友", "@10001 最后说一句", 9900, mentionBot: true, ct: cts.Token);
        var capped = await WaitForRequestAsync(openAi, r => r.Contains("本群身份"), TimeSpan.FromSeconds(60));
        var adminCount = capped is null ? 0 : System.Text.RegularExpressions.Regex.Matches(capped, "（31\\d{3}）").Count;
        Check("★ 身份段有人数上限（不会把整个群的管理员都塞进提示词）",
            capped is not null && adminCount <= 14, $"身份段里出现 {adminCount} 个 QQ 号（上限 14）");

        bot2.Dispose();
    }
}
