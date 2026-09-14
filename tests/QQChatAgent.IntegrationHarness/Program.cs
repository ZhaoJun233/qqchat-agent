using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// headless 机器人的端到端集成测试：
///   S1  反向 WebSocket：收到群 @ 消息 → 请求模型 → 回发 send_group_msg（带引用）
///   S2  提示词组装：人设 / 人物档案 / 消息格式 是否真的喂给了模型
///   S3  白名单严格模式：名单外的群消息既不落会话也不请求模型
///   S4  模型选择沉默（reply 为空）→ 不发消息
///   S5  长回复分句发送（多段 send_group_msg）
///   S6  正向 WebSocket + 私聊链路 + 重启后会话/档案恢复
///   S7  健康检查端点 /healthz /readyz /status
///   S17 面板内扫码登录（向 NapCat WebUI 取二维码，不靠用户自己找入口）
///   S22 听音乐（识别分享 → 歌词 → 波形分析 → 交给模型）
///   S23 链接预览（群友发的链接真去打开取标题/摘要）
///   S24 语音消息（模型 speak → record 段只给 URL → 面板试听/健康检查）
///   S25 撤回消息（[已撤回] 标记 / 不给模型看原文 / 可评论带冷却）
///   S26 联网搜索（模型自带搜索 grounding / 搜索源兜底 / read 读页面 / SSRF 闸门）
///   S27 老 JSON → SQLite 迁移（不丢数据 / 旧文件留档 / 幂等 / 真的生效）
/// 每个场景用独立进程与独立数据目录，互不干扰。
/// </summary>
public static partial class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// 测试替身固定返回的二维码短链所对应的 SVG 指纹（SHA-256）。
    /// 它把「编码进去的到底是什么」钉死：载荷、纠错级别、安静区宽度任一被改动，这里就会失败。
    /// （已另外用 OpenCV 的 QRCodeDetector 反解过同一份编码结果，确认真的能扫。）
    /// </summary>
    private const string QrSvgFingerprint = "7DA75FAC64F0D0517E5170F1E8D57170BF233A14A16778BE42BFBDBACA387E83";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 忽略
        }

        // 外部模式：只起 mock，不拉子进程。用于验证「真正跑在容器里的机器人」。
        if (args.Contains("--serve-external"))
        {
            return await RunExternalAsync();
        }

        Console.WriteLine($"仓库根目录: {RepoRoot}");
        Console.WriteLine($"机器人 DLL : {BotDll}");
        Console.WriteLine(new string('─', 70));
        try
        {
            // 可选：只跑指定场景，方便定点迭代。例：
            //   $env:QQCHAT_IT_ONLY='s16'; dotnet ...\QQChatAgent.IntegrationHarness.dll
            // 留空 = 全量。
            // 注意：这里必须传「方法组」而不是调用结果。
            // 写成 `await scenario("s1", RunS1Async())` 会先把所有场景启动起来再挑选要等谁 ——
            // 结果就是 QQCHAT_IT_ONLY 形同虚设：其余场景在后台并发跑，抢端口、留僵尸进程。
            async Task Scenario(string tag, Func<Task> run)
            {
                if (ShouldRun(tag))
                {
                    await run();
                }
            }

            await Scenario("s1", RunReverseWsScenarioAsync);
            await Scenario("s3", RunWhitelistScenarioAsync);
            await Scenario("s4", RunSilenceScenarioAsync);
            await Scenario("s5", RunSplitReplyScenarioAsync);
            await Scenario("s6", RunForwardWsAndPersistenceScenarioAsync);
            await Scenario("s7", RunSuitabilityGateScenarioAsync);
            await Scenario("s8", RunProfileScopeScenarioAsync);
            await Scenario("s9", RunProfileSummaryScenarioAsync);
            await Scenario("s10", RunModelOutputScenarioAsync);
            await Scenario("s11", RunRestartMemoryScenarioAsync);
            await Scenario("s12", RunHistoryBackfillScenarioAsync);
            await Scenario("s13", RunLegacyScopeScenarioAsync);
            await Scenario("s14", RunGateResizeScenarioAsync);
            await Scenario("s15", RunRuntimeSettingsScenarioAsync);
            await Scenario("s16", RunAccountOfflineScenarioAsync);
            await Scenario("s17", RunLoginQrScenarioAsync);
            await Scenario("s18", RunStickerScenarioAsync);
            await Scenario("s19", RunReplyQuoteScenarioAsync);
            await Scenario("s20", RunPokeScenarioAsync);
            await Scenario("s21", RunModelConfigScenarioAsync);
        await Scenario("s22", RunMusicScenarioAsync);
        await Scenario("s23", RunLinksScenarioAsync);
        await Scenario("s24", RunVoiceScenarioAsync);
        await Scenario("s25", RunRecallScenarioAsync);
        await Scenario("s26", RunSearchScenarioAsync);
        await Scenario("s27", RunMigrationScenarioAsync);
        }
        catch (Exception ex)
        {
            Fail("harness", "harness 自身异常: " + ex);
        }

        Console.WriteLine(new string('─', 70));
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ═══════════════════ S1 + S2：反向 WS、群聊、提示词组装 ═══════════════════

    private static async Task RunReverseWsScenarioAsync()
    {
        Section("S1/S2 反向 WebSocket · 群消息 · 回复/沉默/引用 · 提示词组装");

        const int openAiPort = 17801;
        const int botWsPort = 13011;
        var dataDir = NewDataDir("s1");

        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 900 };
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "在的，我看下。"}""");
        openAi.EnqueueReply("""{"suitability": 15, "reply": ""}""");                       // 小李插话 → 沉默
        openAi.EnqueueReply("""{"suitability": 80, "reply": "就是登录那块的问题。"}""");
        openAi.EnqueueReply("""{"suitability": 10, "reply": ""}""");                       // 小张闲聊 → 沉默

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_PERSONA"] = "你是群里的技术宅「阿宅」，说话简短带点吐槽。",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SEGMENT_DELAY_MS"] = "10",
            ["QQCHAT_HEALTH_PORT"] = "18011"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        Check("反向 WS 已建立（自定义 RFC6455 握手成功）", protocol.IsConnected);

        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        Check("连接后自动调用 get_login_info 识别登录号", protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "get_login_info"));

        // ① 群友 @ 机器人 → 应回复，且不带引用（紧邻回复）
        await protocol.SendGroupMessageAsync(99999, 20002, "老王", "阿宅在吗？帮我看看这个报错", 7001, mentionBot: true, ct: cts.Token);
        var send1 = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(40));
        Check("群消息触发 send_group_msg", send1 is not null);
        Check("发送目标为群 99999", send1?["params"]?["group_id"]?.GetValue<long>() == 99999);
        Check("回复内容来自模型", MessageText(send1) == "在的，我看下。", MessageText(send1));
        Check("紧邻回复不带 QQ 引用段", QuotedMessageId(send1) is null);

        // ② 群友闲聊（未 @）→ 模型选择沉默
        await protocol.SendGroupMessageAsync(99999, 20003, "小李", "我也想问", 7002, ct: cts.Token);

        // ③ 再 @ 一条，紧接着别人插话 → 回复应带引用（指明回的是哪条）
        await protocol.SendGroupMessageAsync(99999, 20002, "老王", "就是登录那块", 7003, mentionBot: true, ct: cts.Token);
        await Task.Delay(150);
        await protocol.SendGroupMessageAsync(99999, 20004, "小张", "我这也一样", 7004, ct: cts.Token);

        var send2 = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(40), skip: 0);
        await Task.Delay(4000); // 等队列跑完

        var sends = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .ToList();
        Check("共回复 2 次（两次沉默未发言）", sends.Count == 2, $"实际 {sends.Count} 次");
        Check("第 2 次回复内容正确", sends.Count >= 2 && MessageText(sends[1]) == "就是登录那块的问题。", sends.Count >= 2 ? MessageText(sends[1]) : "(缺)");
        Check("被插话后的回复带 QQ 引用，且引用的是触发消息 7003",
            sends.Count >= 2 && QuotedMessageId(sends[1]) == 7003,
            $"引用 id = {QuotedMessageId(sends.Count >= 2 ? sends[1] : null)}");
        Check("4 条活跃消息都送进了模型（由模型自主决定是否发言）", openAi.Requests.Count == 4, $"实际 {openAi.Requests.Count} 次");

        // ---- 提示词断言 ----
        var first = openAi.Requests.FirstOrDefault();
        var last = openAi.Requests.LastOrDefault();
        Check("模型收到了请求", first is not null);

        if (first is not null)
        {
            var system = SystemText(first);
            Check("系统提示包含配置的人设", system.Contains("阿宅"), Truncate(system, 300));
            Check("系统提示注入了机器人 QQ 号", system.Contains("10001"));
            Check("系统提示包含发言决策指令", system.Contains("suitability"));
            Check("请求使用配置的模型名", first["model"]?.GetValue<string>() == "mock-model");
            var users0 = UserTexts(first);
            Check("对方消息按 {发送者}{内容--时间} 格式组装", users0.Any(u => u.Contains("{老王}")), string.Join(" | ", users0.Take(3)));
        }

        if (last is not null)
        {
            var system = SystemText(last);
            Check("系统提示包含人物档案段（认出群友）", system.Contains("会话参与者档案"), Truncate(system, 600));
            Check("人物档案含发送者昵称", system.Contains("老王") || system.Contains("小李") || system.Contains("小张"));
            var users = UserTexts(last);
            Check("上下文累积了历史消息（不只当前一条）", users.Count >= 4, $"user 消息 {users.Count} 条");
        }

        Console.WriteLine();
        Console.WriteLine("───── 实际发给模型的完整请求（最后一次） ─────");
        Console.WriteLine(openAi.DescribeRequest(openAi.Requests.Count - 1));

        await bot.StopAsync();
    }

    // ═══════════════════ S3：白名单严格模式 ═══════════════════

    private static async Task RunWhitelistScenarioAsync()
    {
        Section("S3 白名单严格模式");

        const int openAiPort = 17802;
        const int botWsPort = 13012;
        var dataDir = NewDataDir("s3");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 99, "reply": "不该出现"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999", // 只允许 99999
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // 名单外的群 → 必须被忽略
        await protocol.SendGroupMessageAsync(88888, 20002, "路人", "@机器人 你好", 7101, mentionBot: true, ct: cts.Token);
        await Task.Delay(2500);

        Check("白名单外的群不请求模型", openAi.Requests.Count == 0, $"实际请求 {openAi.Requests.Count} 次");
        Check("白名单外的群不发送消息", !protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg"));

        var storedConversations = DbProbe.Dump(dataDir, "SELECT source_key FROM conversations");
        Check("白名单外的群没有落库会话", !storedConversations.Contains("88888"), Truncate(storedConversations, 200));

        // 名单内的群 → 必须被处理
        await protocol.SendGroupMessageAsync(99999, 20002, "老王", "@机器人 你好", 7102, mentionBot: true, ct: cts.Token);
        var send = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(20));
        Check("白名单内的群正常回复", send is not null);
        // 落库是异步防抖的：立即查库会偶发读到旧内容（S3 曾因此偶发红）。
        var persisted = await WaitUntilAsync(
            () => DbProbe.Dump(dataDir, "SELECT source_key FROM conversations").Contains("99999"),
            TimeSpan.FromSeconds(5));
        Check("白名单内的会话已落库", persisted, "5 秒内没等到会话写库");

        await bot.StopAsync();
    }

    // ═══════════════════ S4：模型选择沉默 ═══════════════════

    private static async Task RunSilenceScenarioAsync()
    {
        Section("S4 模型沉默时不发言");

        const int openAiPort = 17803;
        const int botWsPort = 13013;
        var dataDir = NewDataDir("s4");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 5, "reply": ""}"""); // 明确沉默

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0", // 关掉兜底，确保只测一次
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        await protocol.SendGroupMessageAsync(99999, 20002, "老王", "@机器人 在吗", 7201, mentionBot: true, ct: cts.Token);
        await Task.Delay(4000);

        Check("模型沉默时不发送 QQ 消息", !protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg"));
        Check("沉默时仍然请求了模型（不是没触发）", openAi.Requests.Count >= 1);

        await bot.StopAsync();
    }

    // ═══════════════════ S5：分句发送 ═══════════════════

    private static async Task RunSplitReplyScenarioAsync()
    {
        Section("S5 长回复按句分句发送（含分句上限）");

        const int openAiPort = 17804;
        const int botWsPort = 13014;
        var dataDir = NewDataDir("s5");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "第一句话在这里。第二句话也很长。第三句话继续补充。"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "1",
            ["QQCHAT_SEGMENT_DELAY_MS"] = "10",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        await protocol.SendGroupMessageAsync(99999, 20002, "老王", "@机器人 说说看", 7301, mentionBot: true, ct: cts.Token);
        await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(30));
        await Task.Delay(3000);

        var sends = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .ToList();

        Check("长回复被拆成多段发送", sends.Count >= 2, $"实际 {sends.Count} 段");
        Check("分句后内容完整不丢字", string.Concat(sends) == "第一句话在这里。第二句话也很长。第三句话继续补充。", string.Join(" | ", sends));
        Check("分句后每段只包含一个句子", sends.All(s => s.Count(c => c is '。') <= 1), string.Join(" | ", sends));

        // ---- 标点边界：小数 / 版本号 / 域名 / 连续标点 / 收尾引号都不能被切断 ----
        // 号主反馈“对标点或小数错误分段”：半角点以前无条件当句末。
        const string tricky = "圆周率是 3.14，速度调到 1.5 倍。仓库在 https://github.com/foo/bar 这里，版本 v1.2.3。太厉害了！！！真的「服了。」然后没了。";
        openAi.ClearRequests();
        openAi.EnqueueReply("{\"suitability\": 90, \"reply\": " + System.Text.Json.JsonSerializer.Serialize(tricky) + "}");
        var mark = protocol.ActionsReceived.Count;
        await protocol.SendGroupMessageAsync(99999, 20002, "老王", "@机器人 再说说", 7302, mentionBot: true, ct: cts.Token);
        await Task.Delay(4000);

        var trickySends = protocol.ActionsReceived.Skip(mark)
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .ToList();
        var joined = string.Join(" | ", trickySends);

        Check("★ 内容一字不差（拼接后与原文完全一致）", string.Concat(trickySends) == tricky, joined);
        Check("★ 小数不被切断（3.14 / 1.5 / v1.2.3 完整）",
            trickySends.Any(s => s.Contains("3.14")) && trickySends.Any(s => s.Contains("1.5")) &&
            trickySends.Any(s => s.Contains("v1.2.3")) &&
            !trickySends.Any(s => s.EndsWith("3.") || s.EndsWith("1.") || s.EndsWith("v1.2.")),
            joined);
        Check("★ 域名不被切断（github.com 完整，不会出现以 github. 结尾的段）",
            trickySends.Any(s => s.Contains("github.com")) && !trickySends.Any(s => s.EndsWith("github.")),
            joined);
        Check("★ 连续标点不被拆开（！！！ 与 …… 整块留在同一段）",
            trickySends.Any(s => s.Contains("！！！")),
            joined);
        Check("★ 收尾引号跟着本段（不会有以 」 开头的段）",
            !trickySends.Any(s => s.TrimStart().StartsWith('」')) && trickySends.Any(s => s.Contains("「服了。」")),
            joined);
        Check("★ 段首不会是标点碎片（不会出现以 ，。！？ 开头的段）",
            trickySends.All(s => s.Length > 0 && "，。！？,!".IndexOf(s.TrimStart()[0]) < 0),
            joined);
        Check("段数不超过上限（4 段）", trickySends.Count is > 0 and <= 4, $"{trickySends.Count} 段");

        await bot.StopAsync();
    }

    // ═══════════════════ S6：正向 WS + 私聊 + 持久化 ═══════════════════

    private static async Task RunForwardWsAndPersistenceScenarioAsync()
    {
        Section("S6 正向 WebSocket · 私聊 · 重启后恢复");

        const int openAiPort = 17805;
        const int protocolPort = 13015;
        const int healthPort = 18015;
        var dataDir = NewDataDir("s6");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "收到，我看下。"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.StartForwardServerAsync(protocolPort, cts.Token);

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ForwardWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"ws://127.0.0.1:{protocolPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "*", // 通配：接收全部
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_HEALTH_PORT"] = healthPort.ToString()
        };

        var bot = StartBot(env);
        try
        {
            // 机器人应主动连上 mock 协议端
            var connected = await WaitUntilAsync(() => protocol.IsConnected, TimeSpan.FromSeconds(20));
            Check("正向 WS：机器人主动连上协议端", connected);

            await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(15));
            Check("正向 WS：完成登录号握手", protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "get_login_info"));

            // 健康检查
            var health = await HttpGetAsync($"http://127.0.0.1:{healthPort}/healthz");
            Check("/healthz 返回 200", health.Status == 200, health.Body);
            var ready = await HttpGetAsync($"http://127.0.0.1:{healthPort}/readyz");
            Check("/readyz 已连接时返回 200", ready.Status == 200, ready.Body);
            var status = await HttpGetAsync($"http://127.0.0.1:{healthPort}/status");
            Check("/status 返回运行状态 JSON", status.Status == 200 && status.Body.Contains("\"onebot\""), Truncate(status.Body, 400));
            Check("/status 反映已连接", status.Body.Contains("\"connected\":true"), Truncate(status.Body, 400));

            // 私聊（白名单 * 通配）
            await protocol.SendPrivateMessageAsync(30003, "小美", "机器人帮我看个问题", 7401, cts.Token);
            var send = await protocol.WaitForActionAsync("send_private_msg", TimeSpan.FromSeconds(30));
            Check("私聊消息触发 send_private_msg", send is not null);
            Check("私聊目标为 30003", send?["params"]?["user_id"]?.GetValue<long>() == 30003);
            Check("私聊回复内容正确", MessageText(send) == "收到，我看下。", MessageText(send));

            await Task.Delay(1500); // 等落盘

            // 校验人物档案（现在存在 SQLite 里）
            var profileText = DbProbe.Text(dataDir, "SELECT text FROM member_messages WHERE uid = '30003' LIMIT 1");
            Check("为发送者建立了人物档案", profileText is not null, profileText ?? "(库里没有 30003 的发言)");
            var profileDump = DbProbe.Dump(dataDir,
                "SELECT m.name, mm.text FROM members m LEFT JOIN member_messages mm ON mm.uid = m.uid WHERE m.uid = '30003'");
            Check("档案内容可读（中文正常）", profileDump.Contains("小美") && profileDump.Contains("机器人帮我看个问题"), Truncate(profileDump, 300));
            Check("档案记下了昵称与发言", profileDump.Contains("小美") && profileDump.Contains("|"), Truncate(profileDump, 300));

            Check("会话已落库", DbProbe.Count(dataDir, "SELECT COUNT(1) FROM conversations WHERE source_key = 'private:30003'") == 1);
            var storedRow = DbProbe.Dump(dataDir, "SELECT source_key, sender_id FROM messages WHERE source_key = 'private:30003'");
            Check("落库内容含私聊会话与发送者 QQ", storedRow.Contains("private:30003") && storedRow.Contains("30003"), Truncate(storedRow, 300));
            Check("落库保留了 SenderId（重启后可继续建档案）", storedRow.Contains("30003"), Truncate(storedRow, 400));
        }
        finally
        {
            await bot.StopAsync();
        }

        // ---- 重启：恢复会话，且不再重复拉历史 ----
        Section("S6b 重启后从磁盘恢复会话");
        var bot2 = StartBot(env);
        try
        {
            var log = await WaitForLogLineAsync(bot2, "已恢复", TimeSpan.FromSeconds(20));
            Check("重启后恢复磁盘会话", log is not null, log ?? "(未出现恢复日志)");
            Check("重启后重新连上协议端", await WaitUntilAsync(() => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "get_login_info") >= 2, TimeSpan.FromSeconds(20)));

            var status = await HttpGetAsync($"http://127.0.0.1:{healthPort}/status");
            Check("重启后 /status 显示已恢复会话", status.Body.Contains("\"conversations\":1") || status.Body.Contains("\"conversations\": 1"), Truncate(status.Body, 400));
        }
        finally
        {
            await bot2.StopAsync();
        }
    }

    // ═══════════════════ 外部模式：验证容器里的机器人 ═══════════════════

    /// <summary>
    /// 只启动 mock（监听 0.0.0.0），对「外部已在运行的机器人」发消息并断言。
    /// 用法：先在容器里跑 qqchat，指向 host.docker.internal 上的 mock：
    ///   QQCHAT_BASE_URL=http://host.docker.internal:17899/v1
    ///   QQCHAT_ONEBOT_URL=ws://host.docker.internal:13099
    /// 然后执行：dotnet ... --serve-external
    /// </summary>
    private static async Task<int> RunExternalAsync()
    {
        const int openAiPort = 17899;
        const int protocolPort = 13099;
        var botHealthUrl = Environment.GetEnvironmentVariable("HARNESS_BOT_HEALTH_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(botHealthUrl))
        {
            botHealthUrl = "http://127.0.0.1:18080";
        }

        Section("外部模式：验证容器化部署的机器人");

        using var openAi = new MockOpenAi(openAiPort, bind: "+");
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "容器里跑起来了。"}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var protocol = new MockProtocol { SelfId = 42001 };
        await protocol.StartForwardServerAsync(protocolPort, cts.Token, bind: "+");
        Console.WriteLine($"  · mock 模型已监听 :{openAiPort}（base url 对外用 http://<harness主机>:17899/v1）");
        Console.WriteLine($"  · mock 协议端已监听 :{protocolPort}（对外用 ws://<harness主机>:13099）");

        // 等容器里的机器人连入
        var connected = await WaitUntilAsync(() => protocol.IsConnected, TimeSpan.FromSeconds(30));
        Check("容器里的机器人已连上协议端", connected);
        if (!connected)
        {
            Console.WriteLine("  · 提示：确认机器人已启动且 QQCHAT_ONEBOT_URL 指向本 mock");
            return _failed == 0 ? 0 : 1;
        }

        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(15));
        Check("容器内完成登录号握手", protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "get_login_info"));

        // 健康端点
        var health = await HttpGetAsync($"{botHealthUrl}/healthz");
        Check("容器 /healthz 可达且 200", health.Status == 200, health.Body);
        var ready = await HttpGetAsync($"{botHealthUrl}/readyz");
        Check("容器 /readyz 已连接时 200", ready.Status == 200, ready.Body);

        // 群消息 → 回复
        await protocol.SendGroupMessageAsync(12321, 55001, "容器测试", "机器人你在容器里吗", 9001, mentionBot: true, ct: cts.Token);
        var send = await protocol.WaitForActionAsync("send_group_msg", TimeSpan.FromSeconds(40));
        Check("容器内收到群消息并回复", send is not null);
        Check("回复内容正确（模型请求跑通了）", MessageText(send) == "容器里跑起来了。", MessageText(send));
        Check("容器到宿主 mock 模型的 HTTP 请求成功", openAi.Requests.Count >= 1, $"实际 {openAi.Requests.Count} 次");

        if (openAi.Requests.FirstOrDefault() is { } req)
        {
            Check("容器内提示词组装正常（带发送者）", SystemText(req).Contains("容器测试") || UserTexts(req).Any(u => u.Contains("容器测试")),
                string.Join(" | ", UserTexts(req)));
        }

        Section("外部模式结果");
        Console.WriteLine($"  通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ═══════════════════ S7：suitability 阈值硬门槛 ═══════════════════

    /// <summary>回归：以前 SuitabilityThreshold 只写在提示词里、代码不执行。
    /// 现在必须真的拦住“自评低于阈值”的回复。</summary>
    private static async Task RunSuitabilityGateScenarioAsync()
    {
        Section("S7 发言适合度阈值真正生效（代码侧拦截）");

        const int openAiPort = 17806;
        const int botWsPort = 13016;
        var dataDir = NewDataDir("s7");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 5, "reply": "低分不该发出去"}""");   // 低于阈值 10
        openAi.EnqueueReply("""{"suitability": 90, "reply": "高分应该发出去"}""");  // 高于阈值
        openAi.EnqueueReply("""就是不想用 JSON 格式的纯文本回复""");                    // 非 JSON → 不得拦

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SUITABILITY_THRESHOLD"] = "10",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        async Task SendAsync(string text, long mid)
        {
            await protocol.SendGroupMessageAsync(99999, 20002, "老王", text, mid, mentionBot: true, ct: cts.Token);
            await Task.Delay(1200);
        }

        await SendAsync("第一句", 8001);   // 模型自评 5  → 必须沉默
        await SendAsync("第二句", 8002);   // 模型自评 90 → 必须发言
        await SendAsync("第三句", 8003);   // 非 JSON 文本 → 必须发言
        await Task.Delay(2000);

        var sends = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .ToList();

        Check("自评 5 < 阈值 10 → 被拦下，不发", !sends.Contains("低分不该发出去"), string.Join(" | ", sends));
        Check("自评 90 ≥ 阈值 10 → 正常发出", sends.Contains("高分应该发出去"), string.Join(" | ", sends));
        Check("模型没按 JSON 输出时不误杀（当普通回复）", sends.Contains("就是不想用 JSON 格式的纯文本回复"), string.Join(" | ", sends));
        Check("共只发出 2 条", sends.Count == 2, $"实际 {sends.Count} 条：{string.Join(" | ", sends)}");

        await bot.StopAsync();
    }

    // ═══════════════════ S8：人物档案按会话隔离 ═══════════════════

    /// <summary>回归：档案以前按 QQ 号全局聚合，A 群的发言会泄露进 B 群的上下文。</summary>
    private static async Task RunProfileScopeScenarioAsync()
    {
        Section("S8 人物档案按会话隔离（不跨群串味）");

        const int openAiPort = 17807;
        const int botWsPort = 13017;
        const long groupA = 88881;
        const long groupB = 88882;
        var dataDir = NewDataDir("s8");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        for (var i = 0; i < 40; i++)
        {
            // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = $"{groupA},{groupB}",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_MAX_CONTEXT"] = "5",          // 上下文只留 5 条 → 更早的才进档案
            ["QQCHAT_PROFILE_LINES"] = "20",
            ["QQCHAT_PROFILE_CHARS"] = "100000",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // A 群：12 条，第一条是机密暗号（超出 5 条窗口后应进档案）
        for (var i = 0; i < 12; i++)
        {
            var text = i == 0 ? "A群机密暗号" : $"A群第{i}句闲聊";
            await protocol.SendGroupMessageAsync(groupA, 20002, "老王", text, 8100 + i, mentionBot: true, ct: cts.Token);
        }
        await Task.Delay(3000);

        // B 群：12 条，第一条是另一个暗号
        for (var i = 0; i < 12; i++)
        {
            var text = i == 0 ? "B群机密暗号" : $"B群第{i}句闲聊";
            await protocol.SendGroupMessageAsync(groupB, 20002, "老王", text, 8200 + i, mentionBot: true, ct: cts.Token);
        }
        await Task.Delay(3000);

        // 取「在 B 群发出的最后一次请求」的提示词（按消息内容定位）
        var bRequests = openAi.Requests
            .Where(r => UserTexts(r).Any(u => u.Contains("B群")))
            .ToList();
        var aRequests = openAi.Requests
            .Where(r => UserTexts(r).Any(u => u.Contains("A群")))
            .ToList();

        Check("两个群都产生了模型请求", bRequests.Count > 0 && aRequests.Count > 0,
            $"A={aRequests.Count} B={bRequests.Count}");

        var lastB = bRequests.LastOrDefault();
        var lastA = aRequests.LastOrDefault();

        if (lastB is not null && lastA is not null)
        {
            var sysB = SystemText(lastB);
            var sysA = SystemText(lastA);

            Check("B 群提示词里带了档案段（本群更早的发言）", sysB.Contains("本群更早的发言"),
                "参与者/档案段：" + ParticipantsSection(lastB));            Check("B 群档案含本群更早的消息", sysB.Contains("B群机密暗号"), Truncate(sysB, 600));
            Check("★ B 群提示词绝不包含 A 群的发言", !sysB.Contains("A群机密暗号"), "泄露了：" + Truncate(sysB, 600));

            Check("A 群提示词里带了档案段", sysA.Contains("本群更早的发言"), Truncate(sysA, 600));
            Check("★ A 群提示词绝不包含 B 群的发言", !sysA.Contains("B群机密暗号"), "泄露了：" + Truncate(sysA, 600));

            Check("档案不再重复注入上下文里已有的消息",
                !sysB.Contains("B群第11句闲聊"), Truncate(sysB, 400));
        }

        await bot.StopAsync();
    }

    // ═══════════════════ S9：长期记忆 · 画像摘要 ═══════════════════

    /// <summary>验证：堆积的历史发言会被模型压缩成画像，且后续请求注入的是“画像”而不是“一堆原文”。</summary>
    private static async Task RunProfileSummaryScenarioAsync()
    {
        Section("S9 长期记忆：发言 → 画像 → 注入");

        const int openAiPort = 17808;
        const int botWsPort = 13018;
        const long groupId = 77771;
        var dataDir = NewDataDir("s9");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 前 N 条喂给聊天；中间穿插画像请求（mock 按“是否为画像提示词”自动分派）
        for (var i = 0; i < 40; i++)
        {
            // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
        }

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
            ["QQCHAT_MAX_CONTEXT"] = "5",
            ["QQCHAT_PROFILE_LINES"] = "20",
            ["QQCHAT_PROFILE_CHARS"] = "100000",
            ["QQCHAT_PROFILE_SUMMARY"] = "1",
            ["QQCHAT_PROFILE_SUMMARY_THRESHOLD"] = "8",
            ["QQCHAT_PROFILE_SUMMARY_INTERVAL"] = "5", // 5 秒巡检一次，测试友好
            ["QQCHAT_PROFILE_SUMMARY_CHARS"] = "80",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // 12 条有信息量的历史（足够触发阈值 8）
        for (var i = 0; i < 12; i++)
        {
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", $"老王的历史发言第{i}句", 8300 + i, mentionBot: true, ct: cts.Token);
        }

        // 等画像巡检落地（间隔 5 秒）
        await Task.Delay(14000);

        var profileDump2 = DbProbe.Dump(dataDir, "SELECT scope, text, through_seq FROM member_summaries WHERE uid = '20002'");
        Check("库里已写入画像（Summaries）", profileDump2.Length > 0, Truncate(profileDump2, 400));
        Check("画像记下了折叠到的序号 ThroughSeq", DbProbe.Count(dataDir, "SELECT COUNT(1) FROM member_summaries WHERE uid = '20002' AND through_seq <> 0") > 0, Truncate(profileDump2, 400));

        // 再来一条新消息 → 请求里应带上“画像”字样，且不再重复成堆的已折叠原文
        openAi.EnqueueReply("""{"suitability": 99, "reply": "收到"}""");
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "新的问题来了", 8399, mentionBot: true, ct: cts.Token);
        await Task.Delay(4000);

        var last = openAi.Requests.LastOrDefault(r => UserTexts(r).Any(u => u.Contains("新的问题来了")));
        Check("触发了新的模型请求", last is not null);

        if (last is not null)
        {
            var sys = SystemText(last);
            Check("★ 注入里出现了“画像”字段", sys.Contains("画像："), Truncate(sys, 700));

            var headerChars = sys.Length;
            Check("画像确实压缩了体积（系统提示 < 2500 字）", headerChars < 2500, $"实际 {headerChars} 字");

            var foldedCount = sys.Split("老王的历史发言").Length - 1;
            Check("已折叠的原文不再重复注入（最多只剩未折叠的尾部）", foldedCount <= 4,
                $"提示词里出现了 {foldedCount} 条已折叠原文");
        }

        // 状态端点可观测
        await bot.StopAsync();
    }

    // ═══════════════════ S10：模型输出解析（含浮点评分回归） ═══════════════════

    /// <summary>
    /// 回归：旧实现用 GetInt32() 读 suitability，模型回 85.0 时抛异常 → 被 catch 吞掉 →
    /// 整段 JSON 被当成“普通文本回复”发进群里。
    /// </summary>
    private static async Task RunModelOutputScenarioAsync()
    {
        Section("S10 模型输出解析（浮点评分 / 残缺 JSON / 带花括号的纯文本）");

        const int openAiPort = 17809;
        const int botWsPort = 13019;
        const long groupId = 66661;
        var dataDir = NewDataDir("s10");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        // 1) 浮点评分：以前这里会把整段 JSON 当回复发出去
        openAi.EnqueueReply("""{"suitability": 85.0, "reply": "浮点评分也要正常发出"}""");
        // 2) 残缺 JSON：应沉默，而不是把 JSON 原文发出来
        openAi.EnqueueReply("""{"suitability": 90, "reply": """);
        // 3) 纯文本但含花括号：必须照原样发出（不能因为看起来像 JSON 就丢掉）
        openAi.EnqueueReply("这题答案就是 {a:1} 这种写法");
        // 4) 字符串评分：要能解析
        openAi.EnqueueReply("""{"suitability": "30", "reply": "字符串评分也要认"}""");
        // 5) 浮点评分低于阈值（3.5 → 4 < 10）：沉默
        openAi.EnqueueReply("""{"suitability": 3.5, "reply": "低于阈值不该发"}""");
        // 6) ```json 代码块围栏
        openAi.EnqueueReply("```json\n{\"suitability\": 90, \"reply\": \"代码块围栏也要能解析\"}\n```");
        // 7) 上游偶发只回一个字符（非 JSON）：群里实测单字“悼”刷屏的源头 → 必须当沉默
        openAi.EnqueueReply("悼");
        // 8) 同样的单字但走 JSON 协议：这是模型的明确决定，照发
        openAi.EnqueueReply("""{"suitability": 95, "reply": "好的，我看看"}""");
        // 9) 紧接着又发一模一样的一句 → 复读守卫必须拦下（否则就是自己刷屏）
        openAi.EnqueueReply("""{"suitability": 95, "reply": "好的，我看看"}""");
        // 10) 单字走 JSON，但不在正常应答白名单里（“悼”）：同样按噪声处理
        openAi.EnqueueReply("""{"suitability": 95, "reply": "悼"}""");
        // 11) 单字正常应答（“嗯”）：中文口语里就是这么用的，必须放行
        openAi.EnqueueReply("""{"suitability": 95, "reply": "嗯"}""");
        // 12) ★ 线上实测（2026-09-13）：JSON 前面先写一句解释 ——
        //     以前这种“不以 { 开头”的输出会走纯文本分支，把整段 JSON 连代码块一起发进群里
        openAi.EnqueueReply("好的，我来回：\n{\"suitability\": 90, \"reply\": \"前面带解释也要能解析\"}");
        // 13) ★ 围栏前后还有别的文字
        openAi.EnqueueReply("我来回一下：\n```json\n{\"suitability\": 90, \"reply\": \"围栏前后有文字也要能解析\"}\n```\n就这样。");
        // 14) ★ 想输出 JSON 但括号/引号坏了 → 宁可沉默，也绝不把代码发进群
        openAi.EnqueueReply("{\"suitability\": 90, \"reply\": \"这段不能发出去\"");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
            ["QQCHAT_SPLIT_REPLIES"] = "0", // 避免分句干扰逐条断言
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        for (var i = 0; i < 14; i++)
        {
            await protocol.SendGroupMessageAsync(groupId, 30003, "小明", $"第{i}个测试输入", 9100 + i, mentionBot: true, ct: cts.Token);
            await Task.Delay(700);
        }

        await Task.Delay(2500);

        var sends = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(MessageText)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        Check("浮点评分不抛异常（回复是正文，不是 JSON）",
            sends.Contains("浮点评分也要正常发出"), string.Join(" | ", sends));

        Check("★ 任何情况下都不把 JSON 当消息发出（C1 回归）",
            !sends.Any(s => s.Contains("suitability")), string.Join(" | ", sends));

        Check("纯文本里的花括号照原样发出",
            sends.Contains("这题答案就是 {a:1} 这种写法"), string.Join(" | ", sends));

        Check("字符串评分能解析（30 ≥ 阈值 10）",
            sends.Contains("字符串评分也要认"), string.Join(" | ", sends));

        Check("浮点评分 3.5 低于阈值 → 沉默",
            !sends.Contains("低于阈值不该发"), string.Join(" | ", sends));

        Check("```json 围栏能剥掉",
            sends.Contains("代码块围栏也要能解析"), string.Join(" | ", sends));

        // 回归：群里“悼”刷屏 —— 上游截断回单字（非 JSON）被当正常回复发了出去，
        // 之后模型又读到自己那条“悼”开始反复重发。
        Check("★ 单字碎片（非 JSON）不上群（“悼”刷屏的源头）",
            !sends.Any(s => s.Trim() == "悼"), string.Join(" | ", sends));

        Check("★ 单字噪声（走 JSON 也不发，白名单外）",
            !sends.Any(s => s.Trim() == "悼"), string.Join(" | ", sends));

        Check("单字正常应答（“嗯”）照发",
            sends.Any(s => s.Trim() == "嗯"), string.Join(" | ", sends));

        Check("★ 紧接上一句一字不差 → 复读守卫拦下（只发一次）",
            sends.Count(s => s.Trim() == "好的，我看看") == 1, string.Join(" | ", sends));

        Check("★ JSON 前面带一句解释也当 JSON 解析（线上吐代码的那个场景）",
            sends.Contains("前面带解释也要能解析"), string.Join(" | ", sends));

        Check("★ 围栏前后还有别的文字也能解析出正文",
            sends.Contains("围栏前后有文字也要能解析"), string.Join(" | ", sends));

        Check("★ 格式坏掉的 JSON 一律沉默，绝不把代码发进群",
            !sends.Any(s => s.Contains("这段不能发出去")) && !sends.Any(s => s.Contains("```")), string.Join(" | ", sends));

        Check("共只发出 8 条", sends.Count == 8, $"实际 {sends.Count} 条：{string.Join(" | ", sends)}");

        await bot.StopAsync();
    }

    // ═══════════════════ S11：重启后记忆不冻结 ═══════════════════

    /// <summary>
    /// 回归：会话序号以前不落盘，重启后从头计数 → 小于画像里已记录的 ThroughSeq，
    /// 于是该成员的新发言既不注入、也不参与摘要。
    /// 本场景故意把会话上限调小，让“磁盘上的序号”与“重启后的计数”产生差距。
    /// </summary>
    private static async Task RunRestartMemoryScenarioAsync()
    {
        Section("S11 重启后长期记忆继续推进（序号持久化）");

        const int openAiPort = 17810;
        const int botWsPort = 13020;
        const long groupId = 66662;
        var dataDir = NewDataDir("s11");

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = $"http://127.0.0.1:{openAiPort}/v1",
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_MAX_MESSAGES"] = "20", // 故意很小：让序号超出恢复后的计数
            ["QQCHAT_PROFILE_SUMMARY"] = "1",
            ["QQCHAT_PROFILE_SUMMARY_THRESHOLD"] = "5",
            ["QQCHAT_PROFILE_SUMMARY_INTERVAL"] = "3",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        {
            using var openAi = new MockOpenAi(openAiPort);
            openAi.Start();
            for (var i = 0; i < 40; i++)
            {
                // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
            }

            using var bot = StartBot(env);
            await WaitForPortAsync(botWsPort, cts.Token, bot);

            using var protocol = new MockProtocol { SelfId = 10001 };
            await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
            await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

            for (var i = 0; i < 30; i++)
            {
                await protocol.SendGroupMessageAsync(groupId, 20002, "老王", $"重启前第{i}句", 9200 + i, mentionBot: true, ct: cts.Token);
            }

            await Task.Delay(9000);
            await bot.StopAsync();
            await Task.Delay(1200);  // 等落盘
        }

        var throughSeqBefore = ReadThroughSeq(dataDir, "20002");
        Check("重启前已生成画像（ThroughSeq > 0）", throughSeqBefore > 0, $"ThroughSeq={throughSeqBefore}");

        // ---- 重启：同一数据目录 ----
        using (var openAi2 = new MockOpenAi(openAiPort))
        {
            openAi2.Start();
            for (var i = 0; i < 20; i++)
            {
                // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi2.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
            }

            using var bot2 = StartBot(env);
            await WaitForPortAsync(botWsPort, cts.Token, bot2);

            using var protocol2 = new MockProtocol { SelfId = 10001 };
            await protocol2.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
            await protocol2.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

            for (var i = 0; i < 6; i++)
            {
                await protocol2.SendGroupMessageAsync(groupId, 20002, "老王", $"重启后第{i}句", 9300 + i, mentionBot: true, ct: cts.Token);
            }

            await Task.Delay(9000);

            var throughSeqAfter = ReadThroughSeq(dataDir, "20002");
            Check("★ 重启后画像继续推进（记忆不冻结，C2 回归）",
                throughSeqAfter > throughSeqBefore,
                $"重启前 {throughSeqBefore} → 重启后 {throughSeqAfter}；" +
                $"库中档案发言={DbProbe.TableCount(dataDir, "member_messages")}、消息={DbProbe.TableCount(dataDir, "messages")}；" +
                $"bot2 日志：{string.Join(" | ", bot2.OutputLines.Where(l => l.Contains("Store") || l.Contains("失败")).TakeLast(3))}");

            var prompt = openAi2.Requests.LastOrDefault();
            if (prompt is null)
            {
                Check("重启后仍然注入档案", false, "重启后没有任何模型请求");
            }
            else
            {
                Check("重启后仍然注入档案", SystemText(prompt).Contains("老王"), Truncate(SystemText(prompt), 400));
            }

            await bot2.StopAsync();
        }
    }

    /// <summary>读某人画像折叠到的最大序号（库里查；没有则返回 0）。</summary>
    private static long ReadThroughSeq(string dataDir, string uid)
        => DbProbe.Count(dataDir, "SELECT COALESCE(MAX(through_seq), 0) FROM member_summaries WHERE uid = $uid", ("$uid", uid));

    // ═══════════════════ S12：历史补录真的进得了模型 ═══════════════════

    /// <summary>
    /// 两件事：
    ///   1) 补录的群历史不能被“插入即被裁剪”吃掉（旧实现会谎报成功）
    ///   2) 补录历史在时间上比现有消息更早 → 序号必须比现有最小值更小，
    ///      否则它们在“序号 = 位置”的语义下变成“最新的”，既进不了上下文窗口也进不了档案。
    /// </summary>
    private static async Task RunHistoryBackfillScenarioAsync()
    {
        Section("S12 群历史补录：留得住 + 看得见 + 面板可见");

        const int openAiPort = 17811;
        const int botWsPort = 13021;
        const int panelPort = 18085;
        const long groupId = 66663;
        var dataDir = NewDataDir("s12");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        for (var i = 0; i < 20; i++)
        {
            // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
            ["QQCHAT_MAX_CONTEXT"] = "5",    // 窗口很小 → 补录历史不会出现在上下文里
            ["QQCHAT_PROFILE_LINES"] = "20",
            ["QQCHAT_PROFILE_CHARS"] = "100000",
            ["QQCHAT_PROFILE_SUMMARY"] = "0", // 关掉画像，纯看原文注入
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };

        // 补录历史：同一个发送者（这样它的档案才会被查到），列表下标 0 = 最新
        for (var i = 0; i < 8; i++)
        {
            protocol.GroupHistory.Add((70001 + i, 20002, "老王", $"补录历史第{i}句"));
        }

        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // 第一条消息触发历史补录
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "触发补录的那条", 9400, mentionBot: true, ct: cts.Token);
        await Task.Delay(4000);

        // 补录完成后，再发一条 —— 它的提示词才应该带上补录历史
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "补录之后的提问", 9401, mentionBot: true, ct: cts.Token);
        await Task.Delay(4000);

        var historyCalls = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "get_group_msg_history");
        Check("确实请求了群历史", historyCalls >= 1, $"调用 {historyCalls} 次");

        // 1) 补录消息留在会话里（没被插入即裁剪）
        var (status, body) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/conversations/group:{groupId}/messages?limit=100");
        Check("会话消息接口可访问", status == 200, $"HTTP {status}");

        var kept = Enumerable.Range(0, 8).Count(i => body.Contains($"补录历史第{i}句"));
        Check("★ 补录的 8 条历史全部留在会话里（H3 回归）", kept == 8, $"只找到 {kept}/8 条");

        // 2) 补录历史对模型可见（走档案路径 —— 因为窗口只有 5 条）
        var last = openAi.Requests.LastOrDefault(r => UserTexts(r).Any(u => u.Contains("补录之后的提问")));
        if (last is null)
        {
            Check("模型收到了补录之后的提问", false, "没有对应请求");
        }
        else
        {
            Check("模型收到了补录之后的提问", true);

            var sys = SystemText(last);
            var visible = Enumerable.Range(0, 8).Count(i => sys.Contains($"补录历史第{i}句"));
            Check("★ 补录历史对模型可见（H3 回归）", visible > 0, $"提示词里出现 {visible} 条：{Truncate(sys, 500)}");

            // ★ 真正有区分度的那条：
            // 补录历史在时间上更早，所以它的序号必须比现有消息更小。
            // 旧实现给它们分配 ++_nextSeq（比现有消息更大），序号顺序与列表顺序相反，
            // 导致“这条是否已在上下文窗口里”的判断失效 → 同一条消息既在 user 消息里、
            // 又在 system 档案里被重复注入（白花 token，还让模型以为你说了两遍）。
            var users = UserTexts(last);
            var duplicated = new[] { "触发补录的那条", "补录之后的提问" }
                .Where(t => sys.Contains(t) && users.Any(u => u.Contains(t)))
                .ToList();

            Check("★ 上下文里已有的消息不再被档案重复注入（序号语义回归）",
                duplicated.Count == 0, $"重复注入：{string.Join(" | ", duplicated)}");
        }

        // 3) 面板能看到群里的档案（以前只看得到私聊记录）
        var (pStatus, pBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/profiles/20002");
        Check("成员档案接口可访问", pStatus == 200, $"HTTP {pStatus}");
        Check("★ 面板能看到群聊里的档案（M1 回归）", pBody.Contains("补录历史第"), Truncate(pBody, 400));

        await bot.StopAsync();
    }

    // ═══════════════════ S13：旧数据不再按群名串味 ═══════════════════

    /// <summary>
    /// 回归：旧档案记录没有 GroupId 只有 GroupName。以前会按群名兜底匹配，
    /// 而群名不唯一（“相亲相爱一家人”遍地都是）→ 别的群的发言会被注入本群。
    /// 现在一律视为“不属于任何群范围”。
    /// </summary>
    private static async Task RunLegacyScopeScenarioAsync()
    {
        Section("S13 旧档案（无 GroupId）不再按群名串味");

        const int openAiPort = 17812;
        const int botWsPort = 13022;
        const long groupId = 66664;
        var dataDir = NewDataDir("s13");

        // 手工伪造一份“旧格式”档案：有群名、没有 GroupId，且是别的群的老消息
        var profileDir = Path.Combine(dataDir, "data", "member_profiles");
        Directory.CreateDirectory(profileDir);
        var oldTime = DateTimeOffset.Now.AddHours(-2).ToUnixTimeSeconds();
        await File.WriteAllTextAsync(
            Path.Combine(profileDir, "20002.json"),
            "{\"Uid\":\"20002\",\"Name\":\"老王\",\"Messages\":[" +
            $"{{\"Text\":\"★别的群的机密暗号★\",\"TimeUnix\":{oldTime},\"GroupName\":\"同名群\",\"GroupId\":0,\"Seq\":0}}" +
            "],\"Summaries\":[]}");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        for (var i = 0; i < 10; i++)
        {
            // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
            ["QQCHAT_PROFILE_LINES"] = "20",
            ["QQCHAT_PROFILE_CHARS"] = "100000",
            ["QQCHAT_PROFILE_SUMMARY"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        // 本群群名故意和旧档案里记的群名完全相同
        using var protocol = new MockProtocol { SelfId = 10001, GroupName = "同名群" };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "本群的正常提问", 9500, mentionBot: true, ct: cts.Token);
        await Task.Delay(4000);

        var last = openAi.Requests.LastOrDefault(r => UserTexts(r).Any(u => u.Contains("本群的正常提问")));
        if (last is null)
        {
            Check("产生了模型请求", false, "没有对应请求");
        }
        else
        {
            Check("产生了模型请求", true);
            Check("★ 同名群不串味：别的群的旧记录绝不注入（M2 回归）",
                !SystemText(last).Contains("★别的群的机密暗号★"), Truncate(SystemText(last), 600));
        }

        await bot.StopAsync();
    }

    // ═══════════════════ S14：并发闸门热变更不会卡死会话 ═══════════════════

    /// <summary>
    /// 回归：以前改并发数会 Dispose 旧闸门，而在途 worker 的 Release() 会抛
    /// ObjectDisposedException —— 它落在 finally 里，会让后面的 _inFlight 清理被跳过，
    /// 结果是该会话**永久不再回复**。
    /// </summary>
    private static async Task RunGateResizeScenarioAsync()
    {
        Section("S14 并发上限热变更后会话不卡死");

        const int openAiPort = 17813;
        const int botWsPort = 13023;
        const int panelPort = 18086;
        const long groupId = 66665;
        var dataDir = NewDataDir("s14");

        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 2000 };
        openAi.Start();
        for (var i = 0; i < 10; i++)
        {
            // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
            ["QQCHAT_CONCURRENCY"] = "1",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // 第一条：模型要 2 秒才回 → 让它待在闸门里
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "闸门变更前的问题", 9600, mentionBot: true, ct: cts.Token);
        await Task.Delay(600);

        // 变更并发上限（以前会 Dispose 掉正在被持有的闸门）
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            var payload = new StringContent("{\"maxConcurrentReplies\": 3}", Encoding.UTF8, "application/json");
            var res = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings", payload, cts.Token);
            Check("并发上限变更成功", res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode}");
        }

        // 第二条：如果会话被卡死，这条永远不会被处理
        await protocol.SendGroupMessageAsync(groupId, 20002, "老王", "闸门变更后的问题", 9601, mentionBot: true, ct: cts.Token);
        await Task.Delay(12000);

        var sends = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        Check("★ 闸门热变更后仍能继续回复（不卡死，H1 回归）", sends >= 2, $"只发出 {sends} 条");

        var (status, body) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/status");
        Check("在途请求已归零（没有 _inFlight 泄漏）",
            status == 200 && body.Replace(" ", "").Contains("\"inFlightReplies\":0"), Truncate(body, 400));

        await bot.StopAsync();
    }

    // ═══════════════════ S15：面板改设置必须立即生效 ═══════════════════

    /// <summary>
    /// 回归：定时器（画像巡检 / 静默兜底）以前只在 Start() 里创建一次，
    /// 于是面板里开「启用画像摘要」或改「巡检间隔」**永远不生效，得重启** ——
    /// 用起来就像“设置保存不了”。
    /// 本场景：一开始关着画像，运行中通过 /api/settings 打开 → 必须真的跑起来。
    /// 同时验证把会话上限改小会立即裁剪。
    /// </summary>
    private static async Task RunRuntimeSettingsScenarioAsync()
    {
        Section("S15 面板改设置立即生效（定时器重建 + 立即裁剪）");

        const int openAiPort = 17814;
        const int botWsPort = 13024;
        const int panelPort = 18087;
        const long groupId = 66666;
        var dataDir = NewDataDir("s15");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        for (var i = 0; i < 60; i++)
        {
            // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
        }

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
            ["QQCHAT_PROFILE_SUMMARY"] = "0",        // 一开始关着
            ["QQCHAT_PROFILE_SUMMARY_INTERVAL"] = "0",
            ["QQCHAT_MAX_MESSAGES"] = "200",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        for (var i = 0; i < 30; i++)
        {
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", $"改设置前第{i}句", 9700 + i, mentionBot: true, ct: cts.Token);
        }

        await Task.Delay(5000);
        Check("初始关闭画像时不生成画像", ReadThroughSeq(dataDir, "20002") == 0, $"ThroughSeq={ReadThroughSeq(dataDir, "20002")}");

        var (s0, m0) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/conversations/group:{groupId}/messages?limit=999");
        var countBefore = s0 == 200 ? System.Text.RegularExpressions.Regex.Matches(m0, "\\\"role\\\":").Count : -1;
        Check("改小上限前消息数远大于 20", countBefore > 20, $"当前 {countBefore} 条");

        // ---- 运行中打开画像摘要 + 把会话上限改到最小值 20 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            // 注意：maxMessagesPerConversation 服务端下限是 20
            var body = "{\"enableProfileSummary\": true, \"profileSummaryIntervalSeconds\": 3, \"profileSummaryThreshold\": 5, \"maxMessagesPerConversation\": 20}";
            var res = await http.PostAsync(
                $"http://127.0.0.1:{panelPort}/api/settings",
                new StringContent(body, Encoding.UTF8, "application/json"),
                cts.Token);
            Check("运行中改设置成功", res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode}");
        }

        // 立即裁剪：不等下一条消息，会话条数就应降到 20
        var (s1, m1) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/conversations/group:{groupId}/messages?limit=999");
        var countAfterShrink = s1 == 200 ? System.Text.RegularExpressions.Regex.Matches(m1, "\\\"role\\\":").Count : -1;
        Check("★ 改小会话上限立即裁剪（不用等新消息）", countAfterShrink == 20, $"{countBefore} → {countAfterShrink}（期望 20）");

        // 再发几条新消息 → 新开的画像定时器应该把它们折进画像
        for (var i = 0; i < 8; i++)
        {
            await protocol.SendGroupMessageAsync(groupId, 20002, "老王", $"改设置后第{i}句", 9800 + i, mentionBot: true, ct: cts.Token);
        }

        await Task.Delay(14000); // 定时器首次 3 秒、之后每 3 秒

        var through = ReadThroughSeq(dataDir, "20002");
        Check("★ 运行中开启的画像巡检真的跑起来了（定时器重建，回归）",
            through > 0, $"ThroughSeq={through}（修复前永远是 0，要重启才行）");

        await bot.StopAsync();
    }

    // ═══════════════════ S16：账号掉线必须被发现 ═══════════════════

    /// <summary>
    /// 回归：“WebSocket 连着”不等于“QQ 账号在线”。
    /// 登录失效/被顶号时连接照旧、get_login_info 照旧回显 UIN，
    /// 但消息一条都收不到 —— 以前面板会一直显示“已连接”，全靠人猜。
    /// 现在必须能从 /status 看出账号已离线。
    /// </summary>
    private static async Task RunAccountOfflineScenarioAsync()
    {
        Section("S16 协议端连着但账号掉线 → 面板必须说实话");

        const int openAiPort = 17815;
        const int botWsPort = 13025;
        const int panelPort = 18088;
        const long groupId = 66667;
        var dataDir = NewDataDir("s16");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();
        for (var i = 0; i < 10; i++)
        {
            // 每条都不一样：连续两条一字不差会被“复读守卫”拦下（见 S10），
            // 那会让本场景要验证的东西被静默掉
            openAi.EnqueueReply($$"""{"suitability": 99, "reply": "ok-{{i + 1}}"}""");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001, AccountOnline = true };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));
        await protocol.WaitForActionAsync("get_status", TimeSpan.FromSeconds(15));

        await Task.Delay(1500);
        var (s1, b1) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/status");
        var onlineCompacted = b1.Replace(" ", "");
        Check("在线时 /status 报告 online=true",
            s1 == 200 && onlineCompacted.Contains("\"online\":true"), Truncate(b1, 400));

        // 模拟被顶下线：连接一切正常，只是账号离线
        protocol.AccountOnline = false;

        // 等一轮探测（定时器 8 秒首探 / 30 秒周期）
        await Task.Delay(12000);

        var (s2, b2) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/status");
        var compacted = b2.Replace(" ", "");
        Check("★ 账号掉线后面板不必靠猜：/status 报告 online=false",
            s2 == 200 && compacted.Contains("\"online\":false"), Truncate(b2, 400));
        Check("协议端连接本身仍然是连着的（这正是误导之处）",
            compacted.Contains("\"connected\":true"), Truncate(b2, 400));

        // 此时消息依然进不来，但至少状态是诚实的
        var conversationCount = System.Text.RegularExpressions.Regex.Matches(b2, "\"key\":\"group:").Count;
        Check("离线期间没有新会话（收不到消息）", conversationCount == 0, $"会话数 {conversationCount}");

        await bot.StopAsync();
    }

    // ═══════════════════ S17：面板内扫码登录 ═══════════════════

    /// <summary>
    /// 回归：远程部署卡最久的地方 —— 打开机器人面板看不到登录二维码，
    /// 而 NapCat 的二维码只在它自己的 WebUI（另一个域名 + 另一道 Basic 认证）里。
    /// 现在二维码直接由面板代理出来（/api/qqlogin、/api/qqlogin/qrcode.svg）。
    /// 断言盯的是与 NapCat 的真实契约：哈希算法、Bearer 凭据、缓存与刷新时机、失败时的降级。
    /// </summary>
    private static async Task RunLoginQrScenarioAsync()
    {
        Section("S17 面板内扫码登录（NapCat WebUI 代理 + 二维码渲染）");

        const int openAiPort = 17817;
        const int panelPort = 18091;
        const int napcatPort = 18101;
        const string panelToken = "it-panel-token";
        const string initialQr = "https://txz.qq.com/p?k=INITIAL-KEY&f=1600001615";

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

        using var napcat = new MockNapCatWebUi(napcatPort) { WebUiToken = "it-napcat-token" };
        napcat.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var bot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = NewDataDir("s17"),
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ForwardWebSocket",
            ["QQCHAT_ONEBOT_URL"] = "ws://127.0.0.1:13027", // 没人监听：本场景不关心消息通道
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = panelToken,
            ["QQCHAT_NAPCAT_WEBUI_URL"] = napcat.BaseUrl,
            ["QQCHAT_NAPCAT_WEBUI_TOKEN"] = napcat.WebUiToken
        });

        await WaitForPortAsync(panelPort, cts.Token, bot);

        var panel = $"http://127.0.0.1:{panelPort}";

        // ---- 1) 取二维码 ----
        var (s1, b1) = await HttpGetAsync($"{panel}/api/qqlogin?token={panelToken}");
        Check("面板能拿到登录二维码信息", s1 == 200 && b1.Contains("\"ok\":true"), Truncate(b1, 300));
        Check("返回的就是 NapCat 当前那张二维码", b1.Contains(initialQr), Truncate(b1, 300));
        Check("带二维码指纹（前端靠它做缓存键，避免重载正在扫的图）",
            System.Text.RegularExpressions.Regex.IsMatch(b1, "\"key\":\"[0-9a-f]{12}\""), Truncate(b1, 300));

        var expectedHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(napcat.WebUiToken + ".napcat")))
            .ToLowerInvariant();
        Check("★ 登录握手用的是 NapCat 的算法 SHA256(token + '.napcat')",
            napcat.LoginHashes.Any(h => h == expectedHash),
            napcat.LoginHashes.Count == 0 ? "(压根没发起登录)" : "收到 " + string.Join(",", napcat.LoginHashes.Select(h => Truncate(h, 16))));
        Check("取二维码时带的是登录拿到的 Bearer 凭据", napcat.BadCredentials.Count == 0,
            string.Join(",", napcat.BadCredentials));

        // 缓存：二维码 2 分钟才换一张，面板 10 秒轮询一次，不能每次都去打扰 NapCat
        var callsAfterFirst = napcat.Calls.Count;
        await HttpGetAsync($"{panel}/api/qqlogin?token={panelToken}");
        Check("★ 保质期内不重复请求 NapCat（否则既打爆协议端，又会让正在扫的图闪）",
            napcat.Calls.Count == callsAfterFirst, $"{callsAfterFirst} → {napcat.Calls.Count}");

        // 面板令牌保护同样作用于二维码接口（否则谁知道 URL 谁就能拿到登录二维码）
        var (s401, _) = await HttpGetAsync($"{panel}/api/qqlogin");
        Check("★ 二维码接口同样受面板令牌保护", s401 == 401, $"HTTP {s401}");

        // ---- 2) 二维码图片 ----
        var (svgStatus, svg) = await HttpGetAsync($"{panel}/api/qqlogin/qrcode.svg?token={panelToken}");
        Check("二维码以 SVG 返回（<img> 直接能用，矢量图在手机上更锐）",
            svgStatus == 200 && svg.StartsWith("<?xml") && svg.Contains("<svg"), Truncate(svg, 160));
        // 定位角坐标随模块数变化（30 字符级短链 → 33 模块 → viewBox 41，第二个定位角在 x=30）
        var finderCount = System.Text.RegularExpressions.Regex.Matches(svg, "M(\\d+),(\\d+)h7v7h-7z").Count;
        Check("★ 三个定位角都在（画了个空框或者反色就扫不出来）",
            finderCount == 3 && svg.Contains("M4,4h7v7h-7z"), $"找到 {finderCount} 个 7×7 定位角");
        Check("模块数 33（45 字符短链 + 纠错级 M 决定，viewBox 41；换了替身 URL 需同步）",
            svg.Contains("viewBox=\"0 0 41 41\""), Truncate(svg, 200));
        Check("白底黑块（深色主题下反色就扫不出来）",
            svg.Contains("fill=\"#ffffff\"") && svg.Contains("fill=\"#000000\""), Truncate(svg, 240));

        var fingerprint = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(svg)));
        Check("★ 二维码内容锁定（指纹不变才说明编码的是同一份内容）",
            fingerprint == QrSvgFingerprint, $"实际 {fingerprint}（若确实换了编码参数，同步更新常量即可）");

        // ---- 3) 「换一张」必须真的换 ----
        var (s2, b2) = await HttpGetAsync($"{panel}/api/qqlogin?refresh=1&token={panelToken}");
        Check("★ 点「换一张」会真的让 NapCat 换新二维码",
            s2 == 200 && b2.Contains("ROTATED-1") && napcat.Calls.Contains("/api/QQLogin/RefreshQrcode"),
            Truncate(b2, 240));

        var (_, svg2) = await HttpGetAsync($"{panel}/api/qqlogin/qrcode.svg?token={panelToken}");
        Check("二维码换了图也跟着换（不是缓存的旧图）", svg2 != svg && svg2.Contains("M4,4h7v7h-7z"));

        // ---- 4) 凭据失效要能自愈 ----
        napcat.UnauthorizedOnce = true;
        var (s3, b3) = await HttpGetAsync($"{panel}/api/qqlogin?refresh=1&token={panelToken}");
        Check("★ WebUI 凭据失效后会自动重新登录（不需要重启容器）",
            s3 == 200 && b3.Contains("\"ok\":true") && napcat.LoginHashes.Count >= 2,
            $"登录次数 {napcat.LoginHashes.Count}；{Truncate(b3, 200)}");

        await bot.StopAsync();

        // ---- 5) 没配令牌时：给提示，别 500 ----
        const int panelPort2 = 18092;
        using var bot2 = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = NewDataDir("s17b"),
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_URL"] = "ws://127.0.0.1:13028",
            ["QQCHAT_HEALTH_PORT"] = panelPort2.ToString(),
            ["QQCHAT_NAPCAT_WEBUI_TOKEN"] = string.Empty // 部署时最容易忘的一项
        });

        await WaitForPortAsync(panelPort2, cts.Token, bot2);
        var panel2 = $"http://127.0.0.1:{panelPort2}";

        var (s4, b4) = await HttpGetAsync($"{panel2}/api/qqlogin");
        Check("未配 NapCat 令牌时告知怎么修（而不是 500 或空白）",
            s4 == 200 && b4.Contains("\"configured\":false") && b4.Contains("QQCHAT_NAPCAT_WEBUI_TOKEN"),
            Truncate(b4, 300));

        var (s5, _) = await HttpGetAsync($"{panel2}/api/qqlogin/qrcode.svg");
        Check("拿不到二维码时图片接口给出 503（前端据此显示原因）", s5 == 503, $"HTTP {s5}");

        await bot2.StopAsync();
    }

    // ═══════════════════ 基础设施 ═══════════════════

    private static string? _botDll;

    /// <summary>被测机器人 DLL（懒加载：外部模式下不需要它）。</summary>
    private static string BotDll => _botDll ??= ResolveBotDll();

    private static string ResolveBotDll()
    {
        var fromEnv = Environment.GetEnvironmentVariable("QQCHAT_BOT_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        foreach (var config in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(RepoRoot, "src", "QQChatAgent.Headless", "bin", config, "net8.0", "QQChatAgent.Headless.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("找不到 QQChatAgent.Headless.dll，请先 dotnet build -c Release");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string NewDataDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "qqchat-it", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static BotProcess StartBot(Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "\"" + BotDll + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 不继承宿主机的 QQCHAT_* 配置，避免污染
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (key is not null && key.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase))
            {
                psi.Environment.Remove(key);
            }
        }

        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }

        return new BotProcess(psi);
    }

    private static async Task WaitForPortAsync(int port, CancellationToken ct, BotProcess? bot = null)
    {
        for (var i = 0; i < 200; i++)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync("127.0.0.1", port, ct);
                return;
            }
            catch
            {
                if (bot is { HasExited: true })
                {
                    throw new InvalidOperationException(
                        $"机器人进程已退出（exit={bot.ExitCode}），端口 {port} 未监听。输出:\n" + bot.Diagnostics());
                }

                await Task.Delay(150, ct);
            }
        }

        throw new TimeoutException($"端口 {port} 未在预期时间内监听。输出:\n" + (bot?.Diagnostics() ?? "(无)"));
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(150);
        }

        return false;
    }

    private static async Task<string?> WaitForLogLineAsync(BotProcess bot, string contains, TimeSpan timeout)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            var line = bot.OutputLines.FirstOrDefault(l => l.Contains(contains, StringComparison.Ordinal));
            if (line is not null)
            {
                return line;
            }

            await Task.Delay(150);
        }

        return null;
    }

    private static async Task<(int Status, string Body)> HttpGetAsync(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await http.GetAsync(url);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    // ---------- 断言 ----------

    /// <summary>是否应该跑某个场景（QQCHAT_IT_ONLY=s16 只跑该场景）。</summary>
    private static bool ShouldRun(string tag)
    {
        var only = Environment.GetEnvironmentVariable("QQCHAT_IT_ONLY");
        if (string.IsNullOrWhiteSpace(only))
        {
            return true;
        }

        return only
            .Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>从动作的 params.message 里提取纯文本（OneBot 消息段数组）。</summary>
    private static string MessageText(JsonObject? action)
    {
        var node = action?["params"]?["message"];
        if (node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value)
        {
            return value.GetValue<string>() ?? string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var seg in node.AsArray())
        {
            if (seg?["type"]?.GetValue<string>() == "text")
            {
                sb.Append(seg["data"]?["text"]?.GetValue<string>());
            }
        }

        return sb.ToString();
    }

    /// <summary>取 QQ 引用段的 message_id（无引用返回 null）。</summary>
    private static long? QuotedMessageId(JsonObject? action)
    {
        if (action?["params"]?["message"] is not JsonArray segments)
        {
            return null;
        }

        foreach (var seg in segments)
        {
            if (seg?["type"]?.GetValue<string>() == "reply")
            {
                return seg["data"]?["id"]?.GetValue<long>();
            }
        }

        return null;
    }

    private static string SystemText(JsonObject request)
        => request["messages"]?.AsArray()
               .FirstOrDefault(m => m?["role"]?.GetValue<string>() == "system")?["content"]?.GetValue<string>()
           ?? string.Empty;

    private static List<string> UserTexts(JsonObject request)
        => request["messages"]?.AsArray()
               .Where(m => m?["role"]?.GetValue<string>() == "user")
               .Select(m => m?["content"] is JsonArray parts
                   ? string.Concat(parts.Where(p => p?["type"]?.GetValue<string>() == "text")
                                        .Select(p => p?["text"]?.GetValue<string>()))
                   : m?["content"]?.GetValue<string>() ?? string.Empty)
               .ToList()
           ?? new List<string>();

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("▶ " + title);
    }

    private static void Check(string description, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  ✓ {description}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  ✗ {description}");
            if (detail is not null)
            {
                Console.WriteLine($"      → {Truncate(detail, 300)}");
            }
        }
    }

    private static void Fail(string description, string detail)
    {
        _failed++;
        Console.WriteLine($"  ✗ {description}: {Truncate(detail, 500)}");
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>被测试的机器人子进程。</summary>
    private sealed class BotProcess : IDisposable
    {
        private readonly Process _process;
        private readonly List<string> _lines = new();
        private readonly object _gate = new();

        public BotProcess(ProcessStartInfo psi)
        {
            _process = Process.Start(psi)!;
            _process.OutputDataReceived += (_, e) => Add(e.Data);
            _process.ErrorDataReceived += (_, e) => Add(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public IReadOnlyList<string> OutputLines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToArray();
                }
            }
        }

        private void Add(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                _lines.Add(line);
            }
        }

        /// <summary>优雅停止（模拟 docker stop：发 SIGTERM 后等待退出）。</summary>
        public async Task StopAsync()
        {
            if (_process.HasExited)
            {
                return;
            }

            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // 忽略
            }
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return true;
                }
            }
        }

        public int ExitCode
        {
            get
            {
                try
                {
                    return _process.ExitCode;
                }
                catch
                {
                    return -1;
                }
            }
        }

        /// <summary>最近输出（失败诊断用）。</summary>
        public string Diagnostics() => string.Join(Environment.NewLine, OutputLines.TakeLast(30));

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 忽略
            }

            _process.Dispose();
        }
    }
}
