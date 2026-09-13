using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S21 模型接口可在面板里改（Base URL / API Key / 模型名）。
///
/// 以前这三项只读容器环境变量：想换个中转、换个模型、换个密钥，都得改 .env 再重启容器。
/// 现在面板是“最新意图”，环境变量退居种子。这条链路要盯住的是：改完**真的**走新地址，
/// 重启**真的**不回滚，以及密钥**真的**不落进 settings.json（那份文件是给人看/贴出来排障的）。
/// </summary>
public static partial class Program
{
    private static async Task RunModelConfigScenarioAsync()
    {
        Section("S21 模型接口面板可改（换中转 / 换模型 / 换密钥，不重启、不回滚）");

        const int openAiPortA = 17825; // 环境变量指向的“旧”中转
        const int openAiPortB = 17826; // 面板改成的新中转
        const int botWsPort = 13033;
        const int panelPort = 18096;
        const long groupId = 66691;
        var dataDir = NewDataDir("s21");

        using var openAiA = new MockOpenAi(openAiPortA);
        openAiA.Start();
        using var openAiB = new MockOpenAi(openAiPortB);
        openAiB.Start();

        var env = new Dictionary<string, string>
        {
            ["QQCHAT_DATA_DIR"] = dataDir,
            ["QQCHAT_API_KEY"] = "sk-from-env",
            ["QQCHAT_BASE_URL"] = openAiA.BaseUrl,   // 环境变量指着 A
            ["QQCHAT_MODEL"] = "model-from-env",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{botWsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = groupId.ToString(),
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString()
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var bot = StartBot(env);
        await WaitForPortAsync(botWsPort, cts.Token, bot);

        using var protocol = new MockProtocol { SelfId = 10001 };
        await protocol.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
        await WaitUntilAsync(() => bot.OutputLines.Any(l => l.Contains("已启动")), TimeSpan.FromSeconds(20));

        // ---- 1) 起点：环境变量的值与来源都要如实报出来 ----
        var (s0, body0) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("初始配置来自环境变量（面板能看见来源）",
            s0 == 200 && body0.Contains($"\"{openAiA.BaseUrl}\"") &&
            body0.Contains("\"modelBaseUrlSource\":\"env\"") && body0.Contains("\"apiKeySource\":\"env\""),
            Truncate(body0, 400));

        openAiA.EnqueueReply("""{"suitability": 90, "reply": "旧中转回答"}""");
        await protocol.SendGroupMessageAsync(groupId, 20001, "小明", "会不会走旧中转", 51001, mentionBot: true, ct: cts.Token);
        var usedA = await WaitUntilAsync(() => openAiA.Requests.Count > 0, TimeSpan.FromSeconds(25));
        Check("改之前请求确实发给环境变量里的 A", usedA, $"A 收到 {openAiA.Requests.Count} 次");

        // ---- 2) 面板改成新中转 + 新模型名 + 新密钥 ----
        var newKey = "sk-panel-123456789";
        var patch = new JsonObject
        {
            ["modelBaseUrl"] = openAiB.BaseUrl,
            ["model"] = "model-from-panel",
            ["apiKey"] = newKey
        };
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            using var content = new StringContent(patch.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings", content, cts.Token);
            Check("面板保存模型接口（200）", res.IsSuccessStatusCode, res.StatusCode.ToString());
        }

        // ---- 3) 立刻生效：下一条消息必须打到 B，且带上新模型名 ----
        openAiB.EnqueueReply("""{"suitability": 90, "reply": "新中转回答"}""");
        await protocol.SendGroupMessageAsync(groupId, 20001, "小明", "现在走新中转", 51002, mentionBot: true, ct: cts.Token);
        var usedB = await WaitUntilAsync(() => openAiB.Requests.Count > 0, TimeSpan.FromSeconds(25));
        var modelInRequest = openAiB.Requests.LastOrDefault()?["model"]?.GetValue<string>();
        Check("★ 改完立刻生效：请求打到面板里填的新地址（不用重启容器）", usedB,
            $"B 收到 {openAiB.Requests.Count} 次；A 共 {openAiA.Requests.Count} 次");
        Check("★ 请求里带的是面板里填的模型名", modelInRequest == "model-from-panel", $"实际 model={modelInRequest}");

        // ---- 4) 落库与“密钥不落 settings” ----
        await Task.Delay(700);
        var settingsJson = DbProbe.Text(dataDir, "SELECT json FROM settings WHERE id = 1") ?? string.Empty;
        var secretsValue = DbProbe.Text(dataDir, "SELECT value FROM secrets WHERE name = 'apiKey'") ?? string.Empty;
        Check("面板改的 Base URL / 模型名写进了配置（重启不回滚）",
            settingsJson.Contains(openAiB.BaseUrl) && settingsJson.Contains("model-from-panel"),
            Truncate(settingsJson, 300));
        Check("★ 密钥**没有**写进配置（那份配置会被贴出来排障）",
            !settingsJson.Contains(newKey) && !settingsJson.Contains(oldKeyFromEnvPlaceholder()),
            Truncate(settingsJson, 300));
        Check("★ 密钥单独存在 secrets 表里", secretsValue.Contains(newKey), Truncate(secretsValue, 200));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(DbProbe.DbPath(dataDir));
            Check("库文件权限是 600（里面装着密钥）",
                mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), mode.ToString());
        }

        var (_, body1) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
        Check("面板只回显掩码（不回显明文密钥）",
            !body1.Contains(newKey) && body1.Contains("\"apiKeySource\":\"panel\"") && body1.Contains("\"modelBaseUrlSource\":\"panel\""),
            Truncate(body1, 400));

        // ---- 5) 无效 URL 要被挡下，不能把配置写坏 ----
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            using var badContent = new StringContent("{\"modelBaseUrl\":\"不是个地址\"}", Encoding.UTF8, "application/json");
            using var badRes = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings", badContent, cts.Token);
            Check("★ 非法 Base URL 返回 400（不会把配置写坏到连不上模型）", (int)badRes.StatusCode == 400, ((int)badRes.StatusCode).ToString());
        }

        await bot.StopAsync();
        await Task.Delay(1200);

        // ---- 6) 重启：面板的值必须压过环境变量（同一份环境变量仍指着 A）----
        // 用同一个 mock（B）继续收：换新实例会撞端口（HttpListener 的前缀注册是按机器算的）。
        openAiB.ClearRequests();
        using (var bot2 = StartBot(env))
        {
            await WaitForPortAsync(botWsPort, cts.Token, bot2);
            using var protocol2 = new MockProtocol { SelfId = 10001 };
            await protocol2.ConnectReverseAsync($"ws://127.0.0.1:{botWsPort}", cts.Token);
            await WaitUntilAsync(() => bot2.OutputLines.Any(l => l.Contains("已启动")), TimeSpan.FromSeconds(20));

            openAiB.EnqueueReply("""{"suitability": 90, "reply": "重启后还在新中转"}""");
            await protocol2.SendGroupMessageAsync(groupId, 20001, "小明", "重启后走哪", 51003, mentionBot: true, ct: cts.Token);
            var stillB = await WaitUntilAsync(() => openAiB.Requests.Count > 0, TimeSpan.FromSeconds(30));
            var bootLines = string.Join(" ｜ ", bot2.OutputLines.Where(l => l.Contains("覆盖") || l.Contains("模型")).TakeLast(3));
            Check("★ 重启后依然用面板里的值（环境变量不再回滚它）", stillB,
                $"重启后 B 收到 {openAiB.Requests.Count} 次；启动日志：{Truncate(bootLines, 240)}");

            // ---- 7) 清空密钥 → 回退环境变量 ----
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                using var clearContent = new StringContent("{\"apiKey\":\"\"}", Encoding.UTF8, "application/json");
                using var clearRes = await http.PostAsync($"http://127.0.0.1:{panelPort}/api/settings", clearContent, cts.Token);
                Check("清空密钥返回 200", clearRes.IsSuccessStatusCode, clearRes.StatusCode.ToString());
            }

            await Task.Delay(700);
            var secretsAfter = DbProbe.Text(dataDir, "SELECT value FROM secrets WHERE name = 'apiKey'") ?? string.Empty;
            var (_, body2) = await HttpGetAsync($"http://127.0.0.1:{panelPort}/api/settings");
            Check("★ 清空后密钥不再存在库里，且来源回到环境变量",
                !secretsAfter.Contains(newKey) && body2.Contains("\"apiKeySource\":\"env\""),
                Truncate(body2, 300));

            await bot2.StopAsync();
        }
    }

    private static string oldKeyFromEnvPlaceholder() => "sk-from-env";
}
