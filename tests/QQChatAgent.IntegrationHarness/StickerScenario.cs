using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S18 表情包：自动收集 → 自动识别 → 按语境发出去 → 超限淘汰 → 机器人自己巡检删图。
///
/// 这条链路很长（收图 → 下载 → 哈希去重 → 写盘 → 模型编目 → 检索候选 → 模型选图 → base64 发图 → 巡检删图），
/// 每个环节都可能悄悄坏掉而不报错：比如收集失败只是“库里一直空着”，
/// 编目失败只是“候选永远是那几张没说明的”，发图失败只是“模型选了图但群里什么都没出现”。
/// 所以这里把关键节点都钉住。
/// </summary>
public static partial class Program
{
    private static async Task RunStickerScenarioAsync()
    {
        Section("S18 表情包：自动收集 / 自动识别 / 按语境发送 / 上限淘汰 / 自巡检");

        const int openAiPort = 17823;
        const int botWsPort = 13031;
        const int panelPort = 18093;
        const int imagePort = 18094;
        const long groupId = 66681;
        var dataDir = NewDataDir("s18");
        // 表情包库：索引在 SQLite（stickers 表），图片本体住在数据根目录下的 stickers/
        var stickerDir = Path.Combine(dataDir, "stickers");


        using var images = new MockImageHost(imagePort);
        images.Start();

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
            ["QQCHAT_STICKER_MAX"] = "3",
            ["QQCHAT_STICKER_CANDIDATES"] = "6",
            ["QQCHAT_STICKER_CURATE_INTERVAL"] = "0", // 巡检由测试手动触发，避免跟断言抢时序
            ["QQCHAT_STICKER_COOLDOWN"] = "120",      // 频率门：刚发过的同张不再重发
            // 测试用的图片服务器在 127.0.0.1：默认的 SSRF 防护会拦它（生产行为不变）
            ["QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS"] = "1"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        // 先把生效的配置看清楚：这是全新数据目录，环境变量应当作为“种子”生效
        var (settingsStatus, settingsBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 测试用的表情包配置已生效（上限 3 / 候选 6；环境变量只在首次部署当种子）",
            settingsStatus == 200 && settingsBody.Contains("\"stickerLibraryMax\":3") &&
            settingsBody.Contains("\"stickerCandidates\":6") && settingsBody.Contains("\"enableStickers\":true") &&
            settingsBody.Contains("\"stickerCooldownSeconds\":120"),
            settingsBody.Length > 260 ? settingsBody[^260..] : settingsBody);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- 1) 群友发了图 → 自动收进库 ----
        // 注意脚本回复是 FIFO 的：这条图消息自己也会触发一次模型请求，先给它一条“沉默”，
        // 否则后面给“表情包回复”准备的那条会被它吃掉。
        openAi.EnqueueReply("""{"suitability": 5, "reply": ""}""");
        await protocol.SendGroupMessageAsync(groupId, 30003, "小明", "看看这个", 9201, mentionBot: true,
            imageUrl: images.Url(1), ct: cts.Token);

        var id1 = MockImageHost.StickerId(1);
        var collected = await WaitUntilAsync(
            () => ReadIndex(dataDir).Contains(id1),
            TimeSpan.FromSeconds(25));
        Check("★ 群友发的图被自动收进表情包库", collected, $"期望 id={id1}，索引：{Truncate(ReadIndex(dataDir), 300)}");
        Check("同一张图只存一份（按内容哈希去重）", Directory.Exists(stickerDir) && Directory.GetFiles(stickerDir, $"{id1}-*").Length == 1,
            string.Join(",", Directory.Exists(stickerDir) ? Directory.GetFiles(stickerDir, $"{id1}-*").Select(Path.GetFileName) : Array.Empty<string>()));

        // ---- 2) 自动识别（说明 + 关键词）----
        var described = await WaitUntilAsync(
            () => ReadIndex(dataDir).Contains("憋笑失败的猫"),
            TimeSpan.FromSeconds(30));
        Check("★ 机器人用模型给新图生成说明与关键词（没有它就没法按语境检索）", described, Truncate(ReadIndex(dataDir), 300));
        Check("确实发起了编目请求（带图片的多模态请求）", openAi.StickerDescribeRequests > 0, $"次数 {openAi.StickerDescribeRequests}");

        // ---- 3) 按语境挑候选给模型 ----
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "哈哈", "sticker": "{{id1}}"}""");
        await protocol.SendGroupMessageAsync(groupId, 30004, "老王", "刚才那个太好笑了", 9202, mentionBot: true, ct: cts.Token);
        await Task.Delay(3000);

        // 注意：必须在这条请求发出**之后**再找（之前把这一行放在了发消息之前，永远为空）
        var promptWithStickers = openAi.Requests.LastOrDefault(r => SystemText(r).Contains("[可用表情包]"));
        Check("★ 提示词里带上了按语境挑出来的表情包候选（不是整库）", promptWithStickers is not null,
            string.Join("\n      ", openAi.Requests.Select((r, i) =>
                $"[{i}] marker={SystemText(r).Contains("[可用表情包]")} tail={Tail(SystemText(r), 100)}")));
        Check("候选行里带的是 id + 说明/关键词", promptWithStickers is not null && SystemText(promptWithStickers).Contains(id1),
            promptWithStickers is null ? "(没有带表情包段的请求)" : Truncate(SystemText(promptWithStickers), 400));

        // ---- 4) 模型选了图 → 真的把图发出去 ----
        var imageSent = await WaitUntilAsync(
            () => protocol.ActionsReceived
                .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
                .SelectMany(ImageSegments)
                .Any(f => f.StartsWith("base64://", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(20));
        var sentSegments = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .SelectMany(ImageSegments)
            .ToList();
        Check("★ 模型挑中的表情包真的以图片段发到群里（base64）", imageSent,
            string.Join(",", sentSegments.Select(s => Truncate(s, 40))));
        Check("文字与表情包都能发出来（这条是“文字 + 图”）",
            protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg" && MessageText(a).Contains("哈哈")),
            string.Join(" | ", protocol.ActionsReceived.Where(a => a["action"]?.GetValue<string>() == "send_group_msg").Select(MessageText)));
        Check("用过一次会记账（下次淘汰时更安全）", DbProbe.Count(dataDir, "SELECT COUNT(1) FROM stickers WHERE id = $id AND uses = 1", ("$id", id1)) == 1, Truncate(ReadIndex(dataDir), 300));

        // ---- 6) 审核闸门：模型说“这不是表情包”→ 立即丢掉（线上把聊天截图当表情包收了）----
        // 先把现有图全部识别完，否则下面那条“审核失败”的脚本会被别的图吃掉。
        await WaitUntilAsync(
            () =>
            {
                var (status, body) = HttpGetAsync($"http://127.0.0.1:{panelPort}/api/stickers").GetAwaiter().GetResult();
                return status == 200 && body.Contains("\"pendingDescribe\":0") && body.Contains("\"described\":" + CountStickers(dataDir));
            },
            TimeSpan.FromSeconds(40));

        openAi.EnqueueStickerVerdict("""{"sticker": false, "desc": "聊天截图，文字写着确认是旧版本", "tags": ["截图", "聊天记录"]}""");
        var beforeAudit = CountStickers(dataDir);
        await protocol.SendGroupMessageAsync(groupId, 30007, "小明", "看这个截图", 9301, mentionBot: true,
            imageUrl: images.Url(9), ct: cts.Token);
        var auditId = MockImageHost.StickerId(9);

        // 真正的信号是“机器人自己说它把这张丢了”——只看“库里没有它”会漏掉
        // “压根没收进来”这种情况（那会让断言永远绿，反向验证也不红）。
        var rejected = await WaitUntilAsync(
            () => bot.OutputLines.Any(l => l.Contains("审核不通过") && l.Contains(auditId)),
            TimeSpan.FromSeconds(45));
        Check("★ 审核不通过的图会被机器人主动丢弃（日志里说清了原因）", rejected,
            $"截图 id={auditId}；最近的表情包日志：{Truncate(string.Join(" ｜ ", bot.OutputLines.Where(l => l.Contains("表情包")).TakeLast(4)), 320)}");
        Check("审核不通过的图不会留在库里",
            !ReadIndex(dataDir).Contains(auditId),
            $"之前 {beforeAudit} 张，现在 {CountStickers(dataDir)} 张：{Truncate(ReadIndex(dataDir), 240)}");

        // ---- 5) 存储上限：收 5 张，上限 3 ----
        for (var n = 2; n <= 5; n++)
        {
            // 每条图消息各自会触发一次回复请求：给它们沉默脚本（不干扰上面的断言）
            openAi.EnqueueReply("""{"suitability": 5, "reply": ""}""");
            await protocol.SendGroupMessageAsync(groupId, 30003, "小明", $"再来一张 {n}", 9200 + n, mentionBot: true,
                imageUrl: images.Url(n), ct: cts.Token);
            await Task.Delay(700);
        }

        var trimmed = await WaitUntilAsync(
            () => CountStickers(dataDir) == 3,
            TimeSpan.FromSeconds(30));
        Check("★ 存储上限可配置：收 5 张后库里只剩 3 张（QQCHAT_STICKER_MAX=3）", trimmed,
            $"当前 {CountStickers(dataDir)} 张：{ReadIndex(dataDir)}");
        Check("★ 淘汰的是“用得少 + 最久没用”的（刚用过那张被留下）",
            ReadIndex(dataDir).Contains(id1), Truncate(ReadIndex(dataDir), 400));
        Check("淘汰后磁盘上的图片文件也删了（不留垃圾）",
            Directory.GetFiles(stickerDir, "*.png").Length == 3,
            string.Join(",", Directory.GetFiles(stickerDir).Select(Path.GetFileName)));

        // ---- 6) 机器人自己巡检：决定删一张 ----
        var victim = StickerIds(dataDir).FirstOrDefault(id => id != id1);
        Check("库里还有可被巡检删掉的图", victim is not null, Truncate(ReadIndex(dataDir), 300));
        openAi.EnqueueCuration($$"""{"delete": ["{{victim}}"], "reason": "说明模糊且从没用过"}""");

        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            var res = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/stickers/curate", null, cts.Token);
            Check("手动触发巡检接口可用", res.IsSuccessStatusCode, $"HTTP {(int)res.StatusCode}");
        }

        var deleted = await WaitUntilAsync(
            () => !ReadIndex(dataDir).Contains(victim!),
            TimeSpan.FromSeconds(30));
        Check("★ 机器人自巡检真的删掉了模型点名的图", deleted,
            $"victim={victim}；{Truncate(ReadIndex(dataDir), 300)}");
        Check("它不会删刚用过的那张（代码侧兜底，不听模型的）", ReadIndex(dataDir).Contains(id1),
            Truncate(ReadIndex(dataDir), 300));
        Check("巡检请求确实发生过", openAi.StickerCurateRequests > 0, $"次数 {openAi.StickerCurateRequests}");
        Check("巡检结束后张数仍然 ≤ 上限", CountStickers(dataDir) <= 3, $"当前 {CountStickers(dataDir)}");

        // ---- 7) 频率门：库里就几张时不能每句都挂同一张（群友直接开愤）----
        openAi.EnqueueReply($$"""{"suitability": 99, "reply": "又一句", "sticker": "{{id1}}"}""");
        var imagesBefore = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .SelectMany(ImageSegments)
            .Count(f => f.StartsWith("base64://", StringComparison.Ordinal));

        await protocol.SendGroupMessageAsync(groupId, 30008, "老王", "再来一句", 9302, mentionBot: true, ct: cts.Token);
        await Task.Delay(4000);

        var imagesAfter = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .SelectMany(ImageSegments)
            .Count(f => f.StartsWith("base64://", StringComparison.Ordinal));
        Check("★ 频率门：刚发过同一张 → 这次不再重发（只发文字）",
            imagesAfter == imagesBefore,
            $"表情包条数 {imagesBefore} → {imagesAfter}；日志：{Truncate(string.Join(" ｜ ", bot.OutputLines.Where(l => l.Contains("发表情包") || l.Contains("表情包 #"))), 400)}");
        Check("文字部分照常发出去（频率门只拦图，不拦话）",
            protocol.ActionsReceived.Any(a => a["action"]?.GetValue<string>() == "send_group_msg" && MessageText(a).Contains("又一句")),
            string.Join(" | ", protocol.ActionsReceived.Where(a => a["action"]?.GetValue<string>() == "send_group_msg").Select(MessageText).TakeLast(4)));

        // 这条必须放在**又一次模型请求之后**才算数：
        // 否则看的是“加审核图之前”的旧提示词，把候选过滤退回去也不会红。
        Check("★ 审核不通过的图不会被当作候选给模型（提示词里没有它）",
            !openAi.Requests.Any(r => SystemText(r).Contains($"#{auditId}")),
            $"提示词里出现了被拒的截图 id {auditId}");

        await bot.StopAsync();
    }

    private static string Tail(string text, int max)
        => string.IsNullOrEmpty(text) ? string.Empty
            : text.Length <= max ? text : "…" + text[^max..];

    /// <summary>把库里的表情包索引拼成一段可读文本（断言用 Contains）。</summary>
    private static string ReadIndex(string dataDir)
    {
        try
        {
            return DbProbe.Dump(dataDir, "SELECT id, description, uses, described FROM stickers ORDER BY added_unix");
        }
        catch (Exception ex)
        {
            return "(读取失败: " + ex.Message + ")";
        }
    }

    private static int CountStickers(string dataDir)
        => (int)DbProbe.TableCount(dataDir, "stickers");

    private static List<string> StickerIds(string dataDir)
        => DbProbe.Dump(dataDir, "SELECT id FROM stickers ORDER BY added_unix")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>取一条动作里所有 image 段的 file/base64 值。</summary>
    private static IEnumerable<string> ImageSegments(JsonObject? action)
    {
        if (action?["params"]?["message"] is not JsonArray segments)
        {
            yield break;
        }

        foreach (var seg in segments)
        {
            if (seg?["type"]?.GetValue<string>() != "image")
            {
                continue;
            }

            var file = seg["data"]?["file"]?.GetValue<string>() ?? seg["data"]?["url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(file))
            {
                yield return file!;
            }
        }
    }
}
