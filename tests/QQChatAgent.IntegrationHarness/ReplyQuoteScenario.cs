using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S19 回归：QQ“回复引用”挂错消息。
///
/// 线上现象（用户截图）：群里 11:24 有人说“域名要配dns解析的”，随后话题转到了螺蛳粉上；
/// 机器人最后那句正文明明在回“明明是超香的好不好”，QQ 的回复引用却挂在那条 11:24 的旧消息上，
/// 看起来就是“回复错人/回错消息”。
///
/// 机制：每条消息都会 EnqueueReply(msgId)，进的是该会话的 FIFO 队列；模型慢（常见 15~20 秒）
/// 时队列会积压，等轮到某个旧触发生成时，模型看到的上下文早就是最新那几句了 ——
/// 而旧实现只认排队时那个 id，于是正文在聊新话题、引用挂在旧消息上。
///
/// 本场景构造：A 触发第一次请求（模型 2.5 秒才回），B/C/D 在请求在途期间陆续进来并排队。
/// 第二次生成（触发消息是 B）时，上下文里最新一条别人发的消息是 D：
///   ✓ 引用应该是 D（模型真正在回的那条）或干脆不带引用
///   ✗ 绝不能是 B（排队时那个旧触发）
/// </summary>
public static partial class Program
{
    internal static async Task RunReplyQuoteScenarioAsync()
    {
        Section("S19 回复引用不能挂到排队积压的旧消息上");

        const int openAiPort = 17822;   // 注意：18106–19065 被 Windows/Hyper-V 保留，别用
        const int botWsPort = 13030;
        const long groupId = 66681;

        // 四个消息 id 分开写清楚：A 触发第一次请求，B 是第二次请求的“旧触发”，D 是上下文里最新那条
        const long msgA = 7301;
        const long msgB = 7302;
        const long msgC = 7303;
        const long msgD = 7304;

        var dataDir = NewDataDir("s19");

        // 慢模型是复现的关键：请求在途期间，后面的消息才有机会排队积压
        using var openAi = new MockOpenAi(openAiPort) { ResponseDelayMs = 2500 };
        openAi.Start();
        openAi.EnqueueReply("""{"suitability": 99, "reply": "回A的那句"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "回最新那句"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "再回一句"}""");
        openAi.EnqueueReply("""{"suitability": 99, "reply": "第四句"}""");

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
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",   // 别分句，一条回复就是一次 send_group_msg
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_PROFILE_SUMMARY"] = "0", // 别让画像巡检占掉脚本回复
            ["QQCHAT_STICKERS"] = "0",        // 本场景只关心引用，关掉表情包避免干扰
            ["QQCHAT_HEALTH_PORT"] = "0"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // A：第一次请求就地开始（此时上下文里只有 A）
        await protocol.SendGroupMessageAsync(groupId, 30001, "老王", "这个报错怎么修", msgA, ct: cts.Token);
        await Task.Delay(300);

        // B/C/D：模型还在生成第一条回复，它们只能排队 —— 于是 B 成了“积压的旧触发”
        await protocol.SendGroupMessageAsync(groupId, 30002, "群友A", "域名要配dns解析的", msgB, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30003, "小张", "我只是想吃个螺蛳粉", msgC, ct: cts.Token);
        await Task.Delay(300);
        await protocol.SendGroupMessageAsync(groupId, 30004, "小李", "明明超香的好不好", msgD, ct: cts.Token);

        // 等 4 次生成全部跑完（每次约 2.5s）
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") >= 4,
            TimeSpan.FromSeconds(60));
        await Task.Delay(1500);

        var sends = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => !string.IsNullOrWhiteSpace(MessageText(a)))
            .ToList();

        Check("4 条消息各自触发了一次回复", sends.Count >= 4, $"实际 {sends.Count} 次：{string.Join(" | ", sends.Select(MessageText))}");

        if (sends.Count == 0)
        {
            await bot.StopAsync();
            return;
        }

        var quotes = sends.Select(QuotedMessageId).ToList();

        // 第一次回复就是回 A，而且 A 后面确实有人说话 → 引用应该还在（证明引用没被整体关掉）
        Check("触发消息仍是最新诉求时照旧带引用（引用 A）",
            quotes[0] == msgA, $"引用 id = {quotes[0]}");

        // 核心断言：第二次生成触发的是 B，但模型看到的上下文里最新一条别人发的消息是 D。
        // 旧实现会把引用挂到 B（排队时那个旧触发）→ 线上表现就是“正文聊螺蛳粉、引用挂 11:24 那条”；
        // 后来改成“挂上下文里最新那条（D）”—— 仍然是把正文挂到了**另一个人**头上（D 是小李，不是提问的老王），
        // 群友看到的还是“回复错人”。现在的口径：**触发已经过去就不引用**。
        Check("★ 积压的旧触发不会被当成引用目标",
            quotes.All(q => q != msgB), $"引用 id = {string.Join(", ", quotes.Select(q => q?.ToString() ?? "无"))}");

        Check("★ 触发消息已过去时干脆不引用（宁可不引，不把正文挂到别人头上）",
            quotes.Count > 1 && quotes[1] is null,
            $"第二条引用 id = {(quotes.Count > 1 ? quotes[1]?.ToString() ?? "无" : "(缺)")}，期望“无”");

        // 顺带兜一层：不能把正文挂到别人头上（既不能是中间那条 C，也不能是最新的 D）
        Check("★ 正文不会被挂到别人头上（既不是 C 也不是 D）",
            quotes.All(q => q != msgC && q != msgD),
            $"引用 id = {string.Join(", ", quotes.Select(q => q?.ToString() ?? "无"))}");

        // ---- 根本解法：让模型自己指认“我在回哪条” ----
        // 启发式只能猜（最新那条 / 排队触发），而模型最清楚自己在接哪个哏：
        // 提示词里最近几条别人的消息都带了 (#id)，它可以回一个 replyTo。
        const long msgE = 7305;
        const long msgF = 7306;
        var before = sends.Count;

        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "接的是C那个哏", "replyTo": {{msgC}}}""");
        await protocol.SendGroupMessageAsync(groupId, 30005, "老王", "接着聊", msgE, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") > before,
            TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        var afterChoice = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("接的是C那个哏"))
            .LastOrDefault();
        Check("★ 模型用 replyTo 指认的目标被采信（它自己知道在回哪句）",
            QuotedMessageId(afterChoice) == msgC,
            $"引用 id = {QuotedMessageId(afterChoice)?.ToString() ?? "无"}，期望 {msgC}（模型指认的是 C）");

        // 编造一个不存在的编号：绝不能把引用挂到他不认识的消息上（QQ 会报错或引到别人头上）
        const long bogusId = 999999999;
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "编造编号", "replyTo": {{bogusId}}}""");
        await protocol.SendGroupMessageAsync(groupId, 30006, "老王", "再来一句", msgF, ct: cts.Token);
        await WaitUntilAsync(
            () => protocol.ActionsReceived
                .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
                .Any(a => MessageText(a).Contains("编造编号")),
            TimeSpan.FromSeconds(40));
        await Task.Delay(800);

        var afterBogus = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Where(a => MessageText(a).Contains("编造编号"))
            .LastOrDefault();
        Check("★ 模型编造的 replyTo 不会被采信（防挂到不存在的消息上）",
            QuotedMessageId(afterBogus) != bogusId,
            $"引用 id = {QuotedMessageId(afterBogus)?.ToString() ?? "无"}");

        await bot.StopAsync();
    }
}
