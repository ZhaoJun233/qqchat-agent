using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QQChatAgent.Models;

namespace QQChatAgent.Services.Agent;

/// <summary>
/// Agent 大脑：OpenAI 兼容 Chat Completions 客户端。
/// 由用户自配 Base URL / API Key / 模型，可对接 OpenAI、DeepSeek、通义、本地 Ollama 等。
/// </summary>
public sealed class OpenAiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly ImageDownloader _imageDownloader = new();

    private AppSettings _settings;

    public OpenAiClient(AppSettings settings) => _settings = settings;

    /// <summary>本机登录的机器人 QQ 号（注入模型上下文，帮助理解 @ 与身份）。</summary>
    public string? BotIdentity { get; set; }

    /// <summary>模型人设档案（可选，请求时注入系统上下文）。</summary>
    public string? BotPersona { get; set; }

    /// <summary>AI 对话欲望（0-100）：越高越倾向主动参与群聊发言。</summary>
    public int AiDesire { get; set; } = 50;

    /// <summary>发言适合度阈值（0-100）：模型评出低于此值则不发言。**代码侧强制执行**。</summary>
    public int SuitabilityThreshold { get; set; } = 10;

    /// <summary>给模型的最大上下文消息条数（与 BotAgent 侧共用同一值，避免两头不一样）。</summary>
    public int MaxContextMessages { get; set; } = 200;


    public void UpdateSettings(AppSettings settings) => _settings = settings;

    /// <summary>
    /// 生成回复。context 为按时间正序的最近消息（角色已映射为 system/user/assistant）。
    /// 返回结构化结果：调用方据此判断“沉默”还是“发言”（含模型自评的适合度）。
    /// 网络/接口异常向上抛，由调用方记日志。
    /// </summary>
    public async Task<CompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> context, string? profilesText = null, CancellationToken ct = default,
        IReadOnlyList<StickerChoice>? stickers = null, bool pokeContext = false, string? moodText = null, string? musicText = null, string? linkText = null, bool enableListen = false, bool enableVoice = false, string? recallText = null, bool enableWebSearch = false, string? searchText = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("未配置 API Key");
        }

        if (_settings.ApiKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            _settings.ApiKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("API Key 填成了 URL，请填入密钥 token（设置页「Agent 大脑」）");
        }

        var messages = new JsonArray();

        // 统一在此处截断一次（以前 BotAgent 和本方法各截一次，重复且容易不一致）
        var window = context.Count > MaxContextMessages
            ? context.Skip(context.Count - MaxContextMessages).ToArray()
            : context;

        // 可供模型指认的“最近几条别人的消息”：给它们附上 (#QQ消息id)，模型在 JSON 里用 replyTo 指明它在回哪一条。
        // 为什么要这么做：光靠启发式（“最新那条”或“排队时的触发”）猜不准 ——
        // 线上就出现过“正文在接一个哏，引用却挂在另一个人那句上”。模型自己知道回哪句，让它说出来。只标最近 16 条。
        // 撤回的消息不在此列：引用一条群里已经看不到的消息，群友看到的就是莫名其妙。
        var quotableIds = new HashSet<long>(
            window.Where(m => m.Role == MessageRole.Peer && !m.Recalled && m.QqMessageId is > 0)
                  .TakeLast(16)
                  .Select(m => m.QqMessageId!.Value));

        var systemContent = SystemPrompt;
        if (!string.IsNullOrWhiteSpace(BotIdentity))
        {
            systemContent += $"\n你是登录账号 QQ：{BotIdentity} 的机器人（群聊中别人 @QQ{BotIdentity} 或喊你昵称就是在叫你）。";
        }

        if (!string.IsNullOrWhiteSpace(BotPersona))
        {
            systemContent += "\n\n[机器人人设档案]\n" + BotPersona.Trim();
        }

        systemContent += BuildSuitabilityInstruction();

        // 引用谁：最近几条别人的消息都带了 (#id)，让模型自己指认
        if (quotableIds.Count > 0)
        {
            systemContent +=
                "\n\n[回复谁]\n上下文里形如 `{某某}{内容--时间} (#123456)` 的是最近几条**别人发的**消息，" +
                "# 后面是消息编号。如果你这句话（或表情包）是在回其中某一条，在 JSON 里加 replyTo 字段填那个编号，" +
                "例如：{\"suitability\": 80, \"reply\": \"…\", \"replyTo\": 123456}；" +
                "如果是在回最新的那一句、或者不确定，就不要填 replyTo（不要自己编编号）。";
        }

        // 消息里的标记：表情包和戳一戳在上下文里都是带方括号/全角括号的“事件写法”，
        // 不解释的话模型会把“（戳一戳）某人 戳了你一下”当成别人真说过这句话。
        systemContent +=
            "\n\n[上下文里的标记]\n" +
            "`[表情:微笑]`/`[动画表情:…]` 是对方发的 QQ 原生小表情（名字即它的含义），`[图片]`/`[语音]` 同理，" +
            "`[已撤回] xxx` 表示这句 xxx 发出后**被撤回了**：内容你看得到（你当时在场），但群里其他人已经看不到它了，" +
            "`（戳一戳）某某 戳了你一下` 表示某某在 QQ 里戳了你——那是动作不是文字。";

        // 当前心情：给模型一个“我现在什么状态”的锚，让它的话风/要不要理人有个连贯的落点
        if (!string.IsNullOrWhiteSpace(moodText))
        {
            systemContent +=
                "\n\n[你此刻的心情]\n" + moodText.Trim() +
                "\n（心情只影响你说话的语气与热络程度：烦的时候就短、敷衽、甚至懒得理；心情好可以开玩笑。别把它当成要宣告的信息。）";
        }

        // 群里刚分享的音乐：把“实测到的事实 + 歌词”交给模型，让它聊得像真听过（而不是望着歌名编）
        if (!string.IsNullOrWhiteSpace(musicText))
        {
            systemContent +=
                "\n\n[群里刚分享的音乐]\n" + musicText.Trim() +
                "\n（这是你刚“听”过的一首歌：可以就节奏/旋律/歌词/年代感聊两句，或顺着群友的话接。" +
                "歌名、歌手、时长、BPM、响度、段落这些事实一律以上面的实测数据为准，不要另编；" +
                "歌词里没有的内容不要虚构，也别把整段歌词抰出来 —— 引用一两句点到即止，像真的听过那样随口提。）";
        }

        // 群里消息里的链接：机器人已经打开看过（标题/摘要），别让它对着一串 URL 猜
        if (!string.IsNullOrWhiteSpace(linkText))
        {
            systemContent +=
                "\n\n[群里刚发的链接]\n" + linkText.Trim() +
                "\n（链接内容已取回，按上面的标题/摘要聊就行；别编造页面里没有的细节，也别把整段摘要复述一遍。）";
        }

        // 联网搜索：模型可以要求“去查一下”（search 字段）或“读一下这个页面”（read 字段）。
        // 两轮动作：机器人先把结果拿回来，下一轮它再拿着事实说话（和听音乐同一套思路）。
        if (enableWebSearch)
        {
            systemContent +=
                "\n\n[联网搜索]\n" +
                "当你要用的信息**在你自己脑子里不可靠**、而且这件事一查就能确认时（新闻、某游戏/番剧的最新情报与攻略、" +
                "价格、开服/发售时间、某人是声优/作者/成员这类事实），在 JSON 里加 search 字段写上要查什么：" +
                "{\"suitability\": 85, \"reply\": \"我去查一下\", \"search\": \"碧蓝档案 砂狼白子 声优\"}。\n" +
                "机器人会真的去搜，把结果（带来源）交给你，下一轮你就能拿着事实回答 —— 而不是靠印象编。\n" +
                "也可以让它读某个具体网页：在 JSON 里加 read 字段填 URL（图符群友发过的链接）。\n" +
                "规矩：① 不确定才搜，能自己想起来的别搜；② 同一件事不要连着搜两次；③ 不要每句话都搜（搜一次要花几秒）；" +
                "④ 搜不到/读不到就如实说“没查到”，**绝对不要**用记忆里的旧信息假装是刚查到的；" +
                "⑤ search/read 是后台动作：填了它你这一轮该接的话照接（reply 正常写）。";
        }

        // 刚有人撤回了消息：告诉模型“谁撤的 + 上下文里那条已标成 [已撤回]”
        if (!string.IsNullOrWhiteSpace(recallText))
        {
            systemContent +=
                "\n\n[有人撤回了消息]\n" + recallText.Trim() +
                "\n（规矩：① 你可以记得内容，但**不要复述、不要引用、更不要说“我都看见了/撤什么”这类话**，" +
                "也不要暗示自己看到了 —— 撤回就是不想让它留在群里；" +
                "② 如果对方撤回后马上又发了更正/补充，就当没这回事，顺着新内容接；" +
                "③ 只有当大家都在好奇、气氛适合时，才可以轻描淡写问一句，别审问。）";
        }

        // 刚搜到的结果（或读到的网页正文）：交给模型，用完就清
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            systemContent +=
                "\n\n[刚查到的资料]（你上一轮说要去查，这就是查回来的）\n" + searchText.Trim() +
                "\n（怎么用：① 用你自己的语气说出来 —— 就像你本来就知道这件事一样，别用“根据资料/搜索结果显示”这种播报腔；" +
                "② 接着刚才的话头说，别像换了个人：你的人物设定、口头禅、说话节奏照旧；" +
                "③ 只讲跟那个问题有关的部分，不要把整段资料念一遍；" +
                "④ 上面没写的细节别编，拿不准就说拿不准；" +
                "⑤ 来源链接不用贴（除非有人问“哪来的”）。）";
        }

        // 想听一首歌：模型可以主动要求“让我听听这首歌”（群里让它听歌就是走这条路）
        if (enableListen)
        {
            systemContent +=
                "\n\n[想听一首歌]\n" +
                "群里让你听/放某首歌，或者你想就某首歌接话但没把握时，在 JSON 里加 listen 字段写上歌名（带歌手更好）：" +
                "{\"suitability\": 85, \"reply\": \"我去听听\", \"listen\": \"洛天依 if love == true\"}。\n" +
                "机器人会去网易云搜这首歌、下一份低码率音频做波形分析，然后把歌词和实测数据给你 —— 下一轮你就能真的聊这首歌了。\n" +
                "listen 只在确实需要“听过”时用（同一首歌不要反复请求），也别拿它当通用搜索框。\n" +
                "另外，语境合适时可以**主动分享**一首歌给群里（有人要推荐、聊到某首歌、气氛适合来一首），" +
                "在 JSON 里加 shareSong 字段写歌名（带歌手更好），机器人会搜到后发一张网易云音乐卡片：" +
                "{\"suitability\": 85, \"reply\": \"来一首这个\", \"shareSong\": \"起风了 买辣椒也用券\"}。" +
                "分享要克制：别反复推同一首，也别每轮都发卡片（卡片比文字“重”得多）。";
        }

        // 用语音说话：模型可以要求“这句用语音说”（speak 字段）。语音比文字“重”得多
        // （合成要几秒、占流量、群里显眼），所以提示词里反复强调克制；代码侧还有一道最小间隔门。
        if (enableVoice)
        {
            var maxChars = Math.Clamp(_settings.VoiceMaxChars, 10, 300);
            systemContent +=
                "\n\n[用语音说话]\n" +
                "你**偶尔**可以用语音说一句。想这么做时，在 JSON 里加 speak 字段写上要说出口的话（≤ " + maxChars + " 字）：" +
                "{\"suitability\": 85, \"reply\": \"…\", \"speak\": \"这句话我想用声音说\"}。机器人会把它合成语音发出去。\n" +
                "什么时候值得用：情绪比文字重的时候（道谢、撒娇、学人说话、唱歌、委屈/开心），" +
                "或者群友明确让你“说句话/唱一个/用语音”。**绝大多数时候还是打字**，别每句都发语音，" +
                "也别连续两条都是语音 —— 群里语音是“稀罕事”，滥了就烦人。\n" +
                "speak 里写的就是要说出口的那句话：口语化、短、别放链接/代码/括号里的舞台说明（如“(笑)”）；" +
                "填了 speak 就不要再在 reply 里重复同一句话（语音已经说过了）。" +
                "说不好或超过字数上限时，这次就按普通文字回，不要在上下文里提到“语音发不出去”。";
        }

        // 被戳过才给的指令：戳回去是**可选**动作，看当下心情 —— 不必每次被戳都戳一次
        if (pokeContext)
        {
            systemContent +=
                "\n\n[戳一戳怎么回]\n" +
                "刚有人戳了你，按人设、上下文和心情回一句（或者只发个表情包）。" +
                "回戳是可选动作，**不要每次被戳都回戳**：心情好/想玩可以偶尔戳回去，" +
                "刚被同一个人或几个人连着戳过、心里烦的时候就不要回戳（回句话甚至不理都行）。" +
                "决定回戳时，在 JSON 里加 poke 字段填对方的 QQ 号，例如：{\"suitability\": 85, \"reply\": \"…\", \"poke\": 123456}；" +
                "只能戳上下文里出现过的人（戳你的那个人用他的 QQ 号），不要编造号码。" +
                "另外可以用 mood 字段顺手写一句你现在的心情（≤ 12 字，如“被戳烦了”“心情不错”），会记到下一轮。";
        }

        // 表情包：只把“按语境挑出来的几张”给模型，而不是整库（库可能上千张）。
        if (stickers is { Count: > 0 })
        {
            systemContent += BuildStickerInstruction(stickers);
        }

        // 角色卡片：优先使用外部传入的人物档案（按 QQ 号建表积累），否则由最近消息聚合
        var participants = profilesText is not null
            ? new[] { profilesText }
            : BuildParticipantProfiles(window);
        if (participants.Length > 0)
        {
            systemContent += "\n\n[会话参与者档案（由他们的历史发言总结而来，回复时参考每个人是谁、说过什么）]\n" +
                             string.Join("\n\n", participants);
        }

        messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemContent });

        // 最近上下文里出现图片时，在系统提示中提示模型可以看图
        if (window.Any(m => m.ImageUrls is { Count: > 0 }))
        {
            systemContent += "\n(消息中包含图片，已一并提供，请先看图再结合上下文回复。)";
            messages[^1] = new JsonObject { ["role"] = "system", ["content"] = systemContent };
        }

        foreach (var msg in window) // 上下文窗口
        {
            if (msg.Role == MessageRole.System)
            {
                continue; // 本地系统提示（如"已连接"）不发给模型，避免噪音
            }

            // 对方消息按 {发送者}{内容--时间} 组织，帮助模型分辨谁在何时说了什么；
            // 最近几条再附上 (#id)，供 replyTo 引用。
            // 已撤回的：正文前面加 [已撤回] 标记（**内容保留** —— 它确实看过，只是要让模型
            // 知道“这条已经收回去了”，引用/复述时得自己拿掉分寸）。
            var shownText = msg.Recalled ? RecallMark + msg.Text : msg.Text;
            var content = msg.Role == MessageRole.Peer && !string.IsNullOrWhiteSpace(msg.SenderName)
                ? $"{{{msg.SenderName}}}{{{shownText}--{FormatTime(msg.Timestamp)}}}" +
                  (msg.QqMessageId is long qid && quotableIds.Contains(qid) ? $" (#{qid})" : string.Empty)
                : shownText;
            var role = msg.Role == MessageRole.Self ? "assistant" : "user";

            // 多模态：消息带图片时，把图片（下载转 base64）一并发给模型识图
            if (msg.ImageUrls is { Count: > 0 } && msg.Role == MessageRole.Peer)
            {
                var contentParts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = content } };
                foreach (var url in msg.ImageUrls.Take(3)) // 每条消息最多带 3 张图
                {
                    // 异步下载（绝不同步阻塞 UI 线程），失败跳过该图
                    var dataUrl = await _imageDownloader.DownloadAsDataUrl(url, ct);
                    if (dataUrl is not null)
                    {
                        contentParts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl }
                        });
                    }
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = contentParts });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
        }

        // 上游（Gemini 等）不接受“最后一条是模型自己说的话”的请求：
        //   Requests ending with a model turn are not supported.
        // 而这恰好是机器人的一种正常情形 —— **自己触发的后续发言**：
        //   • 听完歌回来接着聊（模型填了 listen，分析完再请求一次）
        //   • 被戳之后想回一句（戳一戳不是消息，不往上下文里写）
        //   • 静默兜底补的那次请求
        // 这些时候上下文最后一条就是它自己刚说的话，一问就是 400。
        // 修法：补一条系统口吻的 user 轮把它顶成 user —— 顺便告诉模型“这是你自己的后续动作，
        // 不是又有人说话了”，免得它以为群里刚来了新消息、对着自己的话自问自答。
        var lastRole = messages.Count > 0 ? messages[^1]?["role"]?.GetValue<string>() : null;
        if (lastRole != "user")
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "（系统提示：上面最后一条是你自己刚说的话，之后没有新的群消息 —— " +
                              "本轮是你自己的后续动作触发的。想说就接着说，不想说就把 reply 留空。）"
            });
            FileLog.Write("Agent", "上下文以自己发言结尾 → 补一条系统口吻的 user 轮（上游不接受 model-turn 结尾）");
        }

        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = messages,
            ["max_tokens"] = _settings.MaxTokens > 0 ? _settings.MaxTokens : 2048, // 仅防御非法值（旧数据可能为负数），不设上限
            ["temperature"] = 0.7
        };

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Chat Completions 返回 {(int)response.StatusCode}：{Truncate(detail, 200)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var contentNode = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content");
        var rawReply = contentNode.GetString();
        if (rawReply is null && contentNode.ValueKind == JsonValueKind.Array)
        {
            rawReply = string.Concat(contentNode.EnumerateArray().Select(s => s.GetProperty("text").GetString()));
        }

        return ParseModelOutput(rawReply);
    }

    /// <summary>
    /// 解析模型输出。约定：JSON {"suitability":0-100,"reply":"..."}。
    ///   • reply 为空 → 沉默
    ///   • 输出“看起来是 JSON”但解析不了 → 判为格式错误，**沉默**（绝不把 JSON 原文当成回复发出去）
    ///   • 完全不像 JSON（纯文本）→ 当普通回复，适合度未知
    /// </summary>
    /// <summary>纯文本回复的最短长度：1~2 个字的“回复”几乎都是上游被截断的碎片，不是真的想说话。</summary>
    private const int MinPlainTextReplyLength = 3;

    /// <summary>
    /// 可以单独成句的单字应答（中文口语里确实会这么用）。
    /// 白名单之外的单字一律按上游噪声处理 —— 群里的“彫”就是这么刷起来的。
    /// </summary>
    private const string SingleCharReplyWhitelist = "嗯哦啊哈呃哎咦喂喔唔草6?？!！~～";

    private static CompletionResult ParseModelOutput(string? rawReply)
    {
        if (string.IsNullOrWhiteSpace(rawReply))
        {
            return new CompletionResult(null, null, rawReply);
        }

        var text = StripCodeFence(rawReply.Trim());

        // 模型很爱在 JSON 前面写一句解释（“好的，我来回：”）或者把 JSON 裹在围栏里再另起一段 ——
        // 以前这种“不以 { 开头”的输出会直接走纯文本分支，把整段 JSON 发进群里。
        // 这里先在全文里找“长得像我们约定的那个 JSON”的片段。
        if (!text.StartsWith('{') && TryExtractJsonBlock(text, out var extracted))
        {
            text = extracted;
        }

        // 再兜一层：纯文本里带着我们的字段名（suitability/reply/sticker…）说明它本来就是想输出 JSON，
        // 只是格式没弄对 —— 宁可沉默，也绝不把 JSON 代码吐进群里。
        if (!text.StartsWith('{') && LooksLikeSchemaJson(text))
        {
            Services.FileLog.Warn("Agent",
                $"模型输出看似 JSON 但格式不对，按沉默处理（避免把代码发进群）：{Truncate(rawReply, 120)}");
            return new CompletionResult(null, null, rawReply);
        }

        // 只有“以 { 开头”才当作 JSON 尝试。
        // 理由：真人语气里也会出现花括号（“这个 {a:1} 是啥”），那些必须当普通文本发出去。
        if (!text.StartsWith('{'))
        {
            // 纯文本兜底：有些模型就是不按 JSON 输出，这里必须放行。
            // 但**极短**的纯文本是个例外 —— 群里实测过单字“悼”刷屏：
            // 上游（cli-proxy-api → Antigravity）偶发只回一个字符，旧实现把它当正常回复发了出去，
            // 而模型又能从上下文里读到自己那条“悼”，于是开始自我复读。
            // 不到 3 个字、又不像 JSON，就当作“没有回复”，并把原文写进日志以便回溯上游异常。
            if (VisibleLength(text) < MinPlainTextReplyLength)
            {
                Services.FileLog.Warn("Agent",
                    $"模型输出疑似被截断（非 JSON，只有 {VisibleLength(text)} 个可见字符），按沉默处理：{Truncate(text, 60)}");
                return new CompletionResult(null, null, rawReply);
            }

            return new CompletionResult(null, text, rawReply);
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        try
        {
            using var doc = JsonDocument.Parse(end > start ? text[start..(end + 1)] : text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new CompletionResult(null, null, rawReply);
            }

            int? suitability = null;
            if (root.TryGetProperty("suitability", out var s))
            {
                suitability = s.ValueKind switch
                {
                    JsonValueKind.Number => ReadScore(s),
                    JsonValueKind.String => int.TryParse(s.GetString(), out var parsed) ? parsed : null,
                    _ => null
                };
            }

            // 只有字符串才算回复；reply 是对象/数组时不能 GetString（会抛）
            string? reply = null;
            if (root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String)
            {
                reply = r.GetString()?.Trim();
            }

            // 单字回复：中文口语里“嗯/哦/哈”确实是正常应答，其余单字基本都是上游噪声——
            // 群里实测就是单个“彫”在刷屏（上游偶发只回一个字符）。
            if (reply is { Length: 1 } && !SingleCharReplyWhitelist.Contains(reply[0]))
            {
                Services.FileLog.Warn("Agent",
                    $"模型只回了 1 个字“{reply}”，不属于正常应答，按沉默处理。原文：{Truncate(rawReply, 80)}");
                return new CompletionResult(suitability, null, rawReply);
            }

            // 1 个字的正常应答：照发。
            if (reply is { Length: 1 })
            {
                Services.FileLog.Write("Agent", $"模型回复只有 1 个字（{reply}），原文：{Truncate(rawReply, 80)}");
            }

            // 表情包：模型可能只发图不说话，所以单独解析（id 去掉 # 前缀，拒绝奇怪的值）
            string? stickerId = null;
            if (root.TryGetProperty("sticker", out var st) || root.TryGetProperty("stickerId", out st))
            {
                var raw = st.ValueKind switch
                {
                    JsonValueKind.String => st.GetString(),
                    JsonValueKind.Number => st.ToString(),
                    _ => null
                };

                var cleaned = raw?.Trim().TrimStart('#').Trim();
                if (!string.IsNullOrWhiteSpace(cleaned) &&
                    cleaned.Length is >= 4 and <= 32 &&
                    cleaned.All(char.IsAsciiHexDigit))
                {
                    stickerId = cleaned.ToLowerInvariant();
                }
            }

            // 模型自己指认的“我在回哪条”（replyTo）。只取正整数：
            // 具体是否采信由上层校验（必须在本次上下文里），这里不做业务判断。
            long? replyToId = null;
            if (root.TryGetProperty("replyTo", out var rt) || root.TryGetProperty("reply_to", out rt))
            {
                replyToId = rt.ValueKind switch
                {
                    JsonValueKind.Number when rt.TryGetInt64(out var value) => value,
                    JsonValueKind.String when long.TryParse(rt.GetString(), out var parsed) => parsed,
                    _ => null
                };

                if (replyToId is <= 0)
                {
                    replyToId = null;
                }
            }

            // 模型想戳谁（poke，也可以是 pokeBack）。同样只取正整数，
            // “这个人到底存不存在”由上层用上下文校验（能防住模型编造号码）。
            long? pokeTargetId = null;            if (root.TryGetProperty("poke", out var pk) || root.TryGetProperty("pokeBack", out pk) || root.TryGetProperty("pokeTo", out pk))
            {
                pokeTargetId = pk.ValueKind switch
                {
                    JsonValueKind.Number when pk.TryGetInt64(out var value) => value,
                    JsonValueKind.String when pk.ValueKind == JsonValueKind.String && long.TryParse(pk.GetString(), out var parsed) => parsed,
                    JsonValueKind.True => 0, // "pokeBack": true = 戳回去，具体号码交给上层（别在这里猜）
                    _ => null
                };

                if (pokeTargetId is <= 0)
                {
                    pokeTargetId = null;
                }
            }

            // 模型想“听一听”某首歌（歌名/歌手）：群里让它听歌、或它自己想聊某首歌却没把握时用。
            // 机器人会拿这个名字去搜歌，搜到就下低码率音频分析波形，下一轮把歌词 + 实测给它。
            string? listen = null;
            if ((root.TryGetProperty("listen", out var ls) || root.TryGetProperty("听歌", out ls)) && ls.ValueKind == JsonValueKind.String)
            {
                var wanted = ls.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(wanted) && wanted.Length is >= 2 and <= 60)
                {
                    listen = wanted;
                }
            }

            // 模型想把某首歌分享给群里（发一张网易云卡片）：“推荐首歌/点歌/聊到某首歌”这类语境
            string? shareSong = null;
            if ((root.TryGetProperty("shareSong", out var ss) || root.TryGetProperty("share_song", out ss)) && ss.ValueKind == JsonValueKind.String)
            {
                var want = ss.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(want) && want.Length is >= 2 and <= 60)
                {
                    shareSong = want;
                }
            }

            // 模型想“用语音说这句”（speak）：值可以是字符串（要说的话），也可以是 true（= 用语音说 reply）。            // 真正能不能发由上层决定（开关/字数上限/频率门/服务可达），这里只负责取值与基本清洗。
            string? speak = null;
            if (root.TryGetProperty("speak", out var sp) || root.TryGetProperty("用语音说", out sp))
            {
                if (sp.ValueKind == JsonValueKind.String)
                {
                    var spoken = sp.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(spoken))
                    {
                        speak = spoken;
                    }
                }
                else if (sp.ValueKind == JsonValueKind.True)
                {
                    // {"speak": true} = 把 reply 用语音说（模型偷懒时也能用）
                    speak = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim();
                }
            }

            // 模型想“上网查一下”（search）/“读一下某个页面”（read）。
            // 真正去查是上层的事（要发 HTTP、有冷却），这里只取词：
            //   • search 太短（<2）/太长（>120）都不要 —— 太长基本是它在写句子，不是搜索词；
            //   • read 必须是 http(s) 地址（SSRF 闸门在上层）。
            string? search = null;
            if ((root.TryGetProperty("search", out var se) || root.TryGetProperty("查一下", out se)) && se.ValueKind == JsonValueKind.String)
            {
                var wanted = se.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(wanted) && wanted.Length is >= 2 and <= 120)
                {
                    search = wanted;
                }
            }

            string? read = null;
            if ((root.TryGetProperty("read", out var rd) || root.TryGetProperty("读一下", out rd)) && rd.ValueKind == JsonValueKind.String)
            {
                var url = rd.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(url) && url.Length <= 500 &&
                    (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    read = url;
                }
            }

            // 模型顺手写的心情（≤ 24 字）：存起来给下一轮用；太长/非字符串一律忽略
            string? mood = null;
            if (root.TryGetProperty("mood", out var md) && md.ValueKind == JsonValueKind.String)
            {
                mood = md.GetString()?.Trim();
            }

            return new CompletionResult(suitability, string.IsNullOrWhiteSpace(reply) ? null : reply, rawReply, stickerId, replyToId, pokeTargetId, mood, listen, shareSong, speak, search, read);
        }
        catch (JsonException)
        {
            // 长得像 JSON 却解析不了：判为格式错误 → 沉默。
            // （以前这里会落到“按普通文本处理”，把整段 JSON 发进群里。）
            return new CompletionResult(null, null, rawReply);
        }
    }

    /// <summary>
    /// 从一段文本里抠出第一个“像我们约定的” JSON 对象（带花括号配对、跳过字符串里的括号）。
    /// 判据是里面出现了我们的字段名 —— 免得把群友消息里引用的一小段 JSON 当成模型输出。
    /// </summary>
    private static bool TryExtractJsonBlock(string text, out string block)
    {
        block = string.Empty;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var j = i; j < text.Length; j++)
            {
                var c = text[j];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                switch (c)
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            var candidate = text[i..(j + 1)];
                            if (LooksLikeSchemaJson(candidate))
                            {
                                block = candidate;
                                return true;
                            }

                            j = text.Length; // 这个块不是，继续找下一个 {
                        }

                        break;
                }
            }
        }

        return false;
    }

    /// <summary>这段文本里有没有我们的约定字段（说明它想输出的是结构化回复）。</summary>
    private static bool LooksLikeSchemaJson(string text)
        => text.Contains("\"suitability\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"reply\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"sticker\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"replyTo\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"mood\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"speak\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"search\"", StringComparison.OrdinalIgnoreCase)
        || text.Contains("\"poke\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 可见字符数：忽略空白、零宽空格、BOM。用于判断输出是不是被上游截断的碎片。
    private static int VisibleLength(string text)
        => text.Count(c => !char.IsWhiteSpace(c) && c != '\u200b' && c != '\ufeff');

    /// 读发言适合度评分。
    /// 模型实际会输出 85 / 85.0 / 85.5 / "85" 各种形式；
    /// 旧实现用 GetInt32() 读，碰到 85.0 直接抛异常 → 整段 JSON 被当成回复发进群。
    /// </summary>
    private static int? ReadScore(JsonElement element)
    {
        if (element.TryGetInt32(out var integer))
        {
            return integer;
        }

        return element.TryGetDouble(out var value) && !double.IsNaN(value) && !double.IsInfinity(value)
            ? (int)Math.Round(value)
            : null;
    }

    /// <summary>去掉 ```json / ``` 围栏，返回内部正文。</summary>
    private static string StripCodeFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return text;
        }

        var body = text[(firstLineEnd + 1)..];
        var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fenceEnd >= 0 ? body[..fenceEnd] : body).Trim();
    }

    /// <summary>
    /// 表情包指令：把候选图连同它们的说明/关键词给模型，让它自己挑。
    /// 关键是“宁可不发”——表情包用错语境比不发更尴尬。
    /// </summary>
    private static string BuildStickerInstruction(IReadOnlyList<StickerChoice> stickers)
    {
        var sb = new StringBuilder();
        sb.Append("\n\n[可用表情包]");
        sb.Append("你可以用一张表情包来补充或代替文字（一轮最多一张）。按当下语境挑最贴切的那张；");
        sb.Append("没有合适的就正常发文字。表情包是调味品不是主食：一组对话里偶尔用一张就够，");
        sb.Append("不要每句都挂，也不要反复用同一张（除非它就是当下最好的回应）。\n");
        foreach (var sticker in stickers)
        {
            sb.Append("  #").Append(sticker.Id).Append(' ').Append(sticker.Description).Append('\n');
        }

        sb.Append("要发表情包时，在 JSON 里加上 sticker 字段，值为上面某个 # 后面的 id（不含 #），例如：");
        sb.Append("{\"suitability\": 85, \"reply\": \"\", \"sticker\": \"").Append(stickers[0].Id).Append("\"}。");
        sb.Append("只发表情包时 reply 留空；又想说话又发表情包就两个都填（先发文字再发图）。");
        return sb.ToString();
    }

    /// <summary>给表情包库用：下载图片原始字节（内部走同一套 SSRF 防护与大小限制）。</summary>
    public Task<(byte[] Data, string Mime, string Ext)?> DownloadImageAsync(string url, CancellationToken ct = default)
        => _imageDownloader.DownloadBytesAsync(url, ct);

    /// <summary>
    /// 表情包自巡检：让模型看一遍库（表格形式），自己决定删哪些。
    /// 返回要删的 id 与理由；解析不了就返回空（宁可什么都不删）。
    /// </summary>
    public async Task<(List<string> Delete, string? Reason)> CurateStickersAsync(string libraryTable, int maxDelete, CancellationToken ct = default)
    {
        var empty = (new List<string>(), (string?)null);
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(libraryTable) || maxDelete <= 0)
        {
            return empty;
        }

        await _stickerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var system =
                "你在帮一个 QQ 群聊机器人整理表情包库。下面是库里的图，每行：id | 说明（含关键词）| 用过几次 | 多久前加入。\n" +
                "挑出不值得留的：与群聊语境无关的（广告、聊天截图、二维码、纯文字通知、屏幕截图）、画质/内容不合适（低俗、恶心、涉政涉黄）、" +
                "说明模糊且从未用过的、和已有的重复表达。标了“24h 内用过”的不要动。\n" +
                $"最多删 {maxDelete} 张（也可以一张都不删）。只输出一行 JSON：" +
                "{\"delete\": [\"id1\", \"id2\"], \"reason\": \"一句话说清为什么删这些\"}。" +
                "没把握就少删：库里图少的时候宁可留着。";

            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = libraryTable }
                },
                ["max_tokens"] = 300,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return empty;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return ParseCuration(raw, maxDelete);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包巡检请求失败：{ex.Message}");
            return empty;
        }
        finally
        {
            _stickerGate.Release();
        }
    }

    /// <summary>解析巡检结果（只收合法 id，并强制不得超过上限）。</summary>
    internal static (List<string> Delete, string? Reason) ParseCuration(string? raw, int maxDelete)
    {
        var delete = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (delete, null);
        }

        var text = StripCodeFence(raw.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (delete, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()?.Trim()
                : null;

            foreach (var name in new[] { "delete", "deletes", "remove", "removes" })
            {
                if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in arr.EnumerateArray())
                {
                    var id = (item.ValueKind == JsonValueKind.String ? item.GetString() : null)?.Trim().TrimStart('#').Trim();
                    if (!string.IsNullOrWhiteSpace(id) && id.Length is >= 4 and <= 32 && id.All(char.IsAsciiHexDigit)
                        && !delete.Contains(id.ToLowerInvariant()))
                    {
                        delete.Add(id.ToLowerInvariant());
                    }
                }

                break;
            }

            if (delete.Count > maxDelete)
            {
                delete = delete.Take(maxDelete).ToList();
            }

            return (delete, reason);
        }
        catch (JsonException)
        {
            return (delete, null);
        }
    }

    private readonly SemaphoreSlim _stickerGate = new(1, 1);

    /// <summary>
    /// 让模型看一张图并给出“一句话说明 + 情绪/场景关键词 + 这是不是表情包”。
    /// 表情包能不能“按语境发”，完全取决于这一步：靠关键词才能检索；
    /// 而“是不是表情包”这一步是入库闸门 —— 群友发的聊天截图/广告不能被当成表情包收下来。
    /// 走独立闸门，不和聊天抢并发。
    /// </summary>
    public async Task<(string? Desc, List<string>? Tags, bool? IsSticker)> DescribeStickerAsync(byte[] image, string mime, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || image.Length == 0)
        {
            return (null, null, null);
        }

        await _stickerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(image)}";
            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] =
                            "你在给一个 QQ 群聊机器人的表情包库做入库审核。看图片内容，只输出一行 JSON：" +
                            "{\"sticker\": true/false, \"desc\": \"一句话说明这张图的画面与用途，20 字以内\", \"tags\": [\"3-6 个情绪或使用场景关键词\"]}。\n" +
                            "sticker 只在它真是“可以用来说话的表情包/梗图”（人或角色+情绪、能当反应那张图用）时为 true；" +
                            "聊天截图、屏幕截图、纯文字图、广告、二维码、文档照片、随手拍的实物 —— 这些一律 false（会被丢掉）。\n" +
                            "关键词要能用在其他句子里检索到它（例如：大笑、无语、嘲讽、点赞、崩溃、狗头、摸鱼）。" +
                            "如果是纯文字图，把文字内容也写进 desc。不要输出 JSON 之外的内容。"
                    },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = "这张图是什么？" },
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } }
                        }
                    }
                },
                ["max_tokens"] = 200,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null, null);
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return ParseStickerDescription(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包识别失败：{ex.Message}");
            return (null, null, null);
        }
        finally
        {
            _stickerGate.Release();
        }
    }

    /// <summary>解析表情包编目结果（容忍代码块围栏与多余文字）。</summary>
    internal static (string? Desc, List<string>? Tags, bool? IsSticker) ParseStickerDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null, null);
        }

        var text = StripCodeFence(raw.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            var plain = text.Trim();
            return plain.Length is > 0 and <= 40 ? (plain, null, null) : (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var desc = root.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim() : null;
            List<string>? tags = null;
            if (root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                tags = t.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim())
                    .Where(x => x.Length > 0)
                    .Take(8)
                    .ToList();
            }

            // 缺字段时不默认 true：宁可多留一张待定，也不要让截图混进来
            bool? isSticker = null;
            if (root.TryGetProperty("sticker", out var s))
            {
                isSticker = s.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(s.GetString(), out var b) => b,
                    _ => null
                };
            }

            return (desc, tags, isSticker);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>按对话欲望生成“发言适合度”评分指令。</summary>
    private string BuildSuitabilityInstruction()
    {
        var desire = AiDesire switch
        {
            >= 80 => "你非常健谈主动：主动参与群聊话题、接话、活跃气氛，除非话题与你完全无关。",
            >= 50 => "你比较健谈：话题与你相关、被提到或你感兴趣时积极发言，不强行插话。",
            >= 20 => "你较为克制：主要在被呼叫或与你直接相关时发言，群友闲聊时保持沉默。",
            _ => "你极少主动发言：仅在被明确 @ 或对方直接对你说话时回复。"
        };

        return "\n\n[发言决策]\n" +
               "收到一条新消息后，结合上述上下文与人设判断：本轮是否应该由你发言。" +
               "先评估“发言适合度”（0-100 的整数，衡量这条消息是否值得你回应、你是否能自然接上话），" +
               "然后写出你的回复。请严格只输出一行 JSON，形如：{\"suitability\": 80, \"reply\": \"你的回复内容\"}。" +
               "" + desire +
               " 如果你决定发言（suitability 不低于 " + SuitabilityThreshold + "），reply 必须填写实际内容；" +
               "如果你决定沉默，reply 填空字符串（\"\"）。" +
               "注意：只要 reply 非空，程序就会把你的话发出去——所以不确定时宁可不发，reply 留空。";
    }

    /// <summary>
    /// 把一堆历史发言压缩成一段人物画像（长期记忆）。
    /// 与聊天请求共用同一个模型与端点，但走独立的并发闸门。
    /// </summary>
    public async Task<string?> SummarizePersonaAsync(
        string name,
        string? existingSummary,
        IReadOnlyList<string> messages,
        int maxChars,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || messages.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("你在为一个 QQ 群聊机器人维护“人物档案”（长期记忆）。\n");
        sb.Append("把下面这些发言压缩成一段人物画像，供机器人以后认人用。\n");
        sb.Append("画像需覆盖：身份/职业线索、性格与说话风格、常聊的话题、与其他群友的关系、值得记住的事实。\n");
        sb.Append("要求：用第三人称陈述句；只保留稳定、有信息量的内容，忽略寒暄与一次性琐事；\n");
        sb.Append($"不要逐条罗列，不要分点，不要客服腔；总长不超过 {maxChars} 字；只输出画像正文。\n");

        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.Append("\n[已有画像]（在此基础上融合新信息，不要推翻已证实的稳定事实）\n");
            sb.Append(existingSummary.Trim()).Append('\n');
        }

        sb.Append($"\n[新的发言]（{name}）\n");
        foreach (var m in messages)
        {
            sb.Append("- ").Append(m).Append('\n');
        }

        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = sb.ToString() },
                new JsonObject { ["role"] = "user", ["content"] = $"请输出 {name} 的人物画像。" }
            },
            ["max_tokens"] = Math.Clamp(maxChars * 3, 200, 2000),
            ["temperature"] = 0.3
        };

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        using var response = await Http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"画像摘要返回 {(int)response.StatusCode}：{Truncate(detail, 200)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var contentNode = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
        var text = contentNode.ValueKind == JsonValueKind.Array
            ? string.Concat(contentNode.EnumerateArray().Select(s => s.GetProperty("text").GetString()))
            : contentNode.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim().Trim('"', '“', '”');
        return SafeTruncate(trimmed, maxChars);
    }

    /// <summary>
    /// 按字符数截断，但绝不切开代理对（emoji 占两个 char）。
    /// 直接 `s[..max]` 会把 emoji 切一半 → 输出乱码。
    /// </summary>
    private static string SafeTruncate(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--; // 回退一格，不要切开代理对
        }

        return text[..cut];
    }

    /// <summary>
    /// 让模型“亲耳听”：把低码率音频（截前 N 秒 / N KB）交给**独立的音频识别模型**，
    /// 让它客观描述听到的内容（曲风/编配/人声/情绪/节奏感）。
    ///
    /// 为什么要单独配一个模型：主模型（gemini-3.8-flash-high）实测收不到音频 —— 代理会把音频静默丢掉，
    /// 它只能看到文字，于是“听歌”就只剩手写 DSP 的客观数字（听不出曲风情绪）。
    /// 实测同代理上 gemini-3.7-flash-high / gemini-3-flash / gemini-3.1-pro-low 真能听到（用 440Hz 蜂鸣验证过）。
    ///
    /// 返回 null 表示“这次没听到”（未配置/模型不支持/请求失败）—— 调用方降级回只给 DSP 实测数据。
    /// </summary>
    public async Task<string?> DescribeAudioAsync(byte[] audio, string format, string title, string? artist, CancellationToken ct)
    {
        var model = _settings.MusicUnderstandModel?.Trim();
        if (string.IsNullOrWhiteSpace(model) || !_settings.MusicSendAudioToModel || audio.Length == 0)
        {
            return null;
        }

        var capKb = Math.Clamp(_settings.MusicAudioToModelMaxKb, 128, 4096);
        var bytes = audio.Length > capKb * 1024 ? audio[..(capKb * 1024)] : audio;

        var text = "这是一首歌的片段（低码率、可能被截断）。" +
            $"歌名：{title}" + (string.IsNullOrWhiteSpace(artist) ? "。" : $"，歌手：{artist}。") +
            "请只说你**听到的**：曲风、编配与主要乐器、人声特点（音色/唱法）、情绪与氛围、节奏快慢与律动、" +
            "如果能听清歌词就引用一两句。听不清/听不到就直接说听不到，不要根据歌名猜、不要编造。用 3-5 句话。";

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = text },
                    new JsonObject
                    {
                        ["type"] = "input_audio",
                        ["input_audio"] = new JsonObject
                        {
                            ["data"] = Convert.ToBase64String(bytes),
                            ["format"] = format
                        }
                    }
                }
            }
        };

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = 512,
            ["temperature"] = 0.3
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
            using var response = await Http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Services.FileLog.Warn("Agent", $"[Music] 音频识别模型 {model} 返回 {(int)response.StatusCode}：{Truncate(body, 160)}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // 模型说它听不到（模型/链路不支持音频）→ 当作“没听到”，别把这句话当听感塞进上下文
            if (content.Contains("听不到", StringComparison.Ordinal) || content.Contains("无法接收", StringComparison.Ordinal) ||
                content.Contains("无法播放", StringComparison.Ordinal) || content.Contains("没有声音", StringComparison.Ordinal))
            {
                Services.FileLog.Warn("Agent", $"[Music] 音频识别模型 {model} 声称听不到音频（该模型/链路可能不支持 input_audio）");
                return null;
            }

            Services.FileLog.Write("Agent", $"[Music] {model} 听感：{Truncate(content, 200)}");
            return content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Services.FileLog.Warn("Agent", $"[Music] 音频识别请求失败（{model}）: {ex.Message}");
            return null;
        }
    }

    private string BuildUrl()
    {
        var url = _settings.ModelBaseUrl.Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    private const string SystemPrompt =
        "你是运行在桌面 QQ 客户端里的聊天机器人「QQ Chat Agent」。\n" +
        "回复规则：\n" +
        "- 私聊与群聊中，先根据上下文判断当前对话是否与你相关（是否在对你说话、询问你、需要你参与）；" +
        "相关则自然简洁地回复；与你无关（例如群友之间与你无关的闲聊）则只输出空内容，不要回复。\n" +
        "- 不使用任何固定关键词作为回复条件，一切凭上下文理解。\n" +
        "- 上下文中每条对方消息形如 {发送者}{内容--时间}，发送者可能是真人、群成员或角色。\n" +
        "- 若上下文中出现角色卡片（描述某角色的名字、性格、背景、说话风格的设定），" +
        "且对话正在与该角色互动，请以该角色卡片中的身份、性格与说话风格进行思考与回复。\n" +
        "- 像真人群友一样说话：口语化、简短、有来有回，多用群聊常见的语气与口头禅，" +
        "不要像客服/助手一样客套，不要用“首先其次最后”，不要过分礼貌，可以带点调侃。\n" +
        "- 回复长度按场景动态把握：轻松闲聊一句 15 字以内；认真讨论/专业问题 30 字左右；" +
        "需要详细说明时也不要超过 60 字，宁可分几次说。\n" +
        "- 使用中文。";

    /// <summary>时间短格式：当天 HH:mm，跨天 MM-dd HH:mm。</summary>
    private static string FormatTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("MM-dd HH:mm");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>撤回标记：内容是保留的，但必须让模型一眼看出“这条已经收回去了”。</summary>
    private const string RecallMark = "[已撤回] ";

    /// <summary>兜底聚合：从最近 40 条消息统计每个发送者的发言（当未传入外部档案时）。</summary>
    private static string[] BuildParticipantProfiles(IReadOnlyList<ChatMessage> context)
    {
        var stats = new Dictionary<string, (int Count, string Last)>();
        foreach (var m in context.TakeLast(40))
        {
            if (m.Role != MessageRole.Peer || string.IsNullOrWhiteSpace(m.SenderName))
            {
                continue;
            }

            // 撤回了的也一起统计，但标明“已撤回”（这里的文本会进提示词）
            var text = m.Recalled ? RecallMark + m.Text : m.Text;
            stats[m.SenderName] = stats.TryGetValue(m.SenderName, out var s)
                ? (s.Count + 1, text)
                : (1, text);
        }

        return stats.Select(kv => $"{kv.Key}：发言 {kv.Value.Count} 次，最近说“{Truncate(kv.Value.Last, 50)}”").ToArray();
    }
}
/// <summary>提示词里给模型挑的表情包候选（只传 id + 说明，不传图）。</summary>
public readonly record struct StickerChoice(string Id, string Description);

/// <summary>模型一次生成的结构化结果。</summary>
/// <param name="Suitability">模型自评的发言适合度（0-100）；null = 模型未按 JSON 格式输出。</param>
/// <param name="Reply">要发出去的内容；null/空 = 不说话。</param>
/// <param name="RawText">模型原始输出（排障用）。</param>
/// <param name="StickerId">模型挑中的表情包 id；null = 不发图。</param>
/// <param name="ReplyToMessageId">模型自己指认的“我在回哪条消息”（对应提示里的 (#id)）；null = 没指定。</param>
/// <param name="PokeTargetId">模型想戳的人的 QQ 号（对应提示里的 poke 字段）；null = 不戳。</param>
/// <param name="Mood">模型顺手写的“我现在的心情”（≤ 24 字）；null = 没写。</param>
/// <param name="Listen">模型想“听一听”的歌名/歌手（机器人会去搜索并分析波形）；null = 不想听。</param>
/// <param name="ShareSong">模型想分享给群里的歌（机器人搜到后发一张网易云卡片）；null = 不分享。</param>
/// <param name="Speak">模型想“用语音说”的句子（机器人合成语音发出去）；null = 不发语音。</param>
/// <param name="Search">模型想上网查的问题（机器人真去搜，下一轮把结果给它）；null = 不搜。</param>
/// <param name="Read">模型想读的网页地址（机器人抓正文，下一轮把正文给它）；null = 不读。</param>
public readonly record struct CompletionResult(int? Suitability, string? Reply, string? RawText, string? StickerId = null, long? ReplyToMessageId = null, long? PokeTargetId = null, string? Mood = null, string? Listen = null, string? ShareSong = null, string? Speak = null, string? Search = null, string? Read = null);

/// <summary>图片下载器：把图片 URL 下载并转成 base64 data URL（供多模态模型识图），
/// 也给表情包库提供原始字节。</summary>
internal sealed class ImageDownloader
{
    private static readonly HttpClient Client = new()
    {
        // 表情包动图可以大一点；超时仍要短，不能让收消息链路卡住
        Timeout = TimeSpan.FromSeconds(8)
    };

    private const int MaxImageBytes = 6 * 1024 * 1024;

    /// <summary>
    /// 是否允许从内网/回环地址下载（QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1）。
    /// 默认关闭：图片 URL 来自 QQ 事件，属不可信输入，SSRF 防护必须默认生效。
    /// 只在“自建 NapCat 用内网地址、或集成测试用本地图片服务器”时手动打开，
    /// 官方镜像与远程部署都不应该打开它。
    /// </summary>
    public bool AllowPrivateHosts { get; init; } =
        Environment.GetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS") == "1";

    /// <summary>
    /// 下载图片字节（失败返回 null）。供表情包库保存原图使用。
    /// </summary>
    public async Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!IsSafeImageUrl(url, out var uri))
            {
                Services.FileLog.Write("Vision", $"图片地址被拒（SSRF 防护）: {Truncate(url, 120)}");
                return null;
            }

            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Services.FileLog.Write("Vision", $"图片下载失败 {(int)response.StatusCode}: {Truncate(url, 120)}");
                return null;
            }

            // 有 Content-Length 时先拦，避免把超大响应当进内存
            if (response.Content.Headers.ContentLength is long declared &&
                (declared <= 0 || declared > MaxImageBytes))
            {
                Services.FileLog.Write("Vision", $"图片声明大小异常 {declared}: {Truncate(url, 120)}");
                return null;
            }

            var bytes = await ReadCappedAsync(response.Content, MaxImageBytes, ct);
            if (bytes is null || bytes.Length == 0)
            {
                Services.FileLog.Write("Vision", $"图片超限或为空: {Truncate(url, 120)}");
                return null;
            }

            var mime = DetectMime(bytes);
            var ext = mime switch
            {
                "image/jpeg" => "jpg",
                "image/gif" => "gif",
                "image/webp" => "webp",
                _ => "png"
            };
            return (bytes, mime, ext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.FileLog.Write("Vision", $"图片下载异常: {ex.Message} | {Truncate(url, 120)}");
            return null;
        }
    }

    /// <summary>
    /// 下载图片并转为 data:image/...;base64,xxx；失败返回 null。
    /// 图片 URL 来自 QQ 事件，属**不可信输入** → 先做 SSRF 防护再下载。
    /// </summary>
    public async Task<string?> DownloadAsDataUrl(string url, CancellationToken ct)
    {
        var downloaded = await DownloadBytesAsync(url, ct);
        return downloaded is { } d ? $"data:{d.Mime};base64,{Convert.ToBase64String(d.Data)}" : null;
    }

    /// <summary>
    /// SSRF 防护：只允许公网 http(s) 图片地址。
    /// 拦的是这类被构造出来的地址： http://127.0.0.1:6099/...（NapCat WebUI 自身）、
    /// http://169.254.169.254/...（云元数据）、http://napcat:3001/...（容器内服务）。
    /// 局限：不做 DNS 解析，因此无法拦截“解析到内网 IP 的公网域名”（需要出站防火墙）。
    /// </summary>
    private bool IsSafeImageUrl(string? url, out Uri uri)
    {
        uri = null!;

        // 显式放行（QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1，仅供自建/测试）
        if (AllowPrivateHosts)
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url.Trim(), UriKind.Absolute, out var permissive) &&
                (permissive.Scheme == Uri.UriSchemeHttp || permissive.Scheme == Uri.UriSchemeHttps))
            {
                uri = permissive;
                return true;
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = parsed.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        // 单标签主机名（localhost / napcat / redis …）一律拒绝：
        // 真实图片域名必定带点（gchat.qpic.cn 等）
        if (!host.Contains('.'))
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // IP 字面量：拒绝回环 / 私有 / 链路本地 / 未指定
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
                ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            {
                return false;
            }

            if (IsPrivateV4(ip))
            {
                return false;
            }

            // IPv4-mapped IPv6（::ffff:127.0.0.1）
            if (ip.IsIPv4MappedToIPv6 && IsPrivateV4(ip.MapToIPv4()))
            {
                return false;
            }
        }

        uri = parsed;
        return true;
    }

    private static bool IsPrivateV4(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = ip.GetAddressBytes();
        return b[0] == 10                                 // 10.0.0.0/8
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168)               // 192.168.0.0/16
            || (b[0] == 169 && b[1] == 254)               // 169.254.0.0/16 链路本地（云元数据）
            || b[0] == 127                                // 127.0.0.0/8
            || b[0] == 0;                                 // 0.0.0.0/8
    }

    /// <summary>流式读取并限制总字节数：超过上限立即返回 null，不会把超大响应全量读进内存。</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int cap, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();

        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > cap)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string DetectMime(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/png";
    }
}
