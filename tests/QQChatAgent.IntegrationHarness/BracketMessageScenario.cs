using System.Text;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S29 忽略“纯括号消息”：群里「（笑）」「（bushi）」这类旁白不落库、不进上下文、不扯机器人接话。
///
/// 号主原话：『加上忽略群友括号消息的开关』。
/// 口径：去掉所有括号段、空白、标点与 emoji 之后不剩内容才算旁白 —— “今天天气不错（大概）”不误伤；
/// 私聊 / 带图 / @ 了机器人这三种永远不忽略（宁可多回也不装死）。关掉开关则完全不拦。
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

        // ---- 1) 旁白：各种写法都不该进库、也不该请求模型 ----
        openAi.ClearRequests();
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（笑）", 9911, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（笑）（跑）", 9912, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "(bushi)", 9913, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "【摸鱼】", 9914, ct: cts.Token);
        await protocol.SendGroupMessageAsync(groupId, 30021, "小美", "（真的）？", 9915, ct: cts.Token);
        await Task.Delay(4000);

        var stored = DbProbe.Count(dataDir,
            "SELECT COUNT(1) FROM messages WHERE text LIKE '%笑%' OR text LIKE '%bushi%' OR text LIKE '%摸鱼%' OR text LIKE '%真的%'");
        Check("★ 纯括号消息不入库（旁白不污染聊天记录）", stored == 0, $"库里查到 {stored} 条");
        Check("★ 纯括号消息不请求模型（不扯机器人接话）", openAi.Requests.Count == 0,
            $"本轮请求 {openAi.Requests.Count} 次");

        // ---- 2) 不误伤：括号外面有正文的照常处理 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "是啊，今天挺舒服。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30022, "老王", "@10001 今天天气不错（大概）", 9916, mentionBot: true, ct: cts.Token);
        var withBody = await WaitForRequestAsync(openAi, r => r.Contains("今天天气不错"), TimeSpan.FromSeconds(30));
        Check("★ 括号外有正文的消息照常处理（不会误伤）", withBody is not null,
            withBody is null ? "(被误伤了)" : "已进上下文");

        // ---- 3) 例外一：@ 了机器人 → 哪怕只有括号也不忽略 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "在的，你说。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30023, "小红", "（真的吗）", 9917, mentionBot: true, ct: cts.Token);
        var mentioned = await WaitForRequestAsync(openAi, r => r.Contains("真的吗"), TimeSpan.FromSeconds(30));
        Check("★ @ 了机器人的括号消息不会被忽略（直接叫它就得理）", mentioned is not null,
            mentioned is null ? "(被忽略了)" : "已进上下文");

        // ---- 4) 例外二：私聊里的括号消息不忽略 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "笑什么呀。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "（笑）", 9918, ct: cts.Token);
        var privateOne = await WaitForRequestAsync(openAi, r => r.Contains("（笑）") || r.Contains("笑"), TimeSpan.FromSeconds(30));
        Check("★ 私聊里同样的括号消息不会被忽略（一对一必须理）", privateOne is not null,
            privateOne is null ? "(被忽略了)" : "已进上下文");

        // ---- 5) 例外三：带图的括号消息不忽略（图本身就是内容）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "这图有点意思。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30024, "小美", "（图）", 9919,
            imageUrl: "http://127.0.0.1:9/none.png", ct: cts.Token);
        await Task.Delay(3000);
        var imageStored = DbProbe.Count(dataDir, "SELECT COUNT(1) FROM messages WHERE text LIKE '%（图）%'");
        Check("★ 带图的括号消息不忽略（会落库）", imageStored >= 1, $"库里 {imageStored} 条");
        // ---- 6) 关掉开关 → 同样的旁白照常进上下文 ----
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using (var offResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
                   new StringContent("""{"ignoreBracketMessages":false}""", Encoding.UTF8, "application/json"), cts.Token))
        {
            Check("★ 面板能关掉这个开关", offResp.IsSuccessStatusCode);
        }

        await Task.Delay(400);
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "笑就笑吧。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30025, "小明", "（笑死）", 9920, ct: cts.Token);
        var afterOff = await WaitForRequestAsync(openAi, r => r.Contains("笑死"), TimeSpan.FromSeconds(30));
        Check("★ 关掉开关后旁白照常处理（开关真的生效）", afterOff is not null,
            afterOff is null ? "(关掉后仍被忽略)" : "已进上下文");

        bot.Dispose();
    }
}
