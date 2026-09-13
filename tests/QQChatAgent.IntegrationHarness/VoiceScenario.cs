using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S24 语音消息：模型填 <c>speak</c> → 机器人发 OneBot <c>record</c> 段（只给 URL，让协议端自己下载）→
/// 面板「试听一句」能当场合成并回放。
///
/// 这条链路全是“静默失败”体质，所以断言分四层：
///   ① 发送层：record 段里是不是我们要的 URL（文本/音色/语速都对），且**不是**又发一遍文字；
///   ② 记忆层：机器人自己说过的话要落进上下文（[语音] …），否则模型下一轮不记得自己说过；
///   ③ 克制层：同一会话 45 秒内不重复发语音、超字数不发、开关关了不发 —— 一律退化成文字；
///   ④ 面板层：/api/voice/test 真的能合成出 wav，/api/voice/health 能报出音色；
///      上游拒绝（retcode≠0）时机器人要改发文字，而不是什么都不发。
/// </summary>
public static partial class Program
{
    private static async Task RunVoiceScenarioAsync()
    {
        Section("S24 语音：模型 speak → record 段（URL 交给协议端）→ 面板试听/健康检查");

        const int openAiPort = 17829;
        const int botWsPort = 13036;
        const int panelPort = 18099;
        const int ttsPort = 18100;
        const long groupId = 66685;
        const long friendId = 30011;
        const long friendId2 = 30012;

        var dataDir = NewDataDir("s24");
        using var tts = new MockTtsHost(ttsPort);
        tts.Start();

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
            ["QQCHAT_WHITELIST"] = $"{groupId},{friendId},{friendId2}",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_ENABLE_VOICE"] = "1",
            ["QQCHAT_VOICE"] = "zh_CN-huayan-medium",
            ["QQCHAT_VOICE_SPEED"] = "110",
            ["QQCHAT_VOICE_MAX_CHARS"] = "30",
            ["QQCHAT_TTS_URL"] = tts.BaseUrl
        });

        await WaitForPortAsync(botWsPort, cts.Token, bot);

        var (settingsStatus, settingsBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("★ 语音配置从环境变量生效（开关/音色/语速/上限/TTS 地址）",
            settingsStatus == 200 && settingsBody.Contains("\"enableVoice\":true") &&
            settingsBody.Contains("\"voiceMaxChars\":30") && settingsBody.Contains("zh_CN-huayan-medium") &&
            settingsBody.Contains("\"voiceSpeed\":110"),
            settingsBody.Length > 240 ? settingsBody[^240..] : settingsBody);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(10));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // ---- 1) 模型要求“这句用语音说” ----
        const string spoken = "这首歌我想用声音唱给你听";
        openAi.ClearRequests();
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "", "speak": "{{spoken}}"}""");
        await protocol.SendGroupMessageAsync(groupId, 40001, "小美", "@10001 来一个", 9401, mentionBot: true, ct: cts.Token);

        var sends = await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(40));
        var voiceAction = sends.LastOrDefault(a => SegmentType(a, "record") is not null);
        var voiceUrl = voiceAction is null ? null : SegmentFile(voiceAction, "record");
        Check("★ 模型填 speak → 真发出 record 段（不是又发一条文字）",
            voiceUrl is not null,
            voiceAction is null ? $"只看到 {sends.Count} 条动作：{string.Join(" | ", sends.Select(a => a.ToJsonString()[..Math.Min(120, a.ToJsonString().Length)]))}" : "已发出 record 段");

        Check("★ record 段里放的是 TTS 的 /speak URL（把下载与转 silk 交给协议端）",
            voiceUrl is not null && voiceUrl.StartsWith(tts.BaseUrl + "/speak?", StringComparison.Ordinal) && voiceUrl.Contains("/speak?text="),
            voiceUrl ?? "(没有 record 段)");
        Check("★ URL 里的文本被正确转义（中文/空格不会把查询串撕碎）",
            voiceUrl is not null && voiceUrl.Contains(Uri.EscapeDataString(spoken)),
            voiceUrl is null ? "(无)" : Snippet(voiceUrl, "text="));
        Check("★ URL 带上了配置里的音色与语速（110% → speed=1.1）",
            voiceUrl is not null && voiceUrl.Contains("voice=zh_CN-huayan-medium") && voiceUrl.Contains("speed=1.1"),
            voiceUrl ?? "(无)");
        Check("★ 语音说过就不再用文字重复同一句（只发一条消息）",
            sends.Count == 1 && MessageText(sends.Count > 0 ? sends[0] : null).Length == 0,
            $"本次发出 {sends.Count} 条，文字段内容「{MessageText(sends.Count > 0 ? sends[0] : null)}」");

        // ---- 2) 上下文里要留下“我刚才是用语音说的什么” ----
        openAi.ClearRequests();
        openAi.EnqueueReply("""{"suitability": 70, "reply": "嗯。"}""");
        await protocol.SendGroupMessageAsync(groupId, 40002, "小美", "@10001 好听吗", 9402, mentionBot: true, ct: cts.Token);
        var afterVoice = await WaitForRequestAsync(openAi, r => r.Contains("好听吗"), TimeSpan.FromSeconds(40));
        Check("★ 机器人自己发过的语音会落进上下文（[语音] + 原话）",
            afterVoice is not null && afterVoice.Contains("[语音]") && afterVoice.Contains(spoken),
            afterVoice is null ? "(没等到下一次模型请求)" : Snippet(afterVoice, "[语音]"));

        // ---- 3) 克制层：同一会话 45 秒内不重复发语音（退化成文字） ----
        openAi.ClearRequests();
        const string secondLine = "刚说过话又想唱一句，这次应该被拦下来";
        var groupMark = protocol.ActionsReceived.Count;
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "", "speak": "{{secondLine}}"}""");
        await protocol.SendGroupMessageAsync(groupId, 40003, "小美", "@10001 再唱一个", 9403, mentionBot: true, ct: cts.Token);

        await Task.Delay(6000);
        var secondSends = GroupSendsSince(protocol, groupMark);
        var secondVoice = secondSends.FirstOrDefault(a => SegmentType(a, "record") is not null);
        var secondText = secondSends.Select(MessageText).LastOrDefault(t => t.Length > 0) ?? string.Empty;
        Check("★ 冷却期内不发第二条语音（模型不听话也有代码兜底）",
            secondVoice is null, secondVoice is null ? "已拦下" : "竟然又发了一条 record");
        Check("★ 被拦下的语音内容改成文字发出去（内容不能丢）",
            secondText.Contains(secondLine),
            secondText.Length > 0 ? secondText : "(没看到文字兜底)");

        // ---- 4) 超过字数上限同样退化成文字 ----
        openAi.ClearRequests();
        const string tooLong = "这句话故意写得比字数上限还要长一些用来验证超限以后会退化成文字而不是被截断";
        var longMark = protocol.ActionsReceived.Count;
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "", "speak": "{{tooLong}}"}""");
        await protocol.SendGroupMessageAsync(groupId, 40004, "小明", "@10001 说句长的", 9404, mentionBot: true, ct: cts.Token);

        await Task.Delay(6000);
        var afterLong = GroupSendsSince(protocol, longMark);
        Check("★ 超过字数上限不发语音（长语音又慢又费流量）",
            afterLong.All(a => SegmentType(a, "record") is null),
            $"本步 record 段 {afterLong.Count(a => SegmentType(a, "record") is not null)} 条");
        Check("★ 超限的内容照旧发出来（不截断、不静默）",
            afterLong.Any(a => MessageText(a).Contains("比字数上限还要长")),
            afterLong.Select(MessageText).LastOrDefault(t => t.Length > 0) ?? "(无)");

        // ---- 5) speak: true = 用语音说 reply（模型偷懒时的写法）----
        //  换个会话（私聊）验证：语音频率门是**按会话**的，私聊不该被群里的冷却拖住。
        const string privateLine = "私聊里用语音说这句";
        openAi.ClearRequests();
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "{{privateLine}}", "speak": true}""");
        await protocol.SendPrivateMessageAsync(friendId, "小美", "喂", 9405, ct: cts.Token);

        var privateSends = await WaitForSendsAsync(protocol, 1, TimeSpan.FromSeconds(40), "send_private_msg");
        var privateVoice = privateSends.LastOrDefault(a => SegmentType(a, "record") is not null);
        Check("★ speak:true 时把 reply 当成语音内容（模型偷懒也认）",
            privateVoice is not null && (SegmentFile(privateVoice, "record") ?? string.Empty).Contains(Uri.EscapeDataString(privateLine)),
            privateVoice is null ? "(私聊没发 record 段)" : SegmentFile(privateVoice, "record"));
        Check("★ 频率门按会话隔离（群里的冷却不影响私聊）",
            privateSends.Count(a => SegmentType(a, "record") is not null) == 1);

        // ---- 6) 协议端拒绝（retcode≠0）→ 改发文字，而不是“什么都不发” ----
        //  用另一个好友（新会话）验证：上一个会话刚发过语音，会被频率门拦掉，测不到“上游拒绝”这条路径。
        protocol.FailActions.Add("send_private_msg");
        openAi.ClearRequests();
        const string failLine = "这条语音发不出去就请改成文字";
        var privateMark = protocol.ActionsReceived.Count;
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "", "speak": "{{failLine}}"}""");
        await protocol.SendPrivateMessageAsync(friendId2, "小张", "再说一句", 9406, ct: cts.Token);
        await Task.Delay(6000);

        var failedPrivate = PrivateSendsSince(protocol, privateMark);
        Check("★ record 段被协议端拒绝时会再试一次文字（内容一定落到群里）",
            failedPrivate.Count(a => SegmentType(a, "record") is not null) == 1 &&
            failedPrivate.Any(a => MessageText(a).Contains(failLine)),
            $"record 段 {failedPrivate.Count(a => SegmentType(a, "record") is not null)} 条，文字兜底「{failedPrivate.Select(MessageText).LastOrDefault(t => t.Length > 0) ?? "(无)"}」");
        protocol.FailActions.Clear();

        // ---- 7) 面板：试听一句（真回 wav）+ 检查服务 ----
        var testBody = new JsonObject { ["text"] = "面板试听这一句", ["voice"] = "zh_CN-xiao_ya-medium", ["speed"] = 90 }.ToJsonString();
        using var testResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/voice/test",
            new StringContent(testBody, Encoding.UTF8, "application/json"), cts.Token);
        var testBytes = await testResp.Content.ReadAsByteArrayAsync(cts.Token);
        Check("★ 面板「试听一句」返回真 wav（浏览器能直接播）",
            testResp.IsSuccessStatusCode && testBytes.Length > 44 && testBytes[0] == 'R' && testBytes[1] == 'I' && testBytes[2] == 'F' && testBytes[3] == 'F',
            $"HTTP {(int)testResp.StatusCode}，{testBytes.Length} 字节");
        var lastCall = tts.SpeakCalls.LastOrDefault();
        Check("★ 试听用的音色/语速是面板当时填的值（没保存也能先听）",
            lastCall.Text == "面板试听这一句" && lastCall.Voice == "zh_CN-xiao_ya-medium" && lastCall.Speed == "0.9",
            lastCall.Text is null ? "(假 TTS 没收到请求)" : $"text={lastCall.Text} voice={lastCall.Voice} speed={lastCall.Speed}");

        var (healthStatus, healthBody) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/voice/health");
        Check("★ 面板能问出 TTS 服务状态与可用音色（排障入口）",
            healthStatus == 200 && healthBody.Contains("\"ok\":true") && healthBody.Contains("zh_CN-xiao_ya-medium"),
            healthBody.Length > 200 ? healthBody[^200..] : healthBody);
        Check("★ 假 TTS 的 /health 真的被访问过", tts.HealthHits >= 1, $"healthHits={tts.HealthHits}");

        // 音色不存在：上游 404 的 error 原文要透传到面板（而不是笼统的“失败”）
        var badVoice = new JsonObject { ["text"] = "不存在的音色", ["voice"] = "zh_CN-nobody" }.ToJsonString();
        using var badResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/voice/test",
            new StringContent(badVoice, Encoding.UTF8, "application/json"), cts.Token);
        var badBody = await badResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ 音色写错时把上游原话带给面板（音色不存在：…）",
            !badResp.IsSuccessStatusCode && badBody.Contains("音色不存在"),
            $"HTTP {(int)badResp.StatusCode}：{badBody}");

        // TTS 挂了：面板要如实报错（而不是给一段空音频）
        tts.Broken = true;
        using var brokenResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/voice/test",
            new StringContent(new JsonObject { ["text"] = "挂了" }.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token);
        var brokenBody = await brokenResp.Content.ReadAsStringAsync(cts.Token);
        Check("★ TTS 容器挂了时面板试听如实报错",
            !brokenResp.IsSuccessStatusCode && brokenBody.Contains("piper 挂了"),
            $"HTTP {(int)brokenResp.StatusCode}：{brokenBody}");
        tts.Broken = false;

        // ---- 8) 关掉语音开关 → 模型再要语音也只发文字 ----
        using var saveResp = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings",
            new StringContent("""{"enableVoice":false}""", Encoding.UTF8, "application/json"), cts.Token);
        Check("★ 面板可以关掉语音消息", saveResp.IsSuccessStatusCode, $"HTTP {(int)saveResp.StatusCode}");
        await Task.Delay(300);

        const string offLine = "开关关掉以后这句只能打字了";
        openAi.ClearRequests();
        var offMark = protocol.ActionsReceived.Count;
        openAi.EnqueueReply($$"""{"suitability": 90, "reply": "", "speak": "{{offLine}}"}""");
        await protocol.SendGroupMessageAsync(groupId, 40005, "小美", "@10001 说句话", 9405, mentionBot: true, ct: cts.Token);
        await Task.Delay(6000);

        var offSends = GroupSendsSince(protocol, offMark);
        Check("★ 关掉开关后不再发语音（record 段一个都没有）",
            offSends.All(a => SegmentType(a, "record") is null),
            $"record 段 {offSends.Count(a => SegmentType(a, "record") is not null)} 条");
        Check("★ 关掉后模型想说的话仍以文字发出",
            offSends.Any(a => MessageText(a).Contains(offLine)),
            offSends.Select(MessageText).LastOrDefault(t => t.Length > 0) ?? "(无)");
    }

    /// <summary>取消息里某个段类型的 data.file（没有该段返回 null）。</summary>
    private static string? SegmentFile(JsonObject? action, string type)
        => SegmentType(action, type)?["data"]?["file"]?.GetValue<string>();

    /// <summary>取消息里第一个指定类型的段（没有则 null）。</summary>
    private static JsonNode? SegmentType(JsonObject? action, string type)
    {
        if (action?["params"]?["message"] is not JsonArray segments)
        {
            return null;
        }

        return segments.FirstOrDefault(s => s?["type"]?.GetValue<string>() == type);
    }
}
