using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S30 情绪陪伴 + 主动发言：先读懂群里的气氛，再决定“闭嘴还是说句合适的”；
/// 以及没人叫它时，它能不能自己开一句（主动）。
///
/// 号主要求：『更需要人性化陪伴，根据语境精准分析群内成员的情绪选择沉默或者合适的发言』
///           『让其能够主动或者被动自然发言』
///
/// 代码侧的介入（不只是写提示词）：
///   • 吵架 → 门槛抬到 60（不插嘴）；低落/求助 → 门槛降 10（更愿意陪一句）；
///   • 气氛“沉”时不给表情包候选、不发语音；
///   • 读到的气氛记下来，下一轮当底色（有 TTL）；
///   • 群里安静 + 有由头（情绪低落 或 刚聊得热）→ 允许主动开口，同会话还有冷却。
///
/// ⚠ 测试要点：主动/静默兜底会自己产生模型请求，把脚本文案（FIFO）抢走 ——
/// 所以情绪那几步必须先把 idleFallback 关成 0，等进入“主动”那一步再用面板打开。
/// </summary>
public static partial class Program
{
    private static async Task RunCompanionScenarioAsync()
    {
        Section("S30 情绪陪伴：读气氛决定沉默/发言 + 主动开口");

        const int openAiPort = 17837;
        const int botWsPort = 13046;
        const int panelPort = 18111;
        const long groupId = 66721;

        var dataDir = NewDataDir("s30");
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
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_IDLE_FALLBACK"] = "0",     // 情绪那几步要确定性：先关掉，免得抢脚本文案
            ["QQCHAT_PROACTIVE"] = "0",
            // 背景模型调用（画像总结 / 表情包巡检）也会去抢 mock 的脚本文案（FIFO），本场景不需要它们
            ["QQCHAT_PROFILE_SUMMARY"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_AI_DESIRE"] = "50"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        async Task PatchSettingsAsync(string json)
        {
            using var resp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
                new StringContent(json, Encoding.UTF8, "application/json"), cts.Token);
            await resp.Content.ReadAsStringAsync(cts.Token);
            await Task.Delay(300);
        }

        // ---- 1) 有人在倾诉：自评只有 5（低于阈值 10），情绪政策应让它开口 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 5, "vibe": "低落", "vibeNote": "在说今天被领导骂了，情绪很低", "reply": "咋了，谁惹你了？"}""");
        var mark1 = protocol.ActionsReceived.Count;
        await protocol.SendGroupMessageAsync(groupId, 30031, "小美", "@10001 今天好烦啊", 9931, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => openAi.Requests.Count > 0, TimeSpan.FromSeconds(30));
        await Task.Delay(2500);

        var caringSends = protocol.ActionsReceived.Skip(mark1)
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText).ToList();
        Check("★ 有人在低落时，即便自评 5 也会轻轻接一句（门槛为情绪让步）",
            caringSends.Any(t => t.Contains("谁惹你了")),
            string.Join(" | ", caringSends) + "　｜首轮请求：" + (openAi.Requests.Count > 0 ? Snippet(openAi.DescribeRequest(0), "suitability") : "(没有请求)"));
        Check("★ 日志里能看到它读到气氛与采用的策略",
            bot.OutputLines.Any(l => l.Contains("[Vibe]") && l.Contains("低落")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("[Vibe]")).TakeLast(2)));

        // ---- 2) 读到的气氛进下一轮提示词（且低落时不给表情包候选）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 80, "vibe": "低落", "vibeNote": "还是有点丧", "reply": "想吃点啥不？我请。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30031, "小美", "@10001 算了，没事", 9932, mentionBot: true, ct: cts.Token);
        var withHint = await WaitForRequestAsync(openAi, r => r.Contains("你上一条消息时的感觉"), TimeSpan.FromSeconds(30));
        Check("★ 气氛作为“底色”带进下一轮（像人一样记得刚才什么心情）",
            withHint is not null && withHint.Contains("低落"),
            withHint is null ? "(没有气氛底色)" : Snippet(withHint, "你上一条消息时的感觉"));
        Check("★ 低落时不再给表情包候选（不给它发表情包的机会）",
            withHint is not null && !withHint.Contains("表情包候选"),
            withHint is null ? "(无)" : "已抑制");

        // ---- 3) 群里在对线：自评 30（高于基线 10）也该闭嘴 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 30, "vibe": "吵架", "vibeNote": "两个人在对喷，气氛紧张", "reply": "大家都消消气？"}""");
        var mark3 = protocol.ActionsReceived.Count;
        await protocol.SendGroupMessageAsync(groupId, 30032, "老王", "@10001 你说说谁有道理", 9933, mentionBot: true, ct: cts.Token);
        await Task.Delay(3500);
        var conflictSends = protocol.ActionsReceived.Skip(mark3)
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText).ToList();
        Check("★ 群里在对线时不插嘴（门槛被抬到 60，30 分不够）",
            conflictSends.All(t => !t.Contains("消消气")), string.Join(" | ", conflictSends));
        Check("★ 沉默理由写清楚了（气氛 + 评分 + 阈值）",
            bot.OutputLines.Any(l => l.Contains("适合度不足") && l.Contains("气氛 吵架")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("适合度不足")).TakeLast(2)));

        // ---- 4) 主动开口：打开开关（安静 1 秒即算安静），没人叫它时自己说一句 ----
        await PatchSettingsAsync("""{"enableProactive":true,"idleFallbackSeconds":2,"proactiveQuietSeconds":1,"proactiveCooldownSeconds":60}""");

        // 先制造“由头”：一条情绪低落的消息（它的回复是沉默，不影响后面的主动轮）
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 5, "vibe": "低落", "vibeNote": "还有点难受", "reply": ""}""");
        await protocol.SendGroupMessageAsync(groupId, 30033, "小美", "还是有点难受", 9934, ct: cts.Token);
        await Task.Delay(1200);

        // 接下来没人说话 → 等 tick 触发主动；此时脚本文案已空 → mock 回默认回复"默认回复"
        var mark4 = protocol.ActionsReceived.Count;
        await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("[主动]")), TimeSpan.FromSeconds(40));
        await Task.Delay(2000);
        var proactiveSends = protocol.ActionsReceived.Skip(mark4)
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText).ToList();
        Check("★ 没人叫它时，它自己开了一句（主动开口）",
            proactiveSends.Any(t => t.Contains("默认回复")), string.Join(" | ", proactiveSends));
        Check("★ 主动那轮的提示词里写明了“这次是你自己想说话”",
            openAi.Requests.Select((_, i) => openAi.DescribeRequest(i)).Any(r => r.Contains("这次是你自己想说话")),
            "已带上主动标记");
        Check("★ 主动受了冷却约束：不会连发（只这一条）",
            proactiveSends.Count(t => t.Contains("默认回复")) == 1, $"{proactiveSends.Count} 条");

        // ---- 5) 主动时它觉得没话说 → 就不说（不为刷存在感硬说）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 20, "vibe": "中性", "vibeNote": "没什么可说的", "reply": ""}""");
        var mark5 = protocol.ActionsReceived.Count;
        await Task.Delay(9000);
        var quietSends = protocol.ActionsReceived.Skip(mark5)
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText).ToList();
        Check("★ 主动时觉得没话说就保持安静（不为刷存在感硬开口）",
            quietSends.Count == 0, string.Join(" | ", quietSends));

        // ---- 6) 面板能开关主动、也能读到这两项 −---
        await PatchSettingsAsync("""{"enableProactive":false}""");
        var (_, settings) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 面板能关掉主动开口，且能读到安静/冷却两个参数",
            settings.Contains("\"enableProactive\":false") && settings.Contains("\"proactiveQuietSeconds\":1") &&
            settings.Contains("\"proactiveCooldownSeconds\":60"),
            Snippet(settings, "enableProactive"));

        bot.Dispose();
    }
}
