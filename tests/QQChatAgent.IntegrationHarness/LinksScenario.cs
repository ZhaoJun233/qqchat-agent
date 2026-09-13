using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S23 链接与转发：群里发的东西不再“看不见”。
///
/// 三类以前直接被丢掉的内容：
///   ① 合并转发的聊天记录（forward 段，只有 id，内容要另发 get_forward_msg 取）；
///   ② QQ 分享卡片（json 段，非音乐：新闻/小程序/链接卡片）；
///   ③ 文件（file 段）。
/// 外加链接预览：机器人真去打开群里发的链接，取回标题与摘要再聊。
///
/// 断言重点：**内容真的进到提示词里了**，以及上限（转发只展开前 N 条、多余的要交代清楚）。
/// </summary>
public static partial class Program
{
    private static async Task RunLinksScenarioAsync()
    {
        Section("S23 链接与转发：合并转发的聊天记录 / 分享卡片 / 文件 / 链接预览");

        const int openAiPort = 17828;
        const int botWsPort = 13035;
        const int panelPort = 18099;
        const int pagePort = 18100;
        const long groupId = 66684;

        var dataDir = NewDataDir("s23");
        using var pages = new MockPageHost(pagePort);
        pages.Start();

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

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
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_LINK_PREVIEW_TIMEOUT"] = "5",
            ["QQCHAT_LINK_PREVIEW_MAX"] = "2",
            // 假网页在 127.0.0.1：默认的 SSRF 防护会拦（生产行为不变）
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "1"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- 1) 合并转发：内容要主动去 get_forward_msg 拉回来 ----
        protocol.ForwardRecords["F1"] = new JsonArray(
            MockProtocol.ForwardNode(40001, "小红", "你昨天说的那个事儿定下来了吗"),
            MockProtocol.ForwardNode(40002, "小刚", "还没，等下周"),
            MockProtocol.ForwardNode(40001, "小红", "[图片]"));

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "看起来是还没定，那就再等等。"}""");
        await protocol.SendGroupForwardAsync(groupId, 30021, "小美", "F1", 9401, "你们看这个", cts.Token);

        var forwardReq = await WaitForRequestAsync(openAi, r => r.Contains("合并转发的聊天记录"), TimeSpan.FromSeconds(40));
        Check("★ 合并转发的内容真的去拉了（发了 get_forward_msg）", protocol.ForwardFetchCount >= 1,
            $"fetchCount={protocol.ForwardFetchCount}");
        Check("★ 转发记录展开了条数与说话人", forwardReq is not null && forwardReq.Contains("3 条") &&
            forwardReq.Contains("小红") && forwardReq.Contains("小刚"));
        Check("★ 转发里的发言内容进了提示词（模型能看到转过来的对话）",
            forwardReq is not null && forwardReq.Contains("你昨天说的那个事儿定下来了吗") && forwardReq.Contains("等下周"));
        Check("★ 转发里的图片段有占位（不会静默丢内容）",
            forwardReq is not null && forwardReq.Contains("[图片]"));

        // ---- 2) 转发过长时只展开前 20 行，并交代还有多少条 ----
        var many = new JsonArray();
        for (var i = 1; i <= 25; i++)
        {
            many.Add(MockProtocol.ForwardNode(40010, "话痨", $"第{i}条消息"));
        }

        protocol.ForwardRecords["F2"] = many;
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 70, "reply": "记录有点长，我看了前面。"}""");
        await protocol.SendGroupForwardAsync(groupId, 30022, "小刚", "F2", 9402, "看看这个长记录", cts.Token);

        var longReq = await WaitForRequestAsync(openAi, r => r.Contains("25 条"), TimeSpan.FromSeconds(40));
        Check("★ 超长转发只展开前 20 条，并说明“还有 N 条未展开”",
            longReq is not null && longReq.Contains("还有 5 条未展开") && longReq.Contains("第20条消息") && !longReq.Contains("第21条消息"),
            longReq is null ? "(没等到)" : "已按上限截断");

        // ---- 3) QQ 分享卡片（非音乐）：标题与链接要进上下文 ----
        var card = """
            {"app":"com.tencent.structmsg","prompt":"[分享]某新闻标题xyz",
             "meta":{"news":{"title":"某新闻标题xyz","desc":"某新闻摘要","jumpUrl":"https://news.example.com/a/1"}}}
            """;
        var cardSegments = new JsonArray(
            new JsonObject { ["type"] = "json", ["data"] = new JsonObject { ["data"] = card } });

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 75, "reply": "这新闻我也看到了。"}""");
        await protocol.SendGroupSegmentsAsync(groupId, 30023, "小明", 9403, cardSegments, "[CQ:json,data=...]", cts.Token);

        var cardReq = await WaitForRequestAsync(openAi, r => r.Contains("某新闻标题xyz"), TimeSpan.FromSeconds(40));
        Check("★ 分享卡片被识别成“[分享卡片:标题] + 链接”，链接也给了模型",
            cardReq is not null && cardReq.Contains("分享卡片") && cardReq.Contains("news.example.com/a/1"));

        // ---- 4) 文件段：至少要知道有人发了个什么文件 ----
        var fileSegments = new JsonArray(
            new JsonObject { ["type"] = "file", ["data"] = new JsonObject { ["name"] = "周报-测试.pdf", ["file"] = "a.pdf" } });

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 65, "reply": "文件收到。"}""");
        await protocol.SendGroupSegmentsAsync(groupId, 30024, "小刚", 9404, fileSegments, "[CQ:file,name=周报-测试.pdf]", cts.Token);

        var fileReq = await WaitForRequestAsync(openAi, r => r.Contains("周报-测试.pdf"), TimeSpan.FromSeconds(40));
        Check("★ 文件段带上文件名（以前是完全丢掉）", fileReq is not null);

        // ---- 5) 链接预览：机器人真去打开链接，把标题/摘要拿来用 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 85, "reply": "这标题我懂了。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30025, "小美",
            $"你们看这个 {pages.PageUrl} 挺有意思", 9405, ct: cts.Token);

        var linkReq = await WaitForRequestAsync(openAi, r => r.Contains(pages.Title), TimeSpan.FromSeconds(40));
        Check("★ 机器人真的打开了群里的链接（假网页被访问）", pages.Hits >= 1, $"hits={pages.Hits}");
        Check("★ 链接标题与摘要进了提示词（不用对着 URL 猜）",
            linkReq is not null && linkReq.Contains(pages.Title) && linkReq.Contains(pages.Description));
        Check("★ 提示词里带上了原链接，模型知道说的是哪个网址",
            linkReq is not null && linkReq.Contains("127.0.0.1") && linkReq.Contains("刚发的链接"));

        // ---- 6) 关掉链接预览就不再抓（但消息照常处理）----
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var saveRes = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent("""{"enableLinkPreview":false}""", System.Text.Encoding.UTF8, "application/json"), cts.Token);
        Check("★ 面板可以关掉链接预览", saveRes.IsSuccessStatusCode, $"HTTP {(int)saveRes.StatusCode}");

        var hitsBefore = pages.Hits;
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 60, "reply": "好。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30026, "小刚", $"再看看 {pages.PageUrl}/x2", 9406, ct: cts.Token);
        var afterOff = await WaitForRequestAsync(openAi, _ => true, TimeSpan.FromSeconds(30));
        Check("★ 关掉后不再抓链接（假网页没被再访问）",
            afterOff is not null && pages.Hits == hitsBefore, $"hits {hitsBefore} → {pages.Hits}");
    }
}
