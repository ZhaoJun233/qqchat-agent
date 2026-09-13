using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S22 听音乐：群里分享一首歌 → 查网易云歌词 → 下一份低码率音频 → 分析波形 → 把**实测事实**交给模型。
///
/// 这条链路每一环都可能“静默降级”：认不出分享就只是没有反应；音源挂了就只是没波形；
/// 分析错了就只是模型开始胡说（比如此刻说“很燃”）。所以断言分三层：
///   ① 识别层：music 段 / 文本链接都要认得出来；
///   ② 数据层：假音源真的被下载了、假接口真的被查了，且提示词里带的是实测数字（BPM 落在 110–130）；
///   ③ 降级层：拿不到音源时明说“没拿到音源”，仍然让模型说话，而不是整条流程哑掉。
/// </summary>
public static partial class Program
{
    private static async Task RunMusicScenarioAsync()
    {
        Section("S22 听音乐：识别分享 → 网易云歌词 → 低码率音源 → 波形分析 → 交给模型");

        const int openAiPort = 17827;
        const int botWsPort = 13034;
        const int panelPort = 18097;
        const int musicPort = 18098;
        const long groupId = 66683;
        const long songWithAudio = 999001;
        const long songWithoutAudio = 999002;

        var dataDir = NewDataDir("s22");
        using var music = new MockMusicHost(musicPort);
        music.Start();

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
            ["QQCHAT_NETEASE_BASE_URL"] = music.BaseUrl,
            ["QQCHAT_MUSIC_SOURCES"] = music.SourceTemplate(songWithAudio),
            ["QQCHAT_MUSIC_MAX_MB"] = "8",
            ["QQCHAT_MUSIC_ANALYSIS_SECONDS"] = "60"
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        var (status, body) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 音乐相关配置生效（开关默认开 / 音源模板 / 码率 / 分析上限）",
            status == 200 && body.Contains("\"enableMusic\":true") &&
            body.Contains("\"musicMaxAnalysisSeconds\":60") && body.Contains("audio/{id}.mp3"),
            body.Length > 300 ? body[^300..] : body);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        // ---- 1) 群友分享一首能拿到音源的歌 ----
        openAi.ClearRequests();
        // 现在听歌是两段：① 音频识别模型的“听感”，② 带着听感+波形数据回复群里。
        // 脚本回复是 FIFO 的，顺序不能反。
        openAi.EnqueueReply("我听到的是一首中速流行曲：钢琴前奏、弦乐铺底，女声温润。");
        openAi.EnqueueReply("""{"suitability": 92, "reply": "这首副歌一进来就亮了。"}""");
        await protocol.SendGroupMusicAsync(groupId, 30011, "小美", songWithAudio, 9301, ct: cts.Token);

        var noteRequest = await WaitForRequestAsync(openAi, r => r.Contains("波形实测"), TimeSpan.FromSeconds(60));
        if (noteRequest is null)
        {
            foreach (var row in openAi.Requests.Select((r, i) => (i, text: openAi.DescribeRequest(i))))
            {
                Console.WriteLine($"      [调试] 请求{row.i}: {row.text.Length} 字符, 含水印={row.text.Contains("波形实测")}, " +
                    $"含999001={row.text.Contains("999001")}, 含小美={row.text.Contains("小美")}, 含分享了一首歌={row.text.Contains("分享了一首歌")}");
            }

            Console.WriteLine("      [调试] 第一条请求尾部：" +
                (openAi.Requests.Count > 0 ? openAi.DescribeRequest(0)[^600..].Replace('\n', ' ') : "(无)"));
        }

        Check("★ 等待“听完再说”：波形分析完成后模型才被叫来（而不是先对着歌名瞎聊）", noteRequest is not null);

        var note = noteRequest ?? string.Empty;
        Check("★ 提示词带上了歌名与歌手", note.Contains(music.Title) && note.Contains(music.Artist));

        // 音频识别模型那段：请求里必须真的带了音频（input_audio + base64），听感必须进了上下文
        var audioReq = openAi.Requests.Select((_, i) => openAi.DescribeRequest(i))
            .FirstOrDefault(r => r.Contains("input_audio"));
        Check("★ 音频真的以 input_audio 交给音频识别模型听（带 base64）",
            audioReq is not null, audioReq is null ? "(没有带音频的请求)" : "已带音频");
        Check("★ 模型的听感进了提示词（不是只有分贝数）",
            note.Contains("模型听感") && note.Contains("中速流行曲"));
        Check("★ 听感与波形实测并列给出（定性 + 定量互相校验）",
            note.Contains("模型听感") && note.Contains("波形实测") && note.Contains("BPM"));
        Check("★ 提示词带上了时长（来自网易云详情接口）", note.Contains("时长 0:45"));
        Check("★ 假音源真的被下载（不是只查了接口）", music.AudioHits >= 1, $"audioHits={music.AudioHits}");
        Check("★ 网易云详情/歌词接口都被访问过", music.ApiHits >= 2, $"apiHits={music.ApiHits}");

        var bpm = ExtractBpm(note);
        Check("★ 波形分析测出的速度落在合理区间（合成音频是 120 BPM）",
            bpm is >= 110 and <= 130, $"实测 BPM={bpm?.ToString() ?? "(没测到)"}");
        Check("★ 段落轮廓反映了真实结构（弱 → 强 → 中）",
            note.Contains("段落分布") && note.Contains("弱") && note.Contains("强"), Snippet(note, "段落分布"));
        Check("★ 动态范围与响度是实测值", note.Contains("dBFS") && note.Contains("动态"));

        Check("★ 歌词进了提示词，且时间轴被剥成纯文本",
            note.Contains("测试歌词第一句") && !note.Contains("[00:28.950]"));
        Check("★ 提示词里没有让模型“看着歌名编”的成分（明确要求以实测为准）",
            note.Contains("以上面的实测数据为准") || note.Contains("实测数据为准"));

        var sends = await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(30));
        Check("★ 机器人用模型的话回了群", sends.Count >= 1 && MessageText(sends[0]).Contains("副歌"));
        Check("★ 这次发言不挂引用（音乐分析触发的，没有触发消息）",
            sends.Count >= 1 && QuotedMessageId(sends[0]) is null);

        var ledger = Path.Combine(dataDir, "data", "music", "listened.json");
        var ledgerHasSong = false;
        if (File.Exists(ledger))
        {
            // 注意别直接对文本 Contains：JSON 里中文是 \uXXXX 转义的
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ledger));
            ledgerHasSong = doc.RootElement.EnumerateArray()
                .Any(e => e.TryGetProperty("Title", out var t) && t.GetString() == music.Title);
        }

        Check("★ “听过的歌”落了台账（下次再分享同一首就不重复下载解码）",
            ledgerHasSong, File.Exists(ledger) ? "已生成" : "没生成");

        // ---- 2) 拿不到音源的歌：只给歌词，且如实说明 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "这首我只有歌词，但写得真好。"}""");
        await protocol.SendGroupMessageAsync(groupId, 30012, "小明",
            $"https://music.163.com/song?id={songWithoutAudio}", 9302, ct: cts.Token);

        var fallback = await WaitForRequestAsync(openAi, r => r.Contains("没能拿到音源"), TimeSpan.FromSeconds(60));
        Check("★ 音源 404 时降级成“只读歌词”，并在提示词里如实说明",
            fallback is not null, fallback is null ? "(没看到降级说明)" : Snippet(fallback, "没能拿到音源"));
        Check("★ 降级后仍然带上了歌词与歌名",
            fallback is not null && fallback.Contains("测试歌词第一句") && fallback.Contains(music.Title));
        Check("★ 文本里的网易云链接也能识别成音乐分享（不用 music 段也能认出来）",
            fallback is not null && fallback.Contains("分享了一首歌"));

        // ---- 3) 关掉开关就完全不碰音乐（也不下载）----
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var saveRes = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent("""{"enableMusic":false}""", Encoding.UTF8, "application/json"), cts.Token);
        Check("★ 面板可以关掉听音乐", saveRes.IsSuccessStatusCode, $"HTTP {(int)saveRes.StatusCode}");

        var audioBefore = music.AudioHits;
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 80, "reply": "好。"}""");
        await protocol.SendGroupMusicAsync(groupId, 30013, "小美", songWithAudio, 9303, ct: cts.Token);
        var plain = await WaitForRequestAsync(openAi, _ => true, TimeSpan.FromSeconds(30));
        Check("★ 关掉后不再分析音乐（提示词里没有波形/歌词，也没有再下载音频）",
            plain is not null && !plain.Contains("波形实测") && music.AudioHits == audioBefore,
            $"audioHits {audioBefore} → {music.AudioHits}");

        // ---- 4) 群里说“去听一下 X”：模型填 listen → 机器人真去搜歌、听、再回来聊 ----
        // （线上就是这么被抱怨的：群友纯文字让它听歌，旧实现完全不理会）
        // 先把上一步关掉的总开关重新打开
        var reopen = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent("""{"enableMusic":true}""", Encoding.UTF8, "application/json"), cts.Token);
        Check("★ 重新打开听音乐开关", reopen.IsSuccessStatusCode);
        await Task.Delay(300);

        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 88, "reply": "我去听听。", "listen": "测试小夜曲 测试歌手"}""");
        // 听完回来接话的那一轮：响应的内容必须真的发出来 ——
        // 这一轮请求的上下文以“你自己刚说的话”结尾，部分上游（Gemini）会直接 400（Requests ending
        // with a model turn are not supported）；只断言“请求发出去了”盖不住这个问题，
        // 因为假上游会先把请求记下来、再回错误 —— 必须看回复有没有真发出来。
        openAi.EnqueueReply("""{"suitability": 90, "reply": "副歌那两句我听着挺熟。"}""");
        var listenMark = protocol.ActionsReceived.Count;
        await protocol.SendGroupMessageAsync(groupId, 30014, "群友B",
            "@10001 去听一下测试小夜曲", 9307, mentionBot: true, ct: cts.Token);

        var afterListen = await WaitForRequestAsync(openAi,
            r => r.Contains("波形实测") && r.Contains("我去听听"), TimeSpan.FromSeconds(90));
        Check("★ 群里让它听歌会真的去搜索（模型用 listen 字段发起）",
            music.SearchHits >= 1, $"searchHits={music.SearchHits}");
        Check("★ 听完后带着歌词与波形实测回来接话（而不是只回一句“我去听听”）",
            afterListen is not null && afterListen.Contains("测试歌词第一句"),
            afterListen is null ? "(没等到听完之后的回复)" : "已把实测数据交给模型");

        await WaitUntilAsync(() => GroupSendsSince(protocol, listenMark).Any(a => MessageText(a).Contains("副歌")),
            TimeSpan.FromSeconds(15));
        var heardSends = GroupSendsSince(protocol, listenMark);
        Check("★ 听完之后的这一轮真的发出了回复（上游拒绝 model-turn 结尾时这里会卡死）",
            heardSends.Any(a => MessageText(a).Contains("副歌")),
            $"本轮发出 {heardSends.Count} 条：{string.Join(" | ", heardSends.Select(MessageText))}");

        // ---- 5) 语境合适时，机器人自己分享一张网易云卡片 ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 90, "reply": "来一首这个。", "shareSong": "测试小夜曲 测试歌手"}""");
        await protocol.SendGroupMessageAsync(groupId, 30015, "小美",
            "@10001 推荐首歌", 9309, mentionBot: true, ct: cts.Token);
        await Task.Delay(6000);

        var musicCard = protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .Select(a => a.ToJsonString())
            .LastOrDefault(t => t.Contains("\"music\""));
        Check("★ 语境合适时机器人自己分享音乐卡片（发的是 music 段，不是纯文字）",
            musicCard is not null, musicCard is null ? "(没发出卡片)" : "已发出卡片");
        Check("★ 卡片是网易云（type=163）且带上了搜到的歌曲 id",
            musicCard is not null && musicCard.Contains("163") && musicCard.Contains("999001"),
            musicCard is null ? "(无)" : musicCard.Substring(Math.Max(0, musicCard.Length - 160)));
    }

    /// <summary>取“从第 mark 条动作之后”的群消息（把“本步新发出的”与历史分开）。</summary>
    private static List<JsonObject> GroupSendsSince(MockProtocol protocol, int mark)
        => protocol.ActionsReceived.Skip(mark)
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
            .ToList();

    /// <summary>取“从第 mark 条动作之后”的私聊消息。</summary>
    private static List<JsonObject> PrivateSendsSince(MockProtocol protocol, int mark)
        => protocol.ActionsReceived.Skip(mark)
            .Where(a => a["action"]?.GetValue<string>() == "send_private_msg")
            .ToList();

    /// <summary>等一条满足条件的模型请求（返回请求全文，超时返回 null）。</summary>
    private static async Task<string?> WaitForRequestAsync(MockOpenAi openAi, Func<string, bool> match, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            for (var i = openAi.Requests.Count - 1; i >= 0; i--)
            {
                var text = openAi.DescribeRequest(i);
                if (match(text))
                {
                    return text;
                }
            }

            await Task.Delay(200);
        }

        return null;
    }

    /// <summary>等机器人发出至少 n 条消息（默认群消息；私聊传 action="send_private_msg"）。</summary>
    private static async Task<List<JsonObject>> WaitForSendsAsync(MockProtocol protocol, int count, TimeSpan timeout, string action = "send_group_msg")
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            var sends = protocol.ActionsReceived
                .Where(a => a["action"]?.GetValue<string>() == action)
                .ToList();
            if (sends.Count >= count)
            {
                return sends;
            }

            await Task.Delay(200);
        }

        return protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == action)
            .ToList();
    }

    private static int? ExtractBpm(string text)
    {
        var m = Regex.Match(text, @"速度约\s*(\d+(?:\.\d+)?)\s*BPM");
        return m.Success ? (int)Math.Round(double.Parse(m.Groups[1].Value)) : null;
    }

    private static string Snippet(string text, string marker)
    {
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return text.Length > 160 ? text[..160] : text;
        }

        var start = Math.Max(0, idx - 60);
        var len = Math.Min(200, text.Length - start);
        return text.Substring(start, len).Replace('\n', ' ');
    }
}
