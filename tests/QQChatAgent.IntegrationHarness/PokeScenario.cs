using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S20 原生小表情 + 戳一戳。
///
/// 两件事都是“协议端报了、机器人看不懂”的典型：
///   • 小表情：QQ 的 face 段以前统一写成 [表情]，模型只看到“有个表情”，不知道是微笑还是捂脸，
///     于是答非所问（群友发个[捂脸]它当成发表情包）。现在要带上名字（id 表来自 NapCat 自己的配置）。
///   • 戳一戳：notice 事件以前直接丢掉，机器人被戳完全没反应；也要能回话、能戳回去，
///     而且不能陷入“你戳我我戳你”的拉锯（同一个人连着戳只回一次 + 主动戳人有频率门）。
/// </summary>
public static partial class Program
{
    /// <summary>取出每份提示词里的“心情段”（只截到下一个段落，避免提示词里的例子干扰断言）。</summary>
    private static List<string> MoodSections(IEnumerable<string> prompts)
    {
        var sections = new List<string>();
        foreach (var t in prompts)
        {
            var start = t.IndexOf("[你此刻的心情]", StringComparison.Ordinal);
            if (start < 0)
            {
                sections.Add("（无心情段）");
                continue;
            }

            var end = t.IndexOf("\n\n", start + 1, StringComparison.Ordinal);
            var len = (end < 0 ? t.Length : end) - start;
            sections.Add(t.Substring(start, len).Replace("\n", " "));
        }

        return sections;
    }

    /// <summary>取“带戳一戳指令”的那次请求的 system 提示词（断言心情/戳一戳段落用）。</summary></summary>
    private static string SystemTextOfLastPokeRequest(MockOpenAi openAi)
        => openAi.Requests.Select(SystemText).LastOrDefault(t => t.Contains("[戳一戳怎么回]")) ?? string.Empty;

    private static async Task RunPokeScenarioAsync()
    {
        Section("S20 原生小表情识别 + 戳一戳（回话 / 戳回去 / 防拉锯）");

        const int openAiPort = 17824;
        const int botWsPort = 13032;
        const int panelPort = 18095;
        const long groupId = 66682;
        const long alice = 20001;
        const long bob = 20002;
        const long SelfBotId = 10001;
        var dataDir = NewDataDir("s20");

        using var openAi = new MockOpenAi(openAiPort);
        openAi.Start();

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
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_ENABLE_POKE"] = "1",
            ["QQCHAT_POKE_COOLDOWN"] = "600", // 冷却搞大，专门验证“连着戳只回一次”
            ["QQCHAT_MOOD_TTL"] = "3"          // 心情 3 秒就过期（下面要验证过期回落）
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);
        using var protocol = new MockProtocol();
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}/", cts.Token);
        await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("已启动")), TimeSpan.FromSeconds(20));

        // ---- 1) 小表情：face / mface / sface / 认不出来的 id 都要能读出来 ----
        openAi.EnqueueReply("""{"suitability": 90, "reply": "看到了"}""");
        await protocol.SendRawAsync(new JsonObject
        {
            ["post_type"] = "message",
            ["message_type"] = "group",
            ["sub_type"] = "normal",
            ["message_id"] = 41001,
            ["group_id"] = groupId,
            ["user_id"] = alice,
            ["self_id"] = SelfBotId,
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            ["message"] = new JsonArray
            {
                new JsonObject { ["type"] = "at", ["data"] = new JsonObject { ["qq"] = SelfBotId.ToString() } },
                new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = "你看这几个" } },
                new JsonObject { ["type"] = "face", ["data"] = new JsonObject { ["id"] = "14" } },   // 微笑
                new JsonObject { ["type"] = "face", ["data"] = new JsonObject { ["id"] = "264" } },  // 捂脸
                new JsonObject { ["type"] = "face", ["data"] = new JsonObject { ["id"] = "99999" } }, // 表里没有 → 如实写未知
                new JsonObject { ["type"] = "mface", ["data"] = new JsonObject { ["summary"] = "尴尬", ["emoji_id"] = "1" } },
                new JsonObject { ["type"] = "sface", ["data"] = new JsonObject { ["id"] = "3", ["text"] = "晚安" } },
                new JsonObject { ["type"] = "dice", ["data"] = new JsonObject { ["result"] = "4" } }
            },
            ["sender"] = new JsonObject { ["user_id"] = alice, ["nickname"] = "小明", ["card"] = "小明", ["role"] = "member" }
        }.ToJsonString(), cts.Token);

        var facePrompt = await WaitUntilAsync(
            () => openAi.Requests.Any(r => UserTexts(r).Any(t => t.Contains("[表情:微笑]"))),
            TimeSpan.FromSeconds(30));
        var faceText = openAi.Requests
            .SelectMany(r => UserTexts(r))
            .FirstOrDefault(t => t.Contains("[表情:微笑]")) ?? string.Empty;

        Check("★ 小表情带名字进提示词（14=微笑 / 264=捂脸）",
            facePrompt && faceText.Contains("[表情:捂脸]"),
            $"提示词里这行是：{Truncate(faceText, 200)}");
        Check("认不出来的表情如实写成“未知(id)”，不瞎猜名字",
            faceText.Contains("[表情:未知(99999)]"),
            Truncate(faceText, 200));
        Check("动画表情用协议端给的 summary，超级表情用 text，骰子带点数",
            faceText.Contains("[动画表情:尴尬]") && faceText.Contains("[超级表情:晚安]") && faceText.Contains("[骰子:4]"),
            Truncate(faceText, 200));

        // ---- 2) 被人戳：按语境回话，并且能戳回去 ----
        await WaitUntilAsync(() => protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg"),
            TimeSpan.FromSeconds(20));
        var repliesBeforePoke = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");

        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "戳我干嘛", "poke": {{alice}}}""");
        await protocol.SendPokeAsync(groupId, alice, SelfBotId, cts.Token);

        var pokeReplied = await WaitUntilAsync(
            () => protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") > repliesBeforePoke,
            TimeSpan.FromSeconds(25));
        Check("★ 被戳会回话（戳一戳事件不再被丢掉）", pokeReplied,
            $"机器人日志尾部：{Truncate(string.Join(" ｜ ", bot.OutputLines.TakeLast(6)), 300)}");
        Check("★ 模型想戳回去时真的发出了 group_poke（带对方 QQ 号）",
            protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "group_poke" &&
                                              a["params"]?["user_id"]?.GetValue<long>() == alice &&
                                              a["params"]?["group_id"]?.GetValue<long>() == groupId),
            string.Join(" | ", protocol.ActionsReceived.Select(a => a["action"]?.GetValue<string>()).Distinct()));
        var pokeLines = openAi.Requests.SelectMany(r => UserTexts(r)).Where(t => t.Contains("戳了")).ToList();
        Check("戳一戳会进上下文，模型看得到是谁戳的",
            pokeLines.Any(t => t.Contains("戳了你一下") && t.Contains("小明")),
            $"提示词里的戳一戳行：{Truncate(string.Join(" ｜ ", pokeLines.TakeLast(2)), 240)}");

        // 线上实测（18:40）：被戳后那句回复带了引用，挂到 55 分钟前那条消息上了 ——
        // 戳一戳不是消息，没有可引用的目标；启发式只能抽“上下文里最后一条别人的消息”，挂上去就是挂错。
        var pokeReply = protocol.ActionsReceived.LastOrDefault(a =>
            a["action"]?.GetValue<string>() == "send_group_msg" && MessageText(a).Contains("戳我干嘛"));
        Check("★ 戳一戳触发的回复不去引用旧消息（没有“在回哪条”这回事）",
            pokeReply is not null && QuotedMessageId(pokeReply) is null,
            $"该条消息的引用段：{(pokeReply is null ? "没找到这条回复" : QuotedMessageId(pokeReply)?.ToString() ?? "无")}");

        // ---- 2.5) 心情：被戳之后提示词里要带“我现在的心情”，模型还能顺手写一句 ----
        var moodPrompt = SystemTextOfLastPokeRequest(openAi);
        var moodSection = moodPrompt.Contains("[你此刻的心情]")
            ? moodPrompt[moodPrompt.IndexOf("[你此刻的心情]", StringComparison.Ordinal)..]
            : "（没有这段）";
        Check("★ 被戳后提示词带上【你此刻的心情】（模型才有“当前状态”可依据）",
            moodPrompt.Contains("[你此刻的心情]") && moodPrompt.Contains("被戳"),
            $"提示词片段：{Truncate(moodSection, 220)}");

        // 模型写一句心情 → 存下来 → 下一轮请求的提示词里能看到
        // 心情用一句**提示词里不可能出现的**字符串，否则断言会被提示词里的例子（“被戳烦了”就是这么写的例子）
        // 捧中 —— 那种“假绿”反向验证时看不出来，但证明力是零。
        openAi.EnqueueReply("""{"suitability": 90, "reply": "别闹啦", "mood": "测试心情xyz"}""");
        await protocol.SendGroupMessageAsync(groupId, bob, "小刚", "在吗", 41003, mentionBot: true, ct: cts.Token);
        var moodStored = await WaitUntilAsync(
            () => bot.OutputLines.Any(l => l.Contains("心情变成") && l.Contains("测试心情xyz")),
            TimeSpan.FromSeconds(25));
        Check("★ 模型写的心情会被记住（并进日志）", moodStored,
            $"日志：{Truncate(string.Join(" ｜ ", bot.OutputLines.Where(l => l.Contains("心情")).TakeLast(3)), 240)}");

        // 写下的心情要真的进下一轮**心情段**（只看整段提示词会被提示词里的例子骗到）
        var requestsBeforeMoodUse = openAi.Requests.Count;
        openAi.EnqueueReply("""{"suitability": 90, "reply": "嗯"}""");
        await protocol.SendGroupMessageAsync(groupId, bob, "小刚", "还在吗", 41004, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(() => openAi.Requests.Count > requestsBeforeMoodUse, TimeSpan.FromSeconds(25));
        var moodSectionsUsed = MoodSections(openAi.Requests.Skip(requestsBeforeMoodUse).Select(SystemText));
        Check("★ 写下的心情会进下一轮提示词（真的被用上）",
            moodSectionsUsed.Any(s => s.Contains("测试心情xyz")),
            $"心情段：{string.Join(" ｜ ", moodSectionsUsed)}");

        // ---- 2.55) 心情过期：超过 MoodTtlSeconds 没更新就回落，不再影响行为 ----
        // （默认 2 小时；这里把 TTL 设成 3 秒才测得到。否则早上写的“被戳烦了”会影响一整天。）
        // 注意：过期是**懒判定**（下一次用到心情时才收），所以必须先等过 TTL、再发一条消息触发一次请求，
        // 然后只看“这次请求之后”的记录 —— 否则读到的是触发前那份旧提示词（第一版就掉进这个坑）。
        await Task.Delay(4500);
        var requestsBeforeExpiry = openAi.Requests.Count;
        openAi.EnqueueReply("""{"suitability": 90, "reply": "哦"}""");
        await protocol.SendGroupMessageAsync(groupId, bob, "小刚", "过一会儿了", 41005, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => openAi.Requests.Count > requestsBeforeExpiry, TimeSpan.FromSeconds(25));
        var expiredLogged = await WaitUntilAsync(
            () => bot.OutputLines.Any(l => l.Contains("心情") && l.Contains("过期")),
            TimeSpan.FromSeconds(15));
        var promptsAfterExpiry = openAi.Requests.Skip(requestsBeforeExpiry).Select(SystemText).ToList();
        var moodSections = MoodSections(promptsAfterExpiry);
        Check("★ 心情过期后会回落（不再把模型那句旧心情塞进提示词）",
            expiredLogged && moodSections.Count > 0 && moodSections.All(s => !s.Contains("测试心情xyz")),
            $"过期日志：{expiredLogged}；触发后的请求数 {promptsAfterExpiry.Count}；" +
            $"还带着旧心情吗：{moodSections.Any(s => s.Contains("测试心情xyz"))}；实际心情段：{string.Join(" ｜ ", moodSections)}");

        var (_, moodBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("过期后设置接口里的心情字段也被清空（面板不再显示一句不算数的心情）",
            moodBody.Contains("\"mood\":\"\""),
            Truncate(System.Text.RegularExpressions.Regex.Match(moodBody, "\"mood\":[^,]*").Value, 80));

        // ---- 2.6) “不必每次被戳都回戳”：连着被戳时心情变差，代码侧直接拦掉回戳 ----
        // 上面已经有两轮被戳（17 步里 1 次 + 这一段 1 次）→ 超过阈值，模型再怎么想戳也拦下。
        var pokesBefore = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "group_poke");
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "又戳", "poke": {{bob}}}""");
        await protocol.SendPokeAsync(groupId, bob, SelfBotId, cts.Token);
        var moodBlocked = await WaitUntilAsync(
            () => bot.OutputLines.Any(l => l.Contains("没心情回戳") || l.Contains("现在没心情")),
            TimeSpan.FromSeconds(25));
        var pokesNow = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "group_poke");
        Check("★ 连着被戳时心情不好 → 就算模型想回戳也不戳（不必每次都回戳）",
            moodBlocked && pokesNow == pokesBefore,
            $"group_poke 次数 {pokesBefore} → {pokesNow}；" +
            $"日志：{Truncate(string.Join(" ｜ ", bot.OutputLines.Where(l => l.Contains("戳") || l.Contains("心情")).TakeLast(3)), 240)}");

        // ---- 3) 同一个人连着戳：冷却内不再回（不参与互戳拉锯）----
        var repliesAfterFirstPoke = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        await Task.Delay(1200);
        // 用同一个人（小刚）再戳一次：冷却门的判据是“同一个人连着戳”，
        // 中途换人会把记录重置（前面那几段刚用过小明，再拿小明测就测不出东西了）。
        await protocol.SendPokeAsync(groupId, bob, SelfBotId, cts.Token);
        await Task.Delay(6000);
        Check("★ 同一个人连着戳只回一次（冷却门拦住了第 2 次）",
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") == repliesAfterFirstPoke,
            $"回复条数 {repliesAfterFirstPoke} → {protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg")}；" +
            $"日志：{Truncate(string.Join(" ｜ ", bot.OutputLines.Where(l => l.Contains("戳")).TakeLast(3)), 240)}");

        // ---- 4) 别人互戳：只记进上下文，不插话 ----
        // 判据用“有没有因此产生模型请求”而不是“有没有发消息”：
        // 只看消息数的话，模型没回、回得慢、被判沉默时都算“通过”，把实现退回去也不会红（假绿）。
        var requestsBeforeOtherPoke = openAi.Requests.Count;
        var beforeOtherPoke = protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg");
        // 用另一个人来戳（小刚 → 小明）：如果还让小明戳，上一段那条 600 秒冷却会把这一步也拦住，
        // 那“把互戳插话的守卫退回去”就不会变红（又一次假绿 —— 反向验证替我抓到了）。
        await protocol.SendPokeAsync(groupId, bob, alice, cts.Token);
        await Task.Delay(6000);
        Check("别人互戳不主动插话（不产生模型请求，只进上下文）",
            openAi.Requests.Count == requestsBeforeOtherPoke &&
            protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg") == beforeOtherPoke,
            $"模型请求 {requestsBeforeOtherPoke} → {openAi.Requests.Count}，" +
            $"回复 {beforeOtherPoke} → {protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_group_msg")}");

        // ---- 5) 防编造：模型报个上下文里没有的号，不能真去戳人 ----
        // 先把主动戳人的冷却改成 0 —— 否则一条“戳人才 20 秒”的频率门会把这一步也拦下，
        // 断言就又变成假绿（把号码校验退回去也不会红）。
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            using var content = new StringContent("{\"pokeCooldownSeconds\":0}", Encoding.UTF8, "application/json");
            using var res = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings", content, cts.Token);
            Check("把主动戳人的冷却改成 0（后面那步才测得到“号码校验”）", res.IsSuccessStatusCode, res.StatusCode.ToString());
        }
        openAi.EnqueueReply("""{"suitability": 90, "reply": "另一个话题", "poke": 999999999}""");
        await protocol.SendGroupMessageAsync(groupId, bob, "小刚", "晚上吃什么", 41002, mentionBot: true, ct: cts.Token);
        await WaitUntilAsync(
            () => openAi.Requests.SelectMany(r => UserTexts(r)).Any(t => t.Contains("晚上吃什么")),
            TimeSpan.FromSeconds(30));
        var fabricateIgnored = await WaitUntilAsync(
            () => bot.OutputLines.Any(l => l.Contains("999999999") && l.Contains("忽略")),
            TimeSpan.FromSeconds(15));
        Check("★ 模型编造的 poke 号码不会被采信（日志里说清了为什么）",
            fabricateIgnored,
            $"日志尾部：{Truncate(string.Join(" ｜ ", bot.OutputLines.Where(l => l.Contains("戳")).TakeLast(3)), 240)}");
        Check("★ 也不会真去戳那个不存在的号码",
            !protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() is "group_poke" or "friend_poke" &&
                                               a["params"]?["user_id"]?.GetValue<long>() == 999999999),
            string.Join(" | ", protocol.ActionsReceived.Where(a => a["action"]?.GetValue<string>() is "group_poke" or "friend_poke")
                .Select(a => a["params"]?.ToJsonString())));

        // ---- 6) 设置页能读写这两个开关 ----
        var (settingsStatus, settingsBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        var pokeFields = string.Join(" ", System.Text.RegularExpressions.Regex
            .Matches(settingsBody, "\"enablePoke\":\\w+|\"pokeCooldownSeconds\":\\d+")
            .Select(m => m.Value));        Check("设置接口暴露 enablePoke / pokeCooldownSeconds（并能改）",
            settingsStatus == 200 && pokeFields.Contains("\"enablePoke\":true") && pokeFields.Contains("\"pokeCooldownSeconds\":0"),
            $"实际值：{(pokeFields.Length > 0 ? pokeFields : "一个都没找到")}");
        Check("设置接口能读写成心情",
            settingsBody.Contains("\"mood\":") && settingsBody.Contains("\"moodSummary\":"),
            "payload 里没有 mood / moodSummary");
    }
}
