using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QQChatAgent.Models;
using QQChatAgent.Services;
using QQChatAgent.Services.NapCat;
using QQChatAgent.Services.OneBot;

namespace QQChatAgent.Configuration;

/// <summary>
/// Web 面板 + 健康检查服务（复用 in-box HttpListener，不引入 ASP.NET，保持镜像体积）。
///
///   面板      GET  /                       WinUI 界面的 Web 复刻
///   静态      GET  /app.css  /app.js
///   健康      GET  /healthz               存活（docker HEALTHCHECK）
///             GET  /readyz                就绪（已连协议端）
///             GET  /status                状态 JSON（兼容原接口）
///   API       GET  /api/state             面板首屏快照
///             GET  /api/conversations/{key}/messages
///             POST /api/conversations/{key}/send|read|delete
///             GET  /api/profiles/{uid}
///             GET  /api/settings
///             POST /api/settings
///             POST /api/ai-mode
///   实时      GET  /api/events            SSE：新消息/未读/思考中/日志/状态
///   QQ 登录   GET  /api/qqlogin           当前登录二维码信息（URL / 剩余新鲜度 / 错误）
///             GET  /api/qqlogin/qrcode.svg 二维码图片（&lt;img&gt; 不能带自定义头，所以支持 ?token=）
/// </summary>
public sealed class WebUiServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly int _port;
    private readonly string _bind;
    private readonly AppSettings _settings;
    private readonly OneBotGateway _gateway;
    private readonly BotAgent _agent;
    private readonly LoginQrService _loginQr;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _clientsGate = new();
    private readonly List<SseClient> _clients = new();

    private HttpListener? _listener;
    private Task? _loop;
    private int _convBroadcastPending;
    private long _convBroadcastVersion;

    public WebUiServer(int port, AppSettings settings, OneBotGateway gateway, BotAgent agent, LoginQrService loginQr)
    {
        _port = port;
        _settings = settings;
        _gateway = gateway;
        _agent = agent;
        _loginQr = loginQr;
        _bind = Environment.GetEnvironmentVariable("QQCHAT_HEALTH_BIND")?.Trim() is { Length: > 0 } custom
            ? custom
            : "+";
    }

    /// <summary>实际监听的前缀（启动失败为 null）。</summary>
    public string? ListeningOn { get; private set; }

    public void Start()
    {
        // 订阅 Agent 事件 → 实时推送
        _agent.MessageAdded += OnMessageAdded;
        _agent.ConversationsChanged += OnConversationsChanged;
        _agent.StateChanged += OnStateChanged;
        _agent.ThinkingChanged += OnThinkingChanged;
        _agent.LogLine += OnLogLine;

        var candidates = _bind == "+" && !OperatingSystem.IsWindows()
            ? new[] { "+" }
            : _bind == "+"
                ? new[] { "+", "127.0.0.1" }
                : new[] { _bind };

        foreach (var bind in candidates)
        {
            var prefix = $"http://{bind}:{_port}/";
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                _listener = listener;
                ListeningOn = prefix;
                _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
                FileLog.Write("Web", $"面板已监听 {prefix}（/ 、/healthz、/api/*）");
                return;
            }
            catch (Exception ex)
            {
                FileLog.Write("Web", $"绑定 {prefix} 失败：{ex.Message}");
            }
        }

        FileLog.Warn("Web",
            $"面板无法启动（端口 {_port}）。可设 QQCHAT_HEALTH_BIND=127.0.0.1，或 QQCHAT_HEALTH_PORT=0 关闭。");
    }

    // ══════════════ HTTP ══════════════

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => DispatchAsync(context));
        }
    }

    private async Task DispatchAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod;

            // 鉴权：/healthz 与 /readyz 放行（容器 HEALTHCHECK 靠它们）
            if (!path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/readyz", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAuthorized(context))
                {
                    await WriteJsonAsync(context, 401, new JsonObject
                    {
                        ["error"] = "unauthorized",
                        ["hint"] = "本面板已设置 QQCHAT_PANEL_TOKEN，请在地址后加 ?token=你的令牌 打开"
                    });
                    context.Response.Close();
                    return;
                }

                // 防 CSRF：浏览器跨站发起的 POST 会带 Origin/Referer，非浏览器客户端（curl）不带。
                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && !IsSameOrigin(context))
                {
                    FileLog.Write("Web", $"已拒绝跨站 POST：{path}（Origin={context.Request.Headers["Origin"]}）");
                    await WriteJsonAsync(context, 403, new JsonObject { ["error"] = "cross-origin request rejected" });
                    context.Response.Close();
                    return;
                }
            }

            // SSE 是长连接，单独处理（不能走 finally 里的 Close）
            if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseAsync(context, _cts.Token);
                return;
            }

            try
            {
                await RouteAsync(context, path, method);
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // 忽略
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write("Web", $"请求处理异常: {ex.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>
    /// 令牌校验。未配置 QQCHAT_PANEL_TOKEN 时直接放行（回环部署的常见情况）。
    /// 配置后：面板与 /api/* 必须带 X-Panel-Token 头或 ?token= 参数。
    /// </summary>
    private bool IsAuthorized(HttpListenerContext context)
    {
        var expected = _settings.PanelToken?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        var provided = context.Request.Headers["X-Panel-Token"] ?? context.Request.QueryString["token"];
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        // 定时安全比较：长度不等直接返回 false，不会抛异常
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>
    /// 同源校验（防 CSRF）。
    /// 浏览器发 POST 必定带 Origin；只有源自面板自身的 Origin 才放行。
    /// 无 Origin/Referer = 非浏览器客户端（curl、脚本），不适用 CSRF 场景，放行。
    /// </summary>
    private static bool IsSameOrigin(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            origin = context.Request.Headers["Referer"];
        }

        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var requestHost = context.Request.Url?.Host ?? string.Empty;
        if (string.Equals(uri.Host, requestHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // localhost 与 127.0.0.1 视为同一台机器的两种写法
        return uri.IsLoopback && requestHost is "localhost" or "127.0.0.1" or "::1";
    }

    private async Task RouteAsync(HttpListenerContext context, string path, string method)
    {
        // ---------- 静态资源 ----------
        switch (path.TrimEnd('/'))
        {
            case "":
                await WriteAssetAsync(context, "index.html", "text/html; charset=utf-8");
                return;
            case "/app.css":
                await WriteAssetAsync(context, "app.css", "text/css; charset=utf-8");
                return;
            case "/app.js":
                await WriteAssetAsync(context, "app.js", "application/javascript; charset=utf-8");
                return;
            case "/favicon.ico":
                await WriteBytesAsync(context, 204, "image/x-icon", Array.Empty<byte>());
                return;
        }

        // ---------- 健康检查 ----------
        switch (path)
        {
            case "/healthz":
                await WriteJsonAsync(context, 200, new JsonObject { ["status"] = "ok" });
                return;
            case "/readyz":
                var ready = _gateway.IsConnected;
                await WriteJsonAsync(context, ready ? 200 : 503, new JsonObject
                {
                    ["ready"] = ready,
                    ["onebot"] = _gateway.IsConnected ? "connected" : "disconnected"
                });
                return;
            case "/status":
                await WriteJsonAsync(context, 200, BuildStatus());
                return;
        }

        // ---------- QQ 登录二维码（面板内扫码） ----------
        // 为什么放在这里而不是让前端直接连 NapCat WebUI：
        //   浏览器直连 NapCat 需要另一道 Basic 认证 + 跨域，而容器网络里只有本进程能到 napcat:6099。
        if (path.Equals("/api/qqlogin", StringComparison.OrdinalIgnoreCase))
        {
            var refresh = IsTruthy(context.Request.QueryString["refresh"]);
            var snapshot = await _loginQr.SnapshotAsync(refresh, _cts.Token);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = snapshot.Ok,
                ["configured"] = snapshot.Configured,
                ["url"] = snapshot.Url,
                ["ageSeconds"] = snapshot.AgeSeconds,
                ["key"] = snapshot.Key,
                ["refreshAfterSeconds"] = Math.Max(5, 100 - snapshot.AgeSeconds),
                // 超过这个时间还没换新码，说明 NapCat 那边不再轮换二维码了：
                // 用户扫的必然是过期码，面板要把这句话说出来（而不是默默展示一张死码）。
                ["stale"] = snapshot.Ok && snapshot.AgeSeconds > 180,
                ["error"] = snapshot.Error
            });
            return;
        }

        if (path.Equals("/api/qqlogin/qrcode.svg", StringComparison.OrdinalIgnoreCase))
        {
            var refresh = IsTruthy(context.Request.QueryString["refresh"]);
            var snapshot = await _loginQr.SnapshotAsync(refresh, _cts.Token);
            var svg = snapshot.Ok ? _loginQr.Svg() : null;
            if (svg is null)
            {
                await WriteJsonAsync(context, 503, new JsonObject
                {
                    ["error"] = snapshot.Error ?? "暂无二维码",
                    ["configured"] = snapshot.Configured
                });
                return;
            }

            // 图随二维码变，缓存会让用户扫到已失效的旧图
            context.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
            await WriteBytesAsync(context, 200, "image/svg+xml; charset=utf-8", Encoding.UTF8.GetBytes(svg));
            return;
        }

        // ---------- API ----------
        if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context, 200, BuildState());
            return;
        }

        if (path.Equals("/api/settings", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "POST")
            {
                await HandleSettingsSaveAsync(context);
            }
            else
            {
                await WriteJsonAsync(context, 200, BuildSettingsPayload());
            }

            return;
        }

        if (path.Equals("/api/ai-mode", StringComparison.OrdinalIgnoreCase) && method == "POST")
        {
            var body = await ReadJsonAsync(context);
            _agent.AiModeEnabled = body?["enabled"]?.GetValue<bool>() ?? !_agent.AiModeEnabled;
            await WriteJsonAsync(context, 200, new JsonObject { ["aiMode"] = _agent.AiModeEnabled });
            return;
        }

        // /api/conversations/{key}/...
        if (path.StartsWith("/api/conversations/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConversationAsync(context, path["/api/conversations/".Length..], method);
            return;
        }

        // /api/netease/qr：面板内扫码登录网易云（登录态存在自建 API 容器里，登完 VIP 歌才有播放地址）
        if (path.StartsWith("/api/netease/qr", StringComparison.OrdinalIgnoreCase))
        {
            await HandleNeteaseQrAsync(context, path, method);
            return;
        }

        // /api/music/test：一键验证“听音乐”链路（搜索 → 歌词 → 低码率音源 → 波形分析）
        // 为什么要这个入口：这条链路依赖外部 API，挂了只能在群里碰运气 ——
        // 这里可以直接跑一遍，把实测结果贴出来（排障与上线验收都用得上）。
        if (path.Equals("/api/music/test", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMusicTestAsync(context, method);
            return;
        }

        // /api/voice/test：合成一句语音给面板自己播（验证 TTS 服务、音色、语速）。
        // 只合成、不发群 —— 想验证“群里真能听到”得开开关让模型发，或者看 /api/voice/health。
        if (path.Equals("/api/voice/test", StringComparison.OrdinalIgnoreCase))
        {
            await HandleVoiceTestAsync(context, method);
            return;
        }

        // /api/voice/health：问一下 TTS 服务自己（活着吗、有哪些音色），用于一键排障
        if (path.Equals("/api/voice/health", StringComparison.OrdinalIgnoreCase))
        {
            await HandleVoiceHealthAsync(context);
            return;
        }

        // /api/search/test：一键验证“联网搜索”（模型自带搜索 or 搜索源），把结果原样贴出来
        if (path.Equals("/api/search/test", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSearchTestAsync(context, method);
            return;
        }

        // /api/stickers 及子路径（表情包库：列表 / 取图 / 删除 / 立即巡检 / 导入）
        if (path.Equals("/api/stickers", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/stickers/", StringComparison.OrdinalIgnoreCase))
        {
            await HandleStickersAsync(context, path, method);
            return;
        }

        // /api/profiles/{uid}
        if (path.StartsWith("/api/profiles/", StringComparison.OrdinalIgnoreCase))
        {
            var uid = Uri.UnescapeDataString(path["/api/profiles/".Length..]);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["uid"] = uid,
                ["summary"] = _agent.GetProfileSummary(uid)
            });
            return;
        }

        // /api/archive?key=group:123&limit=200　归档消息（已溢出滚动窗口的旧历史）
        if (path.Equals("/api/archive", StringComparison.OrdinalIgnoreCase))
        {
            var key = context.Request.QueryString["key"] ?? string.Empty;
            var limit = int.TryParse(context.Request.QueryString["limit"], out var n) ? Math.Clamp(n, 1, 2000) : 200;
            await WriteJsonAsync(context, 200, ReadArchive(key, limit));
            return;
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "not found", ["path"] = path });
    }

    private async Task HandleConversationAsync(HttpListenerContext context, string rest, string method)
    {
        var parts = rest.Split('/', 2);
        var key = Uri.UnescapeDataString(parts[0]);
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "messages";

        switch (action, method)
        {
            case ("messages", "GET"):
            {
                var conversation = _agent.Find(key);
                if (conversation is null)
                {
                    await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "no such conversation" });
                    return;
                }

                var limit = int.TryParse(context.Request.QueryString["limit"], out var n)
                    ? Math.Clamp(n, 1, 2000)
                    : 300;

                var messages = conversation.TakeLast(limit).Select(ToMessageDto).ToList();
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["key"] = key,
                    ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray())
                });
                return;
            }

            case ("send", "POST"):
            {
                var body = await ReadJsonAsync(context);
                var text = body?["text"]?.GetValue<string>() ?? string.Empty;
                var ok = await _agent.SendAsBotAsync(key, text);
                await WriteJsonAsync(context, ok ? 200 : 400, new JsonObject
                {
                    ["ok"] = ok,
                    ["error"] = ok ? null : (_gateway.IsConnected ? "发送失败" : "QQ 未连接")
                });
                return;
            }

            case ("read", "POST"):
                _agent.MarkRead(key);
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true });
                return;

            case ("delete", "POST"):
                var deleted = _agent.DeleteConversation(key);
                await WriteJsonAsync(context, deleted ? 200 : 404, new JsonObject { ["ok"] = deleted });
                return;

            default:
                await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
                return;
        }
    }

    private async Task HandleSettingsSaveAsync(HttpListenerContext context)
    {
        var body = await ReadJsonAsync(context);
        if (body is null)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "invalid json" });
            return;
        }

        // 模型端点：先校验再应用 —— 无效 URL 直接 400，不然一次手滑就把配置写坏、连不上模型。
        var newBaseUrl = body["modelBaseUrl"] is JsonNode mbu ? (mbu.GetValue<string>() ?? string.Empty).Trim() : null;
        if (newBaseUrl is { Length: > 0 } &&
            (!Uri.TryCreate(newBaseUrl, UriKind.Absolute, out var parsedBaseUrl) ||
             (parsedBaseUrl.Scheme != Uri.UriSchemeHttp && parsedBaseUrl.Scheme != Uri.UriSchemeHttps)))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "Base URL 要形如 http://host:port/v1（http/https 开头的完整地址）" });
            return;
        }

        // 只接受运行时可改的字段（协议端地址、QQ 号这些仍属于容器环境变量职责）
        _agent.ApplyRuntimeSettings(s =>
        {
            if (body["botPersona"] is JsonNode persona) s.BotPersona = persona.GetValue<string>() ?? string.Empty;
            if (body["messageWhitelist"] is JsonNode wl) s.MessageWhitelist = wl.GetValue<string>() ?? string.Empty;
            if (body["aiDesire"] is JsonNode desire) s.AiDesire = Math.Clamp(desire.GetValue<int>(), 0, 100);
            if (body["suitabilityThreshold"] is JsonNode th) s.SuitabilityThreshold = Math.Clamp(th.GetValue<int>(), 0, 100);
            if (body["aiModeEnabled"] is JsonNode ai) s.AiModeEnabled = ai.GetValue<bool>();
            if (body["maxTokens"] is JsonNode mt) s.MaxTokens = Math.Clamp(mt.GetValue<int>(), 64, 32000);
            if (body["groupCooldownSeconds"] is JsonNode gc) s.GroupCooldownSeconds = Math.Max(0, gc.GetValue<int>());
            if (body["privateCooldownSeconds"] is JsonNode pc) s.PrivateCooldownSeconds = Math.Max(0, pc.GetValue<int>());
            if (body["idleFallbackSeconds"] is JsonNode fb) s.IdleFallbackSeconds = Math.Max(0, fb.GetValue<int>());
            if (body["splitReplies"] is JsonNode sp) s.SplitReplies = sp.GetValue<bool>();
            if (body["ignoreBracketMessages"] is JsonNode ibm) s.IgnoreBracketMessages = ibm.GetValue<bool>();
            if (body["segmentDelayMs"] is JsonNode sd) s.SegmentDelayMs = Math.Max(0, sd.GetValue<int>());
            if (body["maxContextMessages"] is JsonNode mc) s.MaxContextMessages = Math.Clamp(mc.GetValue<int>(), 10, 1000);
            if (body["profileLookupCount"] is JsonNode pl) s.ProfileLookupCount = Math.Clamp(pl.GetValue<int>(), 0, 50);
            if (body["profileSummaryLines"] is JsonNode psl) s.ProfileSummaryLines = Math.Clamp(psl.GetValue<int>(), 0, 50);
            if (body["maxProfileChars"] is JsonNode mpc) s.MaxProfileChars = Math.Clamp(mpc.GetValue<int>(), 0, 20000);
            if (body["maxMessagesPerConversation"] is JsonNode mm) s.MaxMessagesPerConversation = Math.Clamp(mm.GetValue<int>(), 20, 100000);
            if (body["maxConcurrentReplies"] is JsonNode mcr) s.MaxConcurrentReplies = Math.Clamp(mcr.GetValue<int>(), 1, 16);
            if (body["enableProfileSummary"] is JsonNode eps) s.EnableProfileSummary = eps.GetValue<bool>();
            if (body["profileSummaryThreshold"] is JsonNode pst) s.ProfileSummaryThreshold = Math.Clamp(pst.GetValue<int>(), 5, 500);
            if (body["profileSummaryMaxChars"] is JsonNode psm) s.ProfileSummaryMaxChars = Math.Clamp(psm.GetValue<int>(), 40, 2000);
            if (body["profileSummaryIntervalSeconds"] is JsonNode psi) s.ProfileSummaryIntervalSeconds = Math.Clamp(psi.GetValue<int>(), 0, 86400);

            // ---- 表情包 ----
            if (body["enableVoice"] is JsonNode ev) s.EnableVoice = ev.GetValue<bool>();
        if (body["voiceName"] is JsonNode vn) s.VoiceName = vn.GetValue<string>().Trim();
        if (body["voiceSpeed"] is JsonNode vs) s.VoiceSpeed = Math.Clamp(vs.GetValue<int>(), 50, 200);
        if (body["voiceMaxChars"] is JsonNode vmc) s.VoiceMaxChars = Math.Clamp(vmc.GetValue<int>(), 10, 300);
        if (body["ttsServiceUrl"] is JsonNode tts) s.TtsServiceUrl = tts.GetValue<string>().Trim();
        if (body["enableWebSearch"] is JsonNode ws) s.EnableWebSearch = ws.GetValue<bool>();
        if (body["webSearchUseModelSearch"] is JsonNode wsm) s.WebSearchUseModelSearch = wsm.GetValue<bool>();
        if (body["webSearchSources"] is JsonNode wss) s.WebSearchSources = wss.GetValue<string>().Trim();
        if (body["webSearchMaxResults"] is JsonNode wsr) s.WebSearchMaxResults = Math.Clamp(wsr.GetValue<int>(), 1, 10);
        if (body["webSearchCooldownSeconds"] is JsonNode wsc) s.WebSearchCooldownSeconds = Math.Clamp(wsc.GetValue<int>(), 0, 86400);
        if (body["webSearchTimeoutSeconds"] is JsonNode wst) s.WebSearchTimeoutSeconds = Math.Clamp(wst.GetValue<int>(), 5, 60);
        if (body["enableLinkPreview"] is JsonNode elp) s.EnableLinkPreview = elp.GetValue<bool>();
        if (body["linkPreviewTimeoutSeconds"] is JsonNode lpt) s.LinkPreviewTimeoutSeconds = Math.Clamp(lpt.GetValue<int>(), 2, 30);
        if (body["linkPreviewMax"] is JsonNode lpm) s.LinkPreviewMax = Math.Clamp(lpm.GetValue<int>(), 0, 5);
        if (body["enableMusic"] is JsonNode em) s.EnableMusic = em.GetValue<bool>();
        if (body["neteaseBaseUrl"] is JsonNode nbu) s.NeteaseBaseUrl = nbu.GetValue<string>().Trim();
        if (body["musicSources"] is JsonNode ms) s.MusicSources = ms.GetValue<string>();
        if (body["musicBitrate"] is JsonNode mb) s.MusicBitrate = Math.Clamp(mb.GetValue<int>(), 32, 320);
        if (body["musicMaxDownloadMb"] is JsonNode mmd) s.MusicMaxDownloadMb = Math.Clamp(mmd.GetValue<int>(), 1, 64);
        if (body["musicMaxAnalysisSeconds"] is JsonNode mma) s.MusicMaxAnalysisSeconds = Math.Clamp(mma.GetValue<int>(), 20, 600);
        if (body["musicLibraryMax"] is JsonNode mml) s.MusicLibraryMax = Math.Clamp(mml.GetValue<int>(), 10, 5000);
        if (body["musicNoteTtlDays"] is JsonNode mnt) s.MusicNoteTtlDays = Math.Clamp(mnt.GetValue<int>(), 1, 365);
        if (body["musicListenCooldownSeconds"] is JsonNode mlc) s.MusicListenCooldownSeconds = Math.Clamp(mlc.GetValue<int>(), 0, 86400);
        if (body["musicUnderstandModel"] is JsonNode mum) s.MusicUnderstandModel = mum.GetValue<string>().Trim();
        if (body["musicSendAudioToModel"] is JsonNode msa) s.MusicSendAudioToModel = msa.GetValue<bool>();
        if (body["musicKeepAudio"] is JsonNode mka) s.MusicKeepAudio = mka.GetValue<bool>();
        if (body["enableStickers"] is JsonNode es) s.EnableStickers = es.GetValue<bool>();
            if (body["stickerLibraryMax"] is JsonNode slm) s.StickerLibraryMax = Math.Clamp(slm.GetValue<int>(), 0, 2000);
            if (body["stickerCandidates"] is JsonNode sc) s.StickerCandidates = Math.Clamp(sc.GetValue<int>(), 0, 20);
            if (body["stickerCurateIntervalSeconds"] is JsonNode sci) s.StickerCurateIntervalSeconds = Math.Clamp(sci.GetValue<int>(), 0, 86400);
            if (body["stickerCooldownSeconds"] is JsonNode scl) s.StickerCooldownSeconds = Math.Clamp(scl.GetValue<int>(), 0, 86400);
        if (body["enablePoke"] is JsonNode ep) s.EnablePoke = ep.GetValue<bool>();
        if (body["pokeCooldownSeconds"] is JsonNode pcl) s.PokeCooldownSeconds = Math.Clamp(pcl.GetValue<int>(), 0, 86400);
        if (body["moodTtlSeconds"] is JsonNode mtt) s.MoodTtlSeconds = Math.Clamp(mtt.GetValue<int>(), 0, 86400 * 7);

        // ---- 模型接口（面板可改；留空 = 回退到环境变量）----
        if (newBaseUrl is not null)
        {
            s.ModelBaseUrlOverride = newBaseUrl.Length == 0 ? null : newBaseUrl;
            s.ModelBaseUrl = newBaseUrl.Length > 0
                ? newBaseUrl
                : Environment.GetEnvironmentVariable("QQCHAT_BASE_URL") is { Length: > 0 } envUrl
                    ? envUrl.Trim()
                    : "https://api.openai.com/v1";
        }

        if (body["model"] is JsonNode modelNode)
        {
            var modelName = (modelNode.GetValue<string>() ?? string.Empty).Trim();
            s.ModelOverride = modelName.Length == 0 ? null : modelName;
            s.Model = modelName.Length > 0
                ? modelName
                : Environment.GetEnvironmentVariable("QQCHAT_MODEL") is { Length: > 0 } envModel
                    ? envModel.Trim()
                    : "gpt-4o-mini";
        }

        // 密钥：存 data/secrets.json（权限 600），**不写 settings.json**；留空 = 删掉、回退环境变量
        if (body["apiKey"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var rawKey))
        {
            var newKey = (rawKey ?? string.Empty).Trim();
            if (newKey.Length == 0)
            {
                SecretsStore.SaveApiKey(null);
                s.ApiKeyOverride = null;
                s.ApiKey = (Environment.GetEnvironmentVariable("QQCHAT_API_KEY") ??
                            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty).Trim();
            }
            else
            {
                SecretsStore.SaveApiKey(newKey);
                s.ApiKeyOverride = newKey;
                s.ApiKey = newKey;
            }

            // 密钥只记“变了”，绝不回显明文（日志会被贴出来排障）
            FileLog.Write("Web", newKey.Length == 0 ? "面板清空了模型 API Key（回退环境变量）" : "面板更新了模型 API Key（已掩码保存）");
        }

        // 心情是机器人状态（不是行为配置），单独走 agent：留空 = 交回自动描述
        if (body["mood"] is JsonValue moodValue && moodValue.TryGetValue<string>(out var moodText))
        {
            _agent.SetMood(moodText);
        }
        });

        // 模型配置改完要让客户端也看到（同一个 AppSettings 实例，这里只是显式同步一次）
        _agent.SyncModelSettings();

        await WriteJsonAsync(context, 200, BuildSettingsPayload());
    }

    // ══════════════ SSE ══════════════

    private async Task HandleSseAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.SendChunked = true;

        var client = new SseClient(response.OutputStream);
        lock (_clientsGate)
        {
            _clients.Add(client);
        }

        try
        {
            // 首帧：完整状态，前端据此渲染
            await client.SendAsync("state", BuildState().ToJsonString(Json));

            // 心跳，避免代理/浏览器掐断空闲连接
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(15000, ct);
                await client.SendCommentAsync("ping");
            }
        }
        catch
        {
            // 客户端断开
        }
        finally
        {
            lock (_clientsGate)
            {
                _clients.Remove(client);
            }

            try
            {
                response.Close();
            }
            catch
            {
                // 忽略
            }
        }
    }

    private void Broadcast(string @event, JsonNode payload)
    {
        var json = payload.ToJsonString(Json);
        List<SseClient> targets;
        lock (_clientsGate)
        {
            targets = _clients.ToList();
        }

        foreach (var client in targets)
        {
            _ = client.SendAsync(@event, json).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    lock (_clientsGate)
                    {
                        _clients.Remove(client);
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    private void OnMessageAdded(string key, ChatMessage message)
        => Broadcast("message", new JsonObject
        {
            ["key"] = key,
            ["message"] = ToMessageDto(message)
        });

    private void OnConversationsChanged()
    {
        // 每条消息都会触发这个事件，而它要重建**全部**会话的 JSON。
        // 忙群（每秒几条）下会把 CPU 和 SSE 带宽吃掉 —— 所以合并：
        // 窗口内只广播一次，窗口结束后若还有新变更再补一次，保证最后状态不会丢。
        Interlocked.Increment(ref _convBroadcastVersion);

        if (Interlocked.Exchange(ref _convBroadcastPending, 1) == 1)
        {
            return;
        }

        _ = BroadcastConversationsLoopAsync();
    }

    private async Task BroadcastConversationsLoopAsync()
    {
        try
        {
            while (true)
            {
                var version = Interlocked.Read(ref _convBroadcastVersion);

                await Task.Delay(250);

                Broadcast("conversations", new JsonObject
                {
                    ["conversations"] = new JsonArray(BuildConversations().Select(c => (JsonNode)c).ToArray())
                });

                Interlocked.Exchange(ref _convBroadcastPending, 0);

                if (Interlocked.Read(ref _convBroadcastVersion) == version)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _convBroadcastPending, 1) == 1)
                {
                    return;
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _convBroadcastPending, 0);
        }
    }

    private void OnStateChanged()
        => Broadcast("state", BuildState());

    private void OnThinkingChanged(string key)
    {
        var conversation = _agent.Find(key);
        Broadcast("thinking", new JsonObject
        {
            ["key"] = key,
            ["thinking"] = conversation?.Thinking ?? false
        });
    }

    private void OnLogLine(string message)
        => Broadcast("log", new JsonObject
        {
            ["time"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ["text"] = message
        });

    // ══════════════ 数据组装 ══════════════

    private JsonObject BuildStatus() => new()
    {
        ["status"] = "running",
        ["uptimeSeconds"] = (int)(DateTimeOffset.Now - _startedAt).TotalSeconds,
        ["startedAt"] = _startedAt.ToString("O"),
        ["onebot"] = new JsonObject
        {
            ["connected"] = _gateway.IsConnected,
            ["protocol"] = _settings.OneBotProtocol,
            ["address"] = _settings.OneBotAddress
        },
        ["account"] = new JsonObject
        {
            ["uin"] = _settings.NormalizedUin,
            ["selfId"] = _agent.SelfId,
            // true/false = 已探明；null = 未知（协议端未实现 get_status）
            // 注意：这是“QQ 账号在不在线”，与上面的 onebot.connected 不是一回事
            ["online"] = _agent.AccountOnline
        },
        ["login"] = new JsonObject
        {
            // 面板靠这两项决定「扫码卡片」里是显示二维码还是显示原因
            ["qrAvailable"] = _loginQr.Configured,
            ["napcatWebUi"] = _loginQr.WebUiDisplay
        },
        ["agent"] = new JsonObject
        {
            ["enabled"] = _settings.AiModeEnabled,
            ["model"] = _settings.Model,
            ["desire"] = _settings.AiDesire,
            ["personaConfigured"] = !string.IsNullOrWhiteSpace(_settings.BotPersona)
        },
        ["conversations"] = _agent.Conversations.Count,
        ["inFlightReplies"] = _agent.InFlightReplies,
        ["queuedReplies"] = _agent.QueuedReplies,
        ["profileSummaries"] = _agent.SummaryDoneCount,
        ["profileSummaryFailures"] = _agent.SummaryFailCount,
        ["stickers"] = _agent.Stickers.Count,
        ["stickersDescribed"] = _agent.Stickers.DescribedCount,
        ["stickersPending"] = _agent.StickerPendingDescribe
    };

    private JsonObject BuildState() => new()
    {
        ["status"] = BuildStatus(),
        ["aiMode"] = _settings.AiModeEnabled,
        ["conversations"] = new JsonArray(BuildConversations().Select(c => (JsonNode)c).ToArray()),
        ["serverTime"] = DateTimeOffset.Now.ToUnixTimeMilliseconds()
    };

    private List<JsonObject> BuildConversations()
    {
        var list = new List<JsonObject>();
        foreach (var c in _agent.Conversations)
        {
            var (isGroup, id) = c.Target;
            list.Add(new JsonObject
            {
                ["key"] = c.SourceKey,
                ["kind"] = isGroup ? "Group" : "Private",
                ["name"] = c.Name,
                ["id"] = id,
                ["avatarUrl"] = BuildAvatarUrl(isGroup, id),
                ["avatarText"] = FirstChar(c.Name),
                ["unread"] = c.Unread,
                ["thinking"] = c.Thinking,
                ["preview"] = c.Preview,
                ["messageCount"] = c.MessageCount,
                ["lastTime"] = c.LastTime.ToUnixTimeMilliseconds()
            });
        }

        return list;
    }

    private static JsonObject ToMessageDto(ChatMessage m) => new()
    {
        ["seq"] = m.Seq,
        ["role"] = m.Role switch
        {
            MessageRole.Self => "Self",
            MessageRole.System => "System",
            _ => "Peer"
        },
        ["text"] = m.Text,
        ["senderName"] = m.SenderName,
        ["senderId"] = m.SenderId,
        ["time"] = m.Timestamp.ToUnixTimeMilliseconds(),
        ["images"] = m.ImageUrls is { Count: > 0 }
            ? new JsonArray(m.ImageUrls.Select(u => (JsonNode)u!).ToArray())
            : null,
        ["qqMessageId"] = m.QqMessageId,
        // 已撤回的消息：面板把它划掉并注明“模型看到的是 [已撤回]”
        ["recalled"] = m.Recalled ? true : null
    };

    /// <summary>QQ 头像：群 p.qlogo.cn/gh/{群号}/{群号}/0；用户 q1.qlogo.cn/g?b=qq&amp;nk={QQ}&amp;s=640。</summary>
    private static string BuildAvatarUrl(bool isGroup, long id) => isGroup
        ? $"https://p.qlogo.cn/gh/{id}/{id}/0"
        : $"https://q1.qlogo.cn/g?b=qq&nk={id}&s=640";

    private static string FirstChar(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "?" : trimmed[..1];
    }

    private JsonObject BuildSettingsPayload()
    {
        var s = _settings;

        return new JsonObject
        {
            ["runtime"] = new JsonObject
            {
                ["botPersona"] = s.BotPersona,
                ["messageWhitelist"] = s.MessageWhitelist,
                ["aiDesire"] = s.AiDesire,
                ["suitabilityThreshold"] = s.SuitabilityThreshold,
                ["aiModeEnabled"] = s.AiModeEnabled,
                ["maxTokens"] = s.MaxTokens,
                ["groupCooldownSeconds"] = s.GroupCooldownSeconds,
                ["privateCooldownSeconds"] = s.PrivateCooldownSeconds,
                ["idleFallbackSeconds"] = s.IdleFallbackSeconds,
                ["splitReplies"] = s.SplitReplies,
                ["ignoreBracketMessages"] = s.IgnoreBracketMessages,
                ["segmentDelayMs"] = s.SegmentDelayMs,
                ["maxContextMessages"] = s.MaxContextMessages,
                ["profileLookupCount"] = s.ProfileLookupCount,
                ["profileSummaryLines"] = s.ProfileSummaryLines,
                ["maxProfileChars"] = s.MaxProfileChars,
                ["maxMessagesPerConversation"] = s.MaxMessagesPerConversation,
                ["maxConcurrentReplies"] = s.MaxConcurrentReplies,
                ["enableProfileSummary"] = s.EnableProfileSummary,
                ["profileSummaryThreshold"] = s.ProfileSummaryThreshold,
                ["profileSummaryMaxChars"] = s.ProfileSummaryMaxChars,
                ["profileSummaryIntervalSeconds"] = s.ProfileSummaryIntervalSeconds,
                ["enableVoice"] = s.EnableVoice,
        ["voiceName"] = s.VoiceName,
        ["voiceSpeed"] = s.VoiceSpeed,
        ["voiceMaxChars"] = s.VoiceMaxChars,
        ["ttsServiceUrl"] = s.TtsServiceUrl,
         ["enableWebSearch"] = s.EnableWebSearch,
         ["webSearchUseModelSearch"] = s.WebSearchUseModelSearch,
         ["webSearchSources"] = s.WebSearchSources,
         ["webSearchMaxResults"] = s.WebSearchMaxResults,
         ["webSearchCooldownSeconds"] = s.WebSearchCooldownSeconds,
         ["webSearchTimeoutSeconds"] = s.WebSearchTimeoutSeconds,
        ["enableLinkPreview"] = s.EnableLinkPreview,
        ["linkPreviewTimeoutSeconds"] = s.LinkPreviewTimeoutSeconds,
        ["linkPreviewMax"] = s.LinkPreviewMax,
        ["enableMusic"] = s.EnableMusic,
        ["neteaseBaseUrl"] = s.NeteaseBaseUrl,
        ["musicSources"] = s.MusicSources,
        ["musicBitrate"] = s.MusicBitrate,
        ["musicMaxDownloadMb"] = s.MusicMaxDownloadMb,
        ["musicMaxAnalysisSeconds"] = s.MusicMaxAnalysisSeconds,
        ["musicLibraryMax"] = s.MusicLibraryMax,
        ["musicNoteTtlDays"] = s.MusicNoteTtlDays,
        ["musicListenCooldownSeconds"] = s.MusicListenCooldownSeconds,
        ["musicUnderstandModel"] = s.MusicUnderstandModel,
        ["musicSendAudioToModel"] = s.MusicSendAudioToModel,
        ["musicAudioToModelMaxKb"] = s.MusicAudioToModelMaxKb,
        ["musicKeepAudio"] = s.MusicKeepAudio,
        ["neteaseCookieSet"] = !string.IsNullOrWhiteSpace(s.NeteaseCookie),
        ["enableStickers"] = s.EnableStickers,
                ["stickerLibraryMax"] = s.StickerLibraryMax,
                ["stickerCandidates"] = s.StickerCandidates,
                ["stickerCurateIntervalSeconds"] = s.StickerCurateIntervalSeconds,
                ["stickerCooldownSeconds"] = s.StickerCooldownSeconds,
        ["enablePoke"] = s.EnablePoke,
        ["pokeCooldownSeconds"] = s.PokeCooldownSeconds,
        ["moodTtlSeconds"] = s.MoodTtlSeconds,
        // 当前心情（可手改；空 = 由代码按被戳次数自动描述）
        ["mood"] = _agent.MoodText,
        ["moodSummary"] = _agent.MoodSummary
            },
            // 只读：容器环境变量职责，改这里无效
            ["env"] = new JsonObject
            {
                ["modelBaseUrl"] = s.ModelBaseUrl,
                ["modelBaseUrlSource"] = string.IsNullOrWhiteSpace(s.ModelBaseUrlOverride) ? "env" : "panel",
                ["model"] = s.Model,
                ["modelSource"] = string.IsNullOrWhiteSpace(s.ModelOverride) ? "env" : "panel",
                ["maxTokens"] = s.MaxTokens,
                ["apiKeyMasked"] = Mask(s.ApiKey),
                ["apiKeySet"] = !string.IsNullOrWhiteSpace(s.ApiKey),
                ["apiKeySource"] = !string.IsNullOrWhiteSpace(s.ApiKeyOverride) ? "panel" : (string.IsNullOrWhiteSpace(s.ApiKey) ? "none" : "env"),
                ["oneBotProtocol"] = s.OneBotProtocol,
                ["oneBotAddress"] = s.OneBotAddress,
                ["oneBotTokenMasked"] = Mask(s.OneBotToken),
                ["uin"] = s.NormalizedUin,
                ["dataDir"] = AppPaths.RuntimeRoot,
                ["healthPort"] = s.HealthPort,
                ["tz"] = Environment.GetEnvironmentVariable("TZ") ?? TimeZoneInfo.Local.Id
            },
            ["settingsFile"] = SettingsStore.FilePath
        };
    }

    private static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        return secret.Length <= 4 ? "****" : secret[..4] + "****";
    }

    // ══════════════ 基础 IO ══════════════

    /// <summary>
    /// 读取某会话的归档（已溢出滚动窗口的旧消息）。
    /// 现在归档在 SQLite 里（messages 表 archived=1），不再是 archive/*.jsonl 文件；
    /// 返回给面板的字段保持与老版一致（t/role/sender/uid/mid/text），前端不用改。
    /// </summary>
    private JsonObject ReadArchive(string sourceKey, int limit)
    {
        var result = new JsonObject
        {
            ["key"] = sourceKey,
            ["messages"] = new JsonArray(),
            ["totalLines"] = 0
        };

        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            result["error"] = "缺少 key";
            return result;
        }

        try
        {
            var rows = _agent.ReadArchive(sourceKey, limit);
            var array = new JsonArray();
            foreach (var m in rows)
            {
                array.Add(new JsonObject
                {
                    ["t"] = m.TimeUnix,
                    ["role"] = m.Role,
                    ["sender"] = m.SenderName,
                    ["uid"] = m.SenderId,
                    ["mid"] = m.QqMessageId,
                    ["text"] = m.Text
                });
            }

            result["messages"] = array;
            result["totalLines"] = rows.Count;
            if (rows.Count == 0)
            {
                result["error"] = "该会话尚无归档";
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
        }

        return result;
    }

    // ══════════════ 表情包库 ══════════════

    /// <summary>
    /// 表情包库接口。库是全库共用一份（不分会话）：
    ///   GET  /api/stickers                 列表（含 id、说明、关键词、用过几次）
    ///   GET  /api/stickers/{id}/img        图片本体（面板缩略图用）
    ///   POST /api/stickers/{id}/delete     删除一张（面板手动）
    ///   POST /api/stickers/curate          立即让机器人巡检一遍（自己决定删哪些）
    ///   POST /api/stickers/import          从 QQ 收藏表情导入（机器人自己“添加”）
    /// </summary>
    private async Task HandleStickersAsync(HttpListenerContext context, string path, string method)
    {
        var store = _agent.Stickers;
        var rest = path.Length > "/api/stickers".Length ? path["/api/stickers/".Length..] : string.Empty;

        if (rest.Length == 0)
        {
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["enabled"] = _settings.EnableStickers,
                ["max"] = _settings.StickerLibraryMax,
                ["candidates"] = _settings.StickerCandidates,
                ["curateIntervalSeconds"] = _settings.StickerCurateIntervalSeconds,
                ["count"] = store.Count,
                ["described"] = store.DescribedCount,
                ["pendingDescribe"] = _agent.StickerPendingDescribe,
                ["describeDone"] = _agent.StickerDescribeDone,
                ["items"] = new JsonArray(store.Snapshot()
                    .OrderByDescending(s => s.AddedAt)
                    .Select(s => (JsonNode)new JsonObject
                    {
                        ["id"] = s.Id,
                        ["desc"] = s.Desc,
                        ["tags"] = new JsonArray(s.Tags.Select(t => (JsonNode)t).ToArray()),
                        ["uses"] = s.Uses,
                        ["bytes"] = s.Bytes,
                        ["ext"] = s.Ext,
                        ["addedAt"] = s.AddedAt * 1000,
                        ["lastUsedAt"] = s.LastUsedAt * 1000,
                        ["fromUid"] = s.FromUid,
                        ["fromGroup"] = s.FromGroup,
                        ["described"] = s.Described,
                        // true/false = 模型审过“是不是表情包”；null = 还没审（未审的不会被发出去）
                        ["isSticker"] = s.IsSticker
                    })
                    .ToArray())
            });
            return;
        }

        var parts = rest.Split('/', 2);
        var id = Uri.UnescapeDataString(parts[0]);

        // 图片本体：<img> 不能带自定义请求头，所以只能靠 ?token=（与 SSE 同一套约定）
        if (parts.Length == 2 && parts[1].Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            var record = store.Find(id);
            if (record is null || !File.Exists(record.AbsolutePath))
            {
                await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "sticker not found" });
                return;
            }

            var bytes = await File.ReadAllBytesAsync(record.AbsolutePath);
            var type = record.Ext switch
            {
                "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                _ => "image/png"
            };
            context.Response.Headers["Cache-Control"] = "no-store";
            await WriteBytesAsync(context, 200, type, bytes);
            return;
        }

        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        // 两段形式：/api/stickers/{id}/delete
        // 注意：单段形式（/api/stickers/curate）下 parts 只有一个元素，
        // 直接读 parts[1] 会抛 IndexOutOfRange（之前就是这么把 curate/import 打挂的）。
        if (parts.Length == 2 && parts[1].Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            var ok = store.Remove(id, "面板手动删除");
            await WriteJsonAsync(context, ok ? 200 : 404, new JsonObject
            {
                ["ok"] = ok,
                ["count"] = store.Count
            });
            return;
        }

        // 单段形式：/api/stickers/curate 或 /api/stickers/import
        if (parts.Length == 1)
        {
            switch (id.ToLowerInvariant())
            {
                case "curate":
                    // 别让面板等模型：先回一句，跑完往面板推日志
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            FileLog.Write("Sticker", await _agent.CurateStickersAsync(force: true));
                        }
                        catch (Exception ex)
                        {
                            FileLog.Warn("Sticker", $"手动巡检失败：{ex.Message}");
                        }
                    });
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["started"] = true });
                    return;

                case "import":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            FileLog.Write("Sticker", await _agent.ImportStickersFromAlbumAsync());
                        }
                        catch (Exception ex)
                        {
                            FileLog.Warn("Sticker", $"导入收藏表情失败：{ex.Message}");
                        }
                    });
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["started"] = true });
                    return;
            }
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "not found", ["path"] = path });
    }

    private static bool IsTruthy(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Equals("1", StringComparison.Ordinal) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>读取嵌入的静态资源（wwwroot/*，LogicalName = web/文件名）。</summary>
    private static async Task WriteAssetAsync(HttpListenerContext context, string fileName, string contentType)
    {
        // 面板是会随版本迭代的，禁止浏览器缓存，否则改了界面看不到
        context.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";

        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
                                 n.Equals("web/" + fileName, StringComparison.OrdinalIgnoreCase));

        if (resource is null)
        {
            var message = Encoding.UTF8.GetBytes($"asset not found: {fileName}");
            await WriteBytesAsync(context, 404, "text/plain; charset=utf-8", message);
            return;
        }

        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        await WriteBytesAsync(context, 200, contentType, buffer.ToArray());
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, JsonNode payload)
        => await WriteBytesAsync(context, status, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(payload.ToJsonString(Json)));

    private static async Task WriteBytesAsync(HttpListenerContext context, int status, string contentType, byte[] bytes)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        if (bytes.Length > 0)
        {
            await context.Response.OutputStream.WriteAsync(bytes);
        }
    }

    /// <summary>
    /// 面板内的网易云扫码登录（代理到自建 API 的 /login/qr/*）。
    /// 为什么放在面板里：手机号/密码登录要暴露账号密码，扫码最干净；
    /// 而且登录态是存在自建 API 那边的，面板只是把二维码拿过来展示、帮忙轮询。
    /// </summary>
    private async Task HandleNeteaseQrAsync(HttpListenerContext context, string path, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        var baseUrl = _settings.NeteaseBaseUrl.TrimEnd('/');
        var stamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        try
        {
            if (path.EndsWith("/check", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadJsonAsync(context);
                var key = body?["key"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "缺少 key" });
                    return;
                }

                var check = await GetJsonFromAsync($"{baseUrl}/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={stamp}");

                // 扫码成功（803）时把登录态存下来：上游会在这条响应里给 cookie。
                // 为什么必须存：cookie 本来只活在自建 API 容器的进程内存里 ——
                // 容器一重建（升级镜像 / compose up 重创）就得重新扫码，号主反馈的“老是掉登录”就是这个。
                // 存进库（secrets 表，权限 600）之后，每轮请求直接带 cookie（见 NeteaseMusicClient），
                // 与那个容器活着不活着无关；重启机器人也不会丢。
                if (TryReadInt(check?["code"]) == 803 &&
                    check?["cookie"] is JsonValue cookieValue && cookieValue.TryGetValue<string>(out var freshCookie) &&
                    !string.IsNullOrWhiteSpace(freshCookie))
                {
                    var saved = SecretsStore.SaveNeteaseCookie(freshCookie);
                    _settings.NeteaseCookie = freshCookie;   // 立即生效（音乐客户端每轮现读）
                    check["saved"] = saved;
                    FileLog.Write("Music", saved
                        ? $"网易云扫码登录成功，登录态已存进库里（{freshCookie.Length} 字，重启/重建容器都不丢）"
                        : "网易云扫码登录成功，但登录态落盘失败（仍会用在本次进程内）");
                }

                await WriteJsonAsync(context, 200, check ?? new JsonObject { ["error"] = "上游无响应" });
                return;
            }

            /// <summary>宽容地读一个整数（上游有时给字符串 "803"，不确定就别让它把整条链路弄挂）。</summary>
            static int? TryReadInt(JsonNode? node)
            {
                if (node is not JsonValue value)
                {
                    return null;
                }

                if (value.TryGetValue<int>(out var number))
                {
                    return number;
                }

                return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
            }

            // 两步：先拿 key，再让上游生成二维码（qrimg=true 直接回 base64 图）
            var keyJson = await GetJsonFromAsync($"{baseUrl}/login/qr/key?timestamp={stamp}");
            var unikey = keyJson?["data"]?["unikey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(unikey))
            {
                await WriteJsonAsync(context, 502, new JsonObject
                {
                    ["error"] = "拿不到二维码 key（自建网易云接口不可用？）",
                    ["detail"] = keyJson?.ToJsonString() ?? "(无响应)"
                });
                return;
            }

            var qrJson = await GetJsonFromAsync($"{baseUrl}/login/qr/create?key={Uri.EscapeDataString(unikey)}&qrimg=true&timestamp={stamp}");
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["key"] = unikey,
                ["qrimg"] = qrJson?["data"]?["qrimg"]?.GetValue<string>() ?? string.Empty,
                ["qrurl"] = qrJson?["data"]?["qrurl"]?.GetValue<string>() ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    /// <summary>面板登录流程用的 HttpClient（打自建网易云接口；超时短一点，别拖住面板）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>向自建网易云接口发一个 GET 并解析 JSON（仅面板登录流程用）。</summary>
    private static async Task<JsonNode?> GetJsonFromAsync(string url)
    {
        using var resp = await Http.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        try
        {
            return JsonNode.Parse(text);
        }
        catch (Exception)
        {
            return new JsonObject { ["raw"] = text.Length > 300 ? text[..300] : text };
        }
    }

    /// <summary>/api/music/test：把“听音乐”链路真跑一遍（搜歌 → 歌词 → 低码率音源 → 波形分析）。</summary>
    private async Task HandleMusicTestAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        JsonNode? body;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请求体不是合法 JSON" });
            return;
        }

        var song = body?["song"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(song) || song.Length > 60)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 song（歌名，可带歌手，≤ 60 字）" });
            return;
        }

        try
        {
            var note = await _agent.TestMusicAsync(song, CancellationToken.None);
            var song2 = _agent.LastMusicTestHeader;
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = note is not null,
                ["song"] = song2,
                ["note"] = note ?? "没搜到，或这首歌没拿到音源且没有歌词"
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    /// <summary>
    /// /api/voice/test：真合成一句语音（走 TTS 容器 /speak），直接把 wav 字节还给浏览器播。
    /// 为什么返回二进制而不是 JSON+base64：浏览器直接 Blob 播放最省事，也不白扛 33% 的 base64 开销。
    /// 音色/语速可以带参数（面板改了还没保存也能试听）；服务地址一律用已保存的设置 ——
    /// 面板不足以成为“拿任意 URL 去访问”的入口（跟白名单/密码一个道理）。
    /// </summary>
    private async Task HandleVoiceTestAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        JsonNode? body;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请求体不是合法 JSON" });
            return;
        }

        var text = body?["text"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 text（要说的一句话）" });
            return;
        }

        if (text.Length > 300)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = $"文本太长（{text.Length} > 300），长文本请改用文字" });
            return;
        }

        var voice = body?["voice"]?.GetValue<string>()?.Trim();
        var speed = body?["speed"] is JsonNode sp && int.TryParse(sp.ToString(), out var parsedSpeed) ? parsedSpeed : (int?)null;

        var (data, error) = await _agent.TestVoiceAsync(text, voice, speed, CancellationToken.None);
        if (data is null)
        {
            await WriteJsonAsync(context, 502, new JsonObject { ["error"] = error ?? "合成失败" });
            return;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        await WriteBytesAsync(context, 200, "audio/wav", data);
    }

    /// <summary>/api/voice/health：把 TTS 服务自己的 /health 透传给面板（活着吗、有哪些音色）。</summary>
    private async Task HandleVoiceHealthAsync(HttpListenerContext context)
    {
        var voice = _agent.Voice;
        if (voice is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject { ["ok"] = false, ["error"] = "语音服务还没初始化" });
            return;
        }

        var (ok, payload, error) = await voice.HealthAsync(CancellationToken.None);
        await WriteJsonAsync(context, ok ? 200 : 502, new JsonObject
        {
            ["ok"] = ok,
            ["url"] = voice.BaseUrl,
            ["currentVoice"] = voice.VoiceName,
            ["voices"] = payload?["voices"]?.DeepClone() ?? new JsonArray(),
            ["default"] = payload?["default"]?.ToString(),
            ["error"] = error
        });
    }

    /// <summary>
    /// /api/search/test：跑一次真实联网搜索（模型自带搜索优先，否则走搜索源模板）。
    /// 为什么要这个入口：搜索能不能用跟“服务器 IP、代理支不支持工具”强相关，
    /// 面板上当场跑一次，比在群里碰运气强。
    /// </summary>
    private async Task HandleSearchTestAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        JsonNode? body;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请求体不是合法 JSON" });
            return;
        }

        var query = body?["query"]?.GetValue<string>()?.Trim();
        var url = body?["url"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(url))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 query（搜索词）或 url（要读的页面）" });
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                var (text, error) = await _agent.TestReadPageAsync(url!, CancellationToken.None);
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["ok"] = text is not null,
                    ["mode"] = "read",
                    ["text"] = text,
                    ["error"] = error
                });
                return;
            }

            var result = await _agent.TestSearchAsync(query!, CancellationToken.None);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = result.HasContent,
                ["mode"] = "search",
                ["provider"] = result.Provider,
                ["answer"] = result.Answer,
                ["hits"] = new JsonArray(result.Hits
                    .Select(h => (JsonNode)new JsonObject
                    {
                        ["title"] = h.Title,
                        ["url"] = h.Url,
                        ["snippet"] = h.Snippet
                    })
                    .ToArray()),
                ["note"] = result.Describe(),
                ["error"] = result.Error
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _agent.MessageAdded -= OnMessageAdded;
        _agent.ConversationsChanged -= OnConversationsChanged;
        _agent.StateChanged -= OnStateChanged;
        _agent.ThinkingChanged -= OnThinkingChanged;
        _agent.LogLine -= OnLogLine;

        _cts.Cancel();
        lock (_clientsGate)
        {
            foreach (var c in _clients)
            {
                c.Dispose();
            }

            _clients.Clear();
        }

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // 忽略
        }

        _ = _loop;
    }

    /// <summary>一个 SSE 订阅者。</summary>
    private sealed class SseClient : IDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public SseClient(Stream stream) => _stream = stream;

        public async Task SendAsync(string @event, string data)
            => await WriteRawAsync($"event: {@event}\ndata: {data}\n\n");

        public async Task SendCommentAsync(string comment) => await WriteRawAsync($": {comment}\n\n");

        private async Task WriteRawAsync(string payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _lock.WaitAsync();
            try
            {
                await _stream.WriteAsync(bytes);
                await _stream.FlushAsync();
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose()
        {
            try
            {
                _stream.Dispose();
            }
            catch
            {
                // 忽略
            }

            _lock.Dispose();
        }
    }
}
