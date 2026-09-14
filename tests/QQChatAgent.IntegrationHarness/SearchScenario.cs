using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S26 联网搜索：模型填 <c>search</c> → 机器人真去查（优先走模型自带搜索 = Gemini 的 google_search 工具，
/// 不可用时回退到可插拔搜索源）→ 把结果作为**事实**交给下一轮 → 模型再开口；
/// 另外覆盖 <c>read</c>（读某个网页正文）、冷却、开关、SSRF 闸门、面板自测入口。
///
/// 为什么这层的断言要分两条路：搜索能不能用跟部署环境强相关
/// （机房 IP 上爬网页搜索会被拦、网关可能不转发 google_search 工具），
/// 所以“首选路能用”和“首选路挂了还能退”都得钉住。
/// </summary>
public static partial class Program
{
    private static async Task RunSearchScenarioAsync()
    {
        Section("S26 联网搜索：模型自带搜索（grounding）/ 搜索源兜底 / 读页面 / 冷却 / 面板自测");

        const int openAiPort = 17833;
        const int botWsPort = 13040;
        const int panelPort = 18103;
        const int searchPort = 18104;
        const long groupId = 66689;
        const long friendId = 30113;

        var dataDir = NewDataDir("s26");
        using var search = new MockSearchHost(searchPort);
        search.Start();

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
            ["QQCHAT_WEB_SEARCH"] = "1",
            // 假搜索后端与假网页都在 127.0.0.1 上：默认的 SSRF 防护会拦（生产行为不变）。
            // SSRF 闸门本身另开一个 allowPrivate=0 的进程验证（见本场景末尾）。
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "1",
            // 兜底源：SearxNG 形状（模型搜索不可用时才走它）
            ["QQCHAT_SEARCH_SOURCES"] = search.SearxSource
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        var (settingsStatus, settingsBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 联网搜索配置生效（开关 + 搜索源 + 结果数）",
            settingsStatus == 200 && settingsBody.Contains("\"enableWebSearch\":true") &&
            settingsBody.Contains("searx-mock"),
            settingsBody.Length > 240 ? settingsBody[^240..] : settingsBody);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        // ---- 1) 模型要查资料 → 走模型自带搜索（Gemini grounding）----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "我去查一下。", "search": "砂狼白子的声优是谁"}""");
        openAi.EnqueueReply("""{"suitability": 90, "reply": "查到了，日语配音是小仓唯。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30401, "小美", "@10001 白子的声优是谁来着", 9601, mentionBot: true, ct: cts.Token);

        var afterSearch = await WaitForRequestAsync(openAi,
            r => r.Contains("刚查到的资料") && r.Contains("小仓唯"), TimeSpan.FromSeconds(60));
        Check("★ 模型填 search → 机器人真去查（打了原生 generateContent 端点）",
            openAi.GroundingRequests >= 1, $"grounding 请求 {openAi.GroundingRequests} 次");
        Check("★ 搜索请求里带了 google_search 工具与检索词",
            openAi.LastGroundingBody.Contains("google_search") && openAi.LastGroundingBody.Contains("砂狼白子的声优是谁"),
            openAi.LastGroundingBody.Length > 200 ? openAi.LastGroundingBody[..200] : openAi.LastGroundingBody);
        Check("★ 查到的资料作为“事实”进了下一轮提示词（含答案与来源标题）",
            afterSearch is not null && afterSearch.Contains("刚查到的资料") && afterSearch.Contains("小仓唯"),
            afterSearch is null ? "(没等到带资料的请求)" : "已注入");
        Check("★ 来源标题也带上了（groundingChunks）",
            afterSearch is not null && afterSearch.Contains("萌娘百科", StringComparison.OrdinalIgnoreCase));
        Check("★ 走的是模型自带搜索，没有去爬配置里的搜索源",
            search.SearxHits == 0, $"searx 命中 {search.SearxHits} 次");

        var sends = await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(40));
        await WaitUntilAsync(() => sends.Any(a => MessageText(a).Contains("小仓唯")) ||
                                   GroupSendsSince(protocol, 0).Any(a => MessageText(a).Contains("小仓唯")),
            TimeSpan.FromSeconds(20));
        Check("★ 拿到资料后模型真的开口回答了（不是查完就哑了）",
            GroupSendsSince(protocol, 0).Any(a => MessageText(a).Contains("小仓唯")));

        // ---- 2) 模型搜索不可用 → 回退到搜索源模板（另一个会话，避开冷却）----
        openAi.GroundingFails = true;
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "我去查查。", "search": "阿拜多斯 对策委员会"}""");
        openAi.EnqueueReply("""{"suitability": 90, "reply": "查到一些资料。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "帮我查下阿拜多斯", 9602, ct: cts.Token);

        var fallback = await WaitForRequestAsync(openAi, r => r.Contains("searx-mock"), TimeSpan.FromSeconds(60));
        Check("★ 模型搜索不可用时回退到搜索源（SearxNG 形状）",
            fallback is not null && search.SearxHits >= 1,
            $"searx 命中 {search.SearxHits} 次");
        Check("★ 回退结果也进了提示词（标题 + 摘要）",
            fallback is not null && fallback.Contains("假 Searx 结果一") && fallback.Contains("小仓唯"),
            (fallback is null ? "(没等到回退后的请求)；" : string.Empty) +
            "机器人日志：" + string.Join(" | ", bot.OutputLines.Where(l => l.Contains("[Search]")).TakeLast(4)));
        openAi.GroundingFails = false;

        // ---- 3) 冷却：同一会话 30 秒内不再搜（但话照说）----
        openAi.ClearRequests();
        var groundingBefore = openAi.GroundingRequests;
        var privateMark = protocol.ActionsReceived.Count;
        openAi.EnqueueReply("""{"suitability": 88, "reply": "我再查一次。", "search": "阿拜多斯 白子"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "再查一次", 9603, ct: cts.Token);
        await Task.Delay(5000);
        Check("★ 冷却期内不再重复搜索（省模型调用与时间）",
            openAi.GroundingRequests == groundingBefore, $"grounding 请求 {groundingBefore} → {openAi.GroundingRequests}");
        Check("★ 被冷却拦下的这一轮照样把话说了（模型的话不能丢）",
            PrivateSendsSince(protocol, privateMark).Any(a => MessageText(a).Contains("我再查一次")),
            string.Join(" | ", PrivateSendsSince(protocol, privateMark).Select(MessageText)));

        // ---- 3b) 冷却时间可调（面板里改，不再是写死的 30 秒）----
        openAi.GroundingAnswer = "独特答案：白子的自行车是『测试专用事实XYZ』。";
        using (var cdResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
                   new StringContent("""{"webSearchCooldownSeconds":0}""", Encoding.UTF8, "application/json"), cts.Token))
        {
            Check("★ 面板能改搜索冷却（默认 30 秒 → 设成 0）", cdResp.IsSuccessStatusCode,
                $"HTTP {(int)cdResp.StatusCode}");
        }

        using (var cdGet = await http.GetAsync($"http://127.0.0.1:{panelPort}/api/settings", cts.Token))
        {
            var cdBody = await cdGet.Content.ReadAsStringAsync(cts.Token);
            Check("★ 冷却值真的存下来了（面板读到 0）", cdBody.Contains("\"webSearchCooldownSeconds\":0"),
                cdBody.Contains("webSearchCooldownSeconds") ? cdBody[(cdBody.IndexOf("webSearchCooldownSeconds", StringComparison.Ordinal) - 20)..][..80] : "(没这个字段)");
        }

        await Task.Delay(300);
        openAi.ClearRequests();
        var groundingBefore3 = openAi.GroundingRequests;
        openAi.EnqueueReply("""{"suitability": 88, "reply": "行，我再查一遍。", "search": "白子的自行车是什么"}""");
        openAi.EnqueueReply("""{"suitability": 88, "reply": "查到了，就是那辆测试专用自行车。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "再查一遍（冷却已设为 0）", 9606, ct: cts.Token);
        var afterCd = await WaitForRequestAsync(openAi,
            r => r.Contains("刚查到的资料") && r.Contains("测试专用事实XYZ"), TimeSpan.FromSeconds(60));
        Check("★ 冷却设成 0 后同一会话立刻能再搜（读的是设置，不是常量）",
            openAi.GroundingRequests > groundingBefore3 && afterCd is not null,
            $"grounding {groundingBefore3} → {openAi.GroundingRequests}");

        // ---- 3c) 带着“刚查到的资料”那一轮不受发言适合度门槛限制 ----
        // 线上症状（号主反馈“有时查完不输出”）：模型回来汇报那一轮自评偏低（它只是回来报答案，
        // 不是在“插嘴”），被门槛静默掉 → 资料白白浪费，群里只看到“我去查一下”。
        using (var thResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
                   new StringContent("""{"suitabilityThreshold":50}""", Encoding.UTF8, "application/json"), cts.Token))
        {
            Check("★ 先把发言门槛调到 50（默认 10）", thResp.IsSuccessStatusCode);
        }

        await Task.Delay(300);
        openAi.GroundingAnswer = "独特答案：第二个测试事实『门槛测试事实QWE』。";
        openAi.ClearRequests();
        var mark4 = protocol.ActionsReceived.Count;
        openAi.EnqueueReply("""{"suitability": 90, "reply": "我查一下这个。", "search": "查一下门槛测试"}""");
        openAi.EnqueueReply("""{"suitability": 5, "reply": "查到了：门槛测试事实QWE。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "帮我查个东西", 9607, ct: cts.Token);
        await WaitUntilAsync(
            () => PrivateSendsSince(protocol, mark4).Any(a => MessageText(a).Contains("门槛测试事实QWE")),
            TimeSpan.FromSeconds(60));
        Check("★ 带着资料的那一轮即便自评只有 5 分，也照样把答案说出来（以前会被静默掉）",
            PrivateSendsSince(protocol, mark4).Any(a => MessageText(a).Contains("门槛测试事实QWE")),
            string.Join(" | ", PrivateSendsSince(protocol, mark4).Select(MessageText)));

        // 反向：门槛并没被整体废掉 —— 没带资料的普通轮，低分依旧沉默
        var mark5 = protocol.ActionsReceived.Count;
        openAi.EnqueueReply("""{"suitability": 5, "reply": "这句我不该说。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "随便说点什么", 9608, ct: cts.Token);
        await Task.Delay(6000);
        Check("★ 普通轮（没带资料）低于门槛时依旧保持沉默",
            !PrivateSendsSince(protocol, mark5).Any(a => MessageText(a).Contains("我不该说")),
            string.Join(" | ", PrivateSendsSince(protocol, mark5).Select(MessageText)));

        // ---- 3d) 资料这一轮没说出来 → 留给下一轮（一次搜索不白费）----
        openAi.GroundingAnswer = "独特答案：第三个测试事实『留着下轮事实RTY』。";
        openAi.ClearRequests();
        var mark6 = protocol.ActionsReceived.Count;
        openAi.EnqueueReply("""{"suitability": 90, "reply": "我查查。", "search": "第三个测试"}""");
        openAi.EnqueueReply("""{"suitability": 90}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "那个第三个测试是什么", 9609, ct: cts.Token);
        await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("查到的资料这次没说出去")),
            TimeSpan.FromSeconds(60));
        Check("★ 资料轮模型沉默时不丢资料（日志明说留着下一轮）",
            bot.OutputLines.Any(l => l.Contains("查到的资料这次没说出去")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("[Search]")).TakeLast(3)));
        Check("★ 资料轮真的没发第二条（沉默就是沉默，不把空回复当话发）",
            PrivateSendsSince(protocol, mark6).Count(a => MessageText(a).Contains("留着下轮事实RTY")) == 0);

        // 下一轮用户再说话：那份资料应该还在提示词里
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "哦对，我查过了：留着下轮事实RTY。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "查到了吗", 9610, ct: cts.Token);
        var carried = await WaitForRequestAsync(openAi,
            r => r.Contains("刚查到的资料") && r.Contains("留着下轮事实RTY"), TimeSpan.FromSeconds(60));
        Check("★ 下一轮提示词里还带着那份资料（搜一次不会白白浪费）", carried is not null,
            carried is null ? "(下一轮没有资料)" : "已带回");
        Check("★ 资料要求“用自己的语气接着说”（结合人设与语境，不许播报腔）",
            carried is not null && carried.Contains("就像你本来就知道这件事") &&
            carried.Contains("你的人物设定") && carried.Contains("接着刚才的话头说"),
            carried is null ? "(没有资料)" : "已要求自然输出");
        openAi.GroundingAnswer = "《碧蓝档案》里的砂狼白子，日语配音是小仓唯。";

        // ---- 4) read：读一个网页的正文（去脚本/标签，只要文字）----
        openAi.ClearRequests();
        openAi.EnqueueReply($$"""{"suitability": 88, "reply": "我看下。", "read": "{{search.PageUrl}}"}""");
        openAi.EnqueueReply("""{"suitability": 90, "reply": "页面里写了她是对策委员会的。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30402, "小明", "@10001 看下这个页面", 9604, mentionBot: true, ct: cts.Token);

        var afterRead = await WaitForRequestAsync(openAi, r => r.Contains("的正文") && r.Contains("阿拜多斯"), TimeSpan.FromSeconds(60));
        Check("★ 模型填 read → 机器人真去抓那个页面", search.PageHits >= 1, $"page 命中 {search.PageHits} 次");
        Check("★ 页面正文抽成了纯文本进提示词（标签与脚本已去掉）",
            afterRead is not null && afterRead.Contains("砂狼白子") && !afterRead.Contains("<script>"),
            afterRead is null ? "(没等到带正文的请求)" : "已注入");

        // ---- 5) 面板自测入口 + SSRF 闸门 ----
        using var testResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/search/test",
            new StringContent(new JsonObject { ["query"] = "测试面板搜索" }.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
        var testBody = await testResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ 面板「搜一下」能跑通并回真实结果",
            testResp.IsSuccessStatusCode && testBody.Contains("\"ok\":true") && testBody.Contains("小仓唯"),
            testBody.Length > 240 ? testBody[^240..] : testBody);

        using var readResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/search/test",
            new StringContent(new JsonObject { ["url"] = search.PageUrl }.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
        var readBody = await readResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ 面板「读页面」也能读（返回抽好的正文）",
            readResp.IsSuccessStatusCode && readBody.Contains("阿拜多斯"));

        using var refusedResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/search/test",
            new StringContent(new JsonObject { ["url"] = "http://127.0.0.1:9/none" }.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
        var refusedBody = await refusedResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ 读不到的页面如实报错（不返回空正文）",
            refusedBody.Contains("\"ok\":false") && refusedBody.Contains("error"),
            refusedBody.Length > 200 ? refusedBody[^200..] : refusedBody);

        // ---- 6) 关掉开关 → 模型再要搜索也不动 ----
        using var saveResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent("""{"enableWebSearch":false}""", Encoding.UTF8, "application/json"), cts.Token);
        Check("★ 面板可以关掉联网搜索", saveResp.IsSuccessStatusCode);

        await Task.Delay(300);
        openAi.ClearRequests();
        var groundingBefore2 = openAi.GroundingRequests;
        var searxBefore = search.SearxHits;
        var mark2 = protocol.ActionsReceived.Count;
        openAi.EnqueueReply("""{"suitability": 88, "reply": "那我就凭记忆说了。", "search": "这个不该被搜"}""");
        await protocol.SendGroupMessageAsync(groupId, 30403, "小美", "@10001 查一下", 9605, mentionBot: true, ct: cts.Token);
        await Task.Delay(6000);
        Check("★ 关掉后不再联网（grounding 与搜索源都没被碰）",
            openAi.GroundingRequests == groundingBefore2 && search.SearxHits == searxBefore,
            $"grounding {groundingBefore2} → {openAi.GroundingRequests}，searx {searxBefore} → {search.SearxHits}");
        Check("★ 关掉后模型的话照发（只是不联网）",
            GroupSendsSince(protocol, mark2).Any(a => MessageText(a).Contains("凭记忆")),
            string.Join(" | ", GroupSendsSince(protocol, mark2).Select(MessageText)));

        // ---- 7) SSRF 闸门（另起一个 allowPrivate=0 的进程：默认部署就是这状态）----
        const int ssrfPort = 18105;
        var ssrfDir = NewDataDir("s26-ssrf");
        using var ssrfBot = StartBot(new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = ssrfDir,
            ["QQCHAT_API_KEY"] = "sk-mock",
            ["QQCHAT_BASE_URL"] = openAi.BaseUrl,
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = "http://0.0.0.0:13041",
            ["QQCHAT_UIN"] = "10002",
            ["QQCHAT_WHITELIST"] = "*",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = ssrfPort.ToString(),
            ["QQCHAT_WEB_SEARCH"] = "1",
            ["QQCHAT_SEARCH_SOURCES"] = search.SearxSource,
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "0"
        });

        await WaitForPortAsync(ssrfPort, cts.Token, ssrfBot);
        using (var ssrfBody = new StringContent(
                   new JsonObject { ["url"] = "http://napcat:3001/" }.ToJsonString(), Encoding.UTF8, "application/json"))
        {
            using var ssrfResp = await http.PostAsync($"http://127.0.0.1:{ssrfPort}/api/search/test", ssrfBody, cts.Token);
            var text = await ssrfResp.Content.ReadAsStringAsync(cts.Token);
            Check("★ SSRF 闸门：默认部署读不了内网地址（单标签主机名直接拒）",
                text.Contains("单标签主机名"), text.Length > 200 ? text[^200..] : text);
        }

        using (var localBody = new StringContent(
                   new JsonObject { ["url"] = search.PageUrl }.ToJsonString(), Encoding.UTF8, "application/json"))
        {
            using var localResp = await http.PostAsync($"http://127.0.0.1:{ssrfPort}/api/search/test", localBody, cts.Token);
            var text = await localResp.Content.ReadAsStringAsync(cts.Token);
            Check("★ SSRF 闸门：回环地址也读不了（防止让服务器替群友访问本机）",
                text.Contains("回环") || text.Contains("私有"), text.Length > 200 ? text[^200..] : text);
        }
    }
}
