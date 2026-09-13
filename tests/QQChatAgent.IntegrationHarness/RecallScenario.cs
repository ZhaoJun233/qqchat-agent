using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S25 撤回消息：群友撤回一条消息 → 上下文里那条变成 [已撤回]（模型看不到原文了）、
/// 不再能被引用，并给模型一次“撤回了啥？”的开口机会（带冷却）。
///
/// 为什么这层必须测：撤回是**没有正文的事件** —— 不处理的话会话里会一直留着一条
/// “群里已经看不到的消息”，模型下一轮就会拿着它接话。这种错在群里看起来非常诡异，
/// 但在日志里几乎看不出来（消息本身看起来很正常的）。
/// </summary>
public static partial class Program
{
    private static async Task RunRecallScenarioAsync()
    {
        Section("S25 撤回消息：内容保留但标 [已撤回] / 不再可引用 / 可评论（带冷却）");

        const int openAiPort = 17831;
        const int botWsPort = 13038;
        const int panelPort = 18101;
        const long groupId = 66687;
        const long friendId = 30111;
        const long friendId2 = 30114;

        var dataDir = NewDataDir("s25");
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
            ["QQCHAT_WHITELIST"] = $"{groupId},{friendId},30114",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // ---- 1) 正常来一条消息（机器人回一句），这条之后会被撤回 ----
        const string secret = "这句话马上就会被撤回XYZ";
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "收到。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30201, "小美", secret, 9501, mentionBot: true, ct: cts.Token);
        await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(30));

        // ---- 2) 撤回它 ----
        // 故意让模型把一个**已撤回**的消息填进 replyTo（9501）：机器人必须拒掉这个引用 ——
        // 线上就是这么出错的一条：“回复”挂着一条群里已经看不到的消息。
        openAi.ClearRequests();
        openAi.EnqueueReply($$"""{"suitability": 85, "reply": "哎？撤回了啥", "replyTo": 9501}""");
        var mark = protocol.ActionsReceived.Count;
        await protocol.SendGroupRecallAsync(groupId, 30201, 9501, ct: cts.Token);

        var afterRecall = await WaitForRequestAsync(openAi, r => r.Contains("有人撤回了消息"), TimeSpan.FromSeconds(40));
        Check("★ 撤回来了一条消息后，机器人会为它单独请求一次模型（“撤回了啥”这种反应）",
            afterRecall is not null, afterRecall is null ? "(没等到撤回后的模型请求)" : "已请求");

        Check("★ 上下文里那条标上了 [已撤回]，但**原文保留**（模型在场，只是要知道这条已被收回）",
            afterRecall is not null && afterRecall.Contains("[已撤回]") && afterRecall.Contains("XYZ"),
            Snippet(afterRecall ?? "", "[已撤回]"));
        Check("★ 提示词里明确“可以记得，但不要引用/复述/当众开玩笑”",
            afterRecall is not null && afterRecall.Contains("不要引用"),
            afterRecall is null ? "(无请求)" : "已加约束");
        Check("★ 撤回的那条不再带 (#id)（引用一条看不见的消息会让人莫名其妙）",
            afterRecall is not null && !afterRecall.Contains("#9501"), "已排除");

        await WaitUntilAsync(() => GroupSendsSince(protocol, mark).Any(a => MessageText(a).Contains("撤回了啥")),
            TimeSpan.FromSeconds(15));
        var recallSends = GroupSendsSince(protocol, mark);
        Check("★ 机器人评论了这次撤回（本轮真的发出去了）",
            recallSends.Any(a => MessageText(a).Contains("撤回了啥")),
            $"本轮发出 {recallSends.Count} 条：{string.Join(" | ", recallSends.Select(MessageText))}");
        Check("★ 评论不带引用（即使模型硬要把已撤回的那条填进 replyTo，也要被拒掉）",
            recallSends.All(a => QuotedMessageId(a) is null));

        // 面板要能看出这条被撤回了（运维视角看得到原文，但要标明模型看不到）
        var (convStatus, convBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/conversations/group%3A{groupId}");
        Check("★ 面板接口把这条标成 recalled（前端会划掉并注明）",
            convStatus == 200 && convBody.Contains("\"recalled\":true"), convBody.Length > 200 ? convBody[^200..] : convBody);

        // ---- 3) 冷却：连着撤回第二条，不再评论（但照样标记）----
        openAi.ClearRequests();
        var mark2 = protocol.ActionsReceived.Count;
        await Task.Delay(500);
        await protocol.SendGroupRecallAsync(groupId, 30201, 9501, ct: cts.Token); // 重复事件：已标记过 → 静默
        await protocol.SendGroupRecallAsync(groupId, 30202, 9502, ct: cts.Token); // 不存在的消息
        await Task.Delay(4000);
        Check("★ 重复/找不到的撤回事件不会让机器人乱说话",
            GroupSendsSince(protocol, mark2).Count == 0,
            $"发出 {GroupSendsSince(protocol, mark2).Count} 条");
        Check("★ 冷却内不再为第二条撤回再请求模型（同会话 90s 一次）",
            openAi.Requests.Count == 0, $"期间模型请求 {openAi.Requests.Count} 次");

        // ---- 4) 私聊撤回（friend_recall）：同样标记，不进群 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 70, "reply": "没事。"}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "私聊这句", 9503, ct: cts.Token);
        await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(30), "send_private_msg");

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 80, "reply": "撤了就撤了。"}""");
        var privateMark = protocol.ActionsReceived.Count;
        await protocol.SendFriendRecallAsync(friendId, 9503, ct: cts.Token);
        var privateRecall = await WaitForRequestAsync(openAi, r => r.Contains("有人撤回了消息"), TimeSpan.FromSeconds(40));
        Check("★ 私聊撤回也认（friend_recall 事件）", privateRecall is not null);
        Check("★ 私聊撤回同样标上 [已撤回]（原文保留）",
            privateRecall is not null && privateRecall.Contains("[已撤回]") && privateRecall.Contains("私聊这句"));

        await WaitUntilAsync(() => PrivateSendsSince(protocol, privateMark).Any(a => MessageText(a).Length > 0),
            TimeSpan.FromSeconds(15));
        Check("★ 私聊里也会评论一句（且只发私聊，不发群）",
            PrivateSendsSince(protocol, privateMark).Any(a => MessageText(a).Contains("撤了就撤了")));

        // ---- 5) 手误更正：撤回之后同一个人又发了新消息 → 不点评（只标记）----
        //  真实案例：群友把“固定bpc”改成“固定npc”撤回了上一条，机器人却回“鬼鬼祟祟撤回什么呢，我都看见了！”
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 80, "reply": "懂了。"}""");
        await protocol.SendPrivateMessageAsync(friendId2, "群友B", "他是这里的固定bpc", 9510, ct: cts.Token);
        await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(30), "send_private_msg");

        openAi.EnqueueReply("""{"suitability": 80, "reply": "嗯，npc。"}""");
        await protocol.SendPrivateMessageAsync(friendId2, "群友B", "他是这里的固定npc", 9511, ct: cts.Token);
        await WaitUntilAsync(() => PrivateSendsSince(protocol, 0).Any(a => MessageText(a).Contains("npc")),
            TimeSpan.FromSeconds(20));

        openAi.ClearRequests();
        var correctedMark = protocol.ActionsReceived.Count;
        await protocol.SendFriendRecallAsync(friendId2, 9510, ct: cts.Token);
        await Task.Delay(5000);

        Check("★ 看出“手误更正”（撤回后同一个人又发了新消息）→ 不给模型开口机会",
            bot.OutputLines.Any(l => l.Contains("看起来是手误更正")),
            string.Join(" | ", bot.OutputLines.Where(l => l.Contains("[Recall]")).TakeLast(3)));
        Check("★ 但标记照做（那条仍然变成 [已撤回]）",
            !PrivateSendsSince(protocol, correctedMark).Any(),
            $"撤回后又发了 {PrivateSendsSince(protocol, correctedMark).Count} 条");

        var (corrStatus, corrBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/conversations/private%3A{friendId2}");
        Check("★ 手误更正的那条在面板里也是 recalled（标记与点评分开决策）",
            corrStatus == 200 && corrBody.Contains("\"recalled\":true"),
            corrBody.Length > 200 ? corrBody[^200..] : corrBody);
    }
}
