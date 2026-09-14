using System.Text;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S29 忽略群友消息里的括号旁白（开关）。
///
/// 号主两次反馈后的真实口径（我拉了群里 700+ 条带括号的消息看过）：
///   • 整条都是旁白（「（笑）」「（bushi）」）→ 整条忽略（不入库、不请求模型）；
///   • 前后带旁白（「行（端在桌上）」「（放在地上）来吧」）→ **只剥掉旁白**，正文照常用；
///   • 机器人自己的内容标记（[表情:斜眼笑]/[图片]）不是旁白，不能剥（把它们剥了等于抹掉群友发表情的记录）；
///   • 句子中间的括号（「（2026）年的计划」）不碰；
///   • 私聊 / 带图 / @ 了机器人三种情况完全不动。
/// </summary>
public static partial class Program
{
    private static async Task RunBracketMessageScenarioAsync()
    {
        Section("S29 忽略纯括号消息（开关 + 不误伤 + 三种例外）");

        const int openAiPort = 17836;
        const int botWsPort = 13045;
        const int panelPort = 18110;
        const long groupId = 66711;
        const long friendId = 30121;

        var dataDir = NewDataDir("s29");
        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = $"{groupId},{friendId}",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_IGNORE_BRACKETS"] = "1"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        var (_, settings) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 面板能读到这个开关（默认关，这里是按环境变量开着的）",
            settings.Contains("\"ignoreBracketMessages\":true"), Snippet(settings, "ignoreBracketMessages"));

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- 1) 整条旁白：各种写法都不该进库、也不该请求模型 ----
        openAi.ClearRequests();
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（笑）", 9911, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（笑）（跑）", 9912, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "(bushi)", 9913, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "【摸鱼】", 9914, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（真的）？", 9915, ct: cts.Token);
        await Task.Delay(4000);

        var stored = DbProbe.Count(dataDir,
            "SELECT COUNT(1) FROM messages WHERE text LIKE '%笑%' OR text LIKE '%bushi%' OR text LIKE '%摸鱼%'");
        Check("★ 整条都是旁白 → 不入库（旁白不污染聊天记录）", stored == 0, $"库里查到 {stored} 条");
        Check("★ 整条都是旁白 → 不请求模型（不扯机器人接话）", openAi.Requests.Count == 0,
            $"本轮请求 {openAi.Requests.Count} 次");

        // ---- 2) 前后带的旁白：正文留着、旁白剥掉（号主说的“不是整条都是括号”）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "好嘞。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30022, "老王", "行（端在桌上）", 9916, ct: cts.Token);
        var tail = await WaitForRequestAsync(openAi, r => r.Contains("行"), TimeSpan.FromSeconds(30));
        Check("★ 尾部旁白被剥掉、正文留着（「行（端在桌上）」→「行」）",
            tail is not null && !tail.Contains("端在桌上"),
            tail is null ? "(没进上下文)" : Snippet(tail, "行"));

        var storedTail = DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text = $t", ("$t", "行"));
        Check("★ 落库的也是洗净后的文本（旁白不进聊天记录，也不进档案）", storedTail >= 1, $"库里「行」{storedTail} 条");

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "来了来了。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30022, "老王", "（放在地上）来吧，猫猫，", 9917, ct: cts.Token);
        var lead = await WaitForRequestAsync(openAi, r => r.Contains("来吧，猫猫"), TimeSpan.FromSeconds(30));
        Check("★ 开头旁白被剥掉、正文留着（「（放在地上）来吧」→「来吧」）",
            lead is not null && !lead.Contains("放在地上"),
            lead is null ? "(没进上下文)" : Snippet(lead, "来吧"));

        // ---- 3) 机器人自己的内容标记不能被当旁白剥掉 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "哈哈。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30023, "小红", "[表情:斜眼笑]", 9918, ct: cts.Token);
        var face = await WaitForRequestAsync(openAi, r => r.Contains("表情:斜眼笑"), TimeSpan.FromSeconds(30));
        Check("★ 群友发的 QQ 表情不是旁白（[表情:斜眼笑] 不剥也不忽略）", face is not null,
            face is null ? "(表情被当旁白吃了)" : "表情还在");

        // ---- 4) 句子中间的括号不碰 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "那得看计划。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30024, "小明", "（2026）年的计划还做吗", 9919, ct: cts.Token);
        var middle = await WaitForRequestAsync(openAi, r => r.Contains("2026") && r.Contains("年的计划"), TimeSpan.FromSeconds(30));
        Check("★ 句子中间的括号是正文（不当旁白剥掉）", middle is not null,
            middle is null ? "(中间括号被剥了)" : "括号保留");

        // ---- 5) 例外一：@ 了机器人 → 完全不动 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "在的，你说。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30025, "小红", "（真的吗）", 9920, mentionBot: true, ct: cts.Token);
        var mentioned = await WaitForRequestAsync(openAi, r => r.Contains("真的吗"), TimeSpan.FromSeconds(30));
        Check("★ @ 了机器人的括号消息完全不动（直接叫它就得理）", mentioned is not null,
            mentioned is null ? "(被忽略了)" : "已进上下文");

        // ---- 6) 例外二：私聊 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "笑什么呀。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "（笑）", 9921, ct: cts.Token);
        var privateOne = await WaitForRequestAsync(openAi, r => r.Contains("（笑）"), TimeSpan.FromSeconds(30));
        Check("★ 私聊里同样的括号消息不动（一对一必须理）", privateOne is not null,
            privateOne is null ? "(被忽略了)" : "已进上下文");

        // ---- 7) 例外三：带图 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "这图有点意思。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30026, "小美", "（图）", 9922,
            imageUrl: "http://127.0.0.1:9/none.png", ct: cts.Token);
        await Task.Delay(3000);
        var imageStored = DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text LIKE '%（图）%'");
        Check("★ 带图的括号消息不动（会落库）", imageStored >= 1, $"库里 {imageStored} 条");

        // ---- 8) 关掉开关 → 旁白不再被剥 ----
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using (var offResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
                   new StringContent("""{"ignoreBracketMessages":false}""", Encoding.UTF8, "application/json"), cts.Token))
        {
            Check("★ 面板能关掉这个开关", offResp.IsSuccessStatusCode);
        }

        await Task.Delay(400);
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "笑就笑吧。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30027, "小明", "（笑死）", 9923, ct: cts.Token);
        var afterOff = await WaitForRequestAsync(openAi, r => r.Contains("笑死"), TimeSpan.FromSeconds(30));
        Check("★ 关掉开关后旁白原样进上下文（开关真的生效）", afterOff is not null,
            afterOff is null ? "(关掉后仍被忽略)" : "已进上下文");

        bot.Dispose();
    }
}