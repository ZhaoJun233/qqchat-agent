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
    }

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

    /// <summary>等机器人发出至少 n 条群消息。</summary>
    private static async Task<List<JsonObject>> WaitForSendsAsync(MockProtocol protocol, int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            var sends = protocol.ActionsReceived
                .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
                .ToList();
            if (sends.Count >= count)
            {
                return sends;
            }

            await Task.Delay(200);
        }

        return protocol.ActionsReceived
            .Where(a => a["action"]?.GetValue<string>() == "send_group_msg")
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
