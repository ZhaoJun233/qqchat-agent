using System.Runtime.InteropServices;
using QQChatAgent.Configuration;
using QQChatAgent.Services;
using QQChatAgent.Services.Agent;
using QQChatAgent.Services.Data;
using QQChatAgent.Services.NapCat;
using QQChatAgent.Services.OneBot;

namespace QQChatAgent.Headless;

using System.Text;

/// <summary>
/// 无界面机器人宿主：加载配置 → 组装服务 → 建立 OneBot 连接 → 常驻运行、优雅退出。
/// 桌面版（WinUI）的 Agent 逻辑原样复用于此，只是去掉界面、改为容器部署。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8; // 中文 banner 在 GBK 代码页下不乱码
        }
        catch
        {
            // 输出被重定向到管道时部分平台不允许设置编码：忽略
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (args.Contains("--version"))
        {
            Console.WriteLine("QQChatAgent.Headless 0.1.0");
            return 0;
        }

        // 容器 HEALTHCHECK 用：探测自身 /healthz，不依赖镜像里的 curl
        if (args.Contains("--health"))
        {
            return await ProbeHealthAsync();
        }

        // ---------- 数据层：先建库（并一次性把老 JSON 导进来），再读配置 ----------
        // 顺序很重要：BotConfig.Load() 会从 settings 表读配置，之前必须把库准备好。
        AppDatabase.Initialize();
        LegacyJsonImporter.ImportIfNeeded();

        // ---------- 配置 ----------
        var settings = BotConfig.Load();
        FileLog.Verbose = settings.VerboseLog;
        FileLog.WriteToFile = Environment.GetEnvironmentVariable("QQCHAT_LOG_FILE") != "0";

        PrintBanner(settings, !string.IsNullOrWhiteSpace(settings.NapCatWebUiToken) && !string.IsNullOrWhiteSpace(settings.NapCatWebUiUrl));

        var errors = BotConfig.Validate(settings);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine("  ✗ " + error);
            }

            Console.Error.WriteLine("\n配置有误，已退出。用 --help 查看环境变量说明。");
            return 2;
        }

        // ---------- 组装服务 ----------
        var gateway = new OneBotGateway(settings)
        {
            SelfIdHint = settings.UinOrZero
        };

        var brain = new OpenAiClient(settings)
        {
            BotIdentity = string.IsNullOrWhiteSpace(settings.NormalizedUin) ? null : settings.NormalizedUin,
            BotPersona = settings.BotPersona,
            AiDesire = settings.AiDesire,
            SuitabilityThreshold = settings.SuitabilityThreshold,
            MaxContextMessages = settings.MaxContextMessages // 漏了这行会让上下文窗口在面板修改后不同步
        };

        var store = new ConversationStore();
        var profiles = new MemberProfileStore();
        var agent = new BotAgent(settings, gateway, brain, store, profiles);

        // 面板内的扫码登录：把 NapCat 的登录二维码搬进机器人面板
        // （用户打开面板看不到二维码，是远程部署卡住最久的原因）
        using var loginQr = new LoginQrService(settings.NapCatWebUiUrl, settings.NapCatWebUiToken);

        // 先恢复磁盘会话并订阅事件，再连接协议端，避免启动期消息竞态
        agent.Start();
        gateway.Start();

        using var web = new WebUiServer(settings.HealthPort, settings, gateway, agent, loginQr);
        if (settings.HealthPort > 0)
        {
            web.Start();
        }

        // ---------- 常驻运行 + 优雅退出 ----------
        using var shutdown = new CancellationTokenSource();

        // 退出可能由多个来源同时触发（Ctrl+C、SIGTERM、ProcessExit），
        // 且 ProcessExit 会在 using 释放 CTS 之后才回调 —— 必须做幂等 + 容错，
        // 否则会在退出阶段抛 ObjectDisposedException 影响退出码。
        var shutdownSignalled = 0;
        void RequestShutdown()
        {
            if (Interlocked.Exchange(ref shutdownSignalled, 1) != 0)
            {
                return;
            }

            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已在退出流程中，忽略
            }
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 阻止立即终止，走优雅退出
            RequestShutdown();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestShutdown();

        using var sigterm = TryRegisterSignal(PosixSignal.SIGTERM, RequestShutdown);
        using var sigint = TryRegisterSignal(PosixSignal.SIGINT, RequestShutdown);

        FileLog.Write("Host", $"运行中。数据目录 {AppPaths.RuntimeRoot}（Ctrl+C 或 SIGTERM 退出）");

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径
        }

        FileLog.Write("Host", "收到停止信号，正在退出…");
        agent.Dispose();
        gateway.Stop();
        FileLog.Write("Host", "已停止。");
        return 0;
    }

    /// <summary>健康探测：GET 127.0.0.1:{QQCHAT_HEALTH_PORT}/healthz。成功 0，失败 1。</summary>
    private static async Task<int> ProbeHealthAsync()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("QQCHAT_HEALTH_PORT"), out var p) ? p : 8080;
        if (port <= 0)
        {
            return 0; // 健康检查已关闭视为健康
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"健康探测失败: {ex.Message}");
            return 1;
        }
    }

    private static IDisposable? TryRegisterSignal(PosixSignal signal, Action onSignal)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                onSignal();
            });
        }
        catch (Exception)
        {
            // 平台不支持该信号（例如 Windows 上的 SIGTERM）：忽略
            return null;
        }
    }

    private static void PrintBanner(AppSettings settings, bool loginQrConfigured)
    {
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("  QQ Chat Agent（Headless）  0.1.0");
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine($"  数据目录   {AppPaths.RuntimeRoot}");
        Console.WriteLine($"  配置文件   {SettingsStore.FilePath}" + (File.Exists(SettingsStore.FilePath) ? "" : "（不存在，全部来自环境变量/默认值）"));
        Console.WriteLine($"  模型       {settings.Model} @ {settings.ModelBaseUrl}");
        Console.WriteLine($"  通道       {settings.OneBotProtocol} → {settings.OneBotAddress}");
        Console.WriteLine($"  账号       {(string.IsNullOrWhiteSpace(settings.NormalizedUin) ? "未配置（自动识别）" : settings.NormalizedUin)}");
        Console.WriteLine($"  白名单     {(string.IsNullOrWhiteSpace(settings.MessageWhitelist) ? "空 → 严格模式：忽略全部消息" : settings.MessageWhitelist.Replace("\n", ","))}");
        Console.WriteLine($"  Web 面板   {(settings.HealthPort > 0 ? $"http://127.0.0.1:{settings.HealthPort}/" : "关闭")}");
        Console.WriteLine("  扫码登录   " + (loginQrConfigured
            ? $"面板内可用（NapCat WebUI {settings.NapCatWebUiUrl}）"
            : "未配置（设 QQCHAT_NAPCAT_WEBUI_TOKEN 后可在面板内扫码）"));

        // 行为类环境变量已被 settings.json 接管时明确告知，避免“改了不生效”的困惑
        if (BotConfig.IgnoredBehaviorEnvVars.Count > 0)
        {
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine("  ⚠ 以下环境变量已被 settings.json 接管，改它不再生效：");
            foreach (var item in BotConfig.IgnoredBehaviorEnvVars)
            {
                Console.WriteLine("     · " + item);
            }

            Console.WriteLine("     请在 Web 面板「设置」页修改。");
        }

        // 模型接口（Base URL / 模型名 / 密钥）在面板里改过之后，环境变量就不再是“真相”了。
        // 不说清楚的话，用户改 .env 重启发现没变化，会以为是 bug。
        if (BotConfig.PanelOverriddenEnvVars.Count > 0)
        {
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine("  ⚠ 以下环境变量已被【面板里的设置】覆盖，改它不再生效：");
            foreach (var item in BotConfig.PanelOverriddenEnvVars)
            {
                Console.WriteLine("     · " + item);
            }

            Console.WriteLine("     想改回环境变量：在面板里把该项清空（密钥点“清除密钥”）后保存。");
        }

        Console.WriteLine("──────────────────────────────────────────────");
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            QQ Chat Agent（Headless / 容器版）

            用法: QQChatAgent.Headless [--help] [--version]

            配置文件: $QQCHAT_DATA_DIR/data/settings.json（可挂载桌面版的 runtime/data/settings.json）
            环境变量覆盖（优先级高于 settings.json）:

              QQCHAT_DATA_DIR          运行时数据目录（默认 ./runtime 或 /data）
              QQCHAT_LOG_FILE          =0 则不写日志文件，只输出 stdout
              QQCHAT_VERBOSE           =0 关闭详细日志

              QQCHAT_API_KEY           OpenAI 兼容 API Key（也支持 OPENAI_API_KEY；_FILE 变体读文件）
              QQCHAT_BASE_URL          接口地址，如 https://api.deepseek.com/v1（或 OPENAI_BASE_URL）
              QQCHAT_MODEL             模型名，如 deepseek-chat（或 OPENAI_MODEL）
              QQCHAT_MAX_TOKENS        单次回复上限（默认 2048）

              QQCHAT_ONEBOT_PROTOCOL   ForwardWebSocket | ReverseWebSocket | Http
              QQCHAT_ONEBOT_URL        ws://napcat:3001 / http://0.0.0.0:3001 / http://napcat:3000
              QQCHAT_ONEBOT_TOKEN      协议端 Access Token（_FILE 变体读文件）
              QQCHAT_UIN               机器人登录 QQ 号

              QQCHAT_WHITELIST         白名单：群号/QQ号，逗号或换行分隔；'*' 表示全部接收
              QQCHAT_PERSONA           机器人人设（_FILE 变体读文件）
              QQCHAT_AI_MODE           =0 只收消息不自动回复（默认 1）
              QQCHAT_AI_DESIRE         对话欲望 0-100（默认 50）
              QQCHAT_SUITABILITY_THRESHOLD  发言适合度阈值 0-100（默认 10）

              QQCHAT_PRIVATE_COOLDOWN  私聊冷却秒数（默认 3）
              QQCHAT_GROUP_COOLDOWN    群聊冷却秒数（默认 8）
              QQCHAT_IDLE_FALLBACK     静默兜底秒数，0=关闭（默认 60）
              QQCHAT_SPLIT_REPLIES     长回复分句发送（默认 1）
              QQCHAT_SEGMENT_DELAY_MS  分句之间的间隔毫秒（默认 700）
              QQCHAT_MAX_CONTEXT       最大上下文条数（默认 200）
              QQCHAT_PROFILE_LOOKUP    附带人物档案数上限（默认 8）

              QQCHAT_HEALTH_PORT       健康检查端口，0=关闭（默认 8080）
              QQCHAT_NAPCAT_WEBUI_URL  NapCat WebUI 地址（默认 http://napcat:6099）
              QQCHAT_NAPCAT_WEBUI_TOKEN  NapCat WebUI 令牌（napcat/config/webui.json 的 token）
                                       配了它，面板就能直接显示登录二维码（扫码登录）

            Web 面板: GET /             WinUI 界面的 Web 复刻（会话列表 / 聊天气泡 / 设置）
                      GET /api/events    SSE 实时推送
                      GET /api/qqlogin  当前登录二维码信息（面板扫码卡片用）
                      GET /api/qqlogin/qrcode.svg  登录二维码（SVG）
            端点:     GET /healthz      存活探针（始终 200）
                      GET /readyz       就绪探针（已连上协议端 200，否则 503）
                      GET /status       运行状态 JSON
            运维:     --health        探测本机 /healthz（容器 HEALTHCHECK 用，无需 curl）

            退出: Ctrl+C 或 SIGTERM（容器 docker stop）会优雅收尾并落盘。
            """);
    }
}
