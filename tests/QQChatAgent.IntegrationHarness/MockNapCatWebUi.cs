using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// 假 NapCat WebUI。只实现面板扫码登录用到的三个端点，但**严格校验请求** ——
/// 目的就是钉住与 NapCat 的真实契约，而不是我们自己封装的形状：
///   POST /api/auth/login              body.hash 必须等于 SHA256(token + ".napcat")（小写十六进制）
///   POST /api/QQLogin/GetQQLoginQrcode  必须带 Authorization: Bearer &lt;登录拿到的 Credential&gt;
///   POST /api/QQLogin/RefreshQrcode     同上；调用后换一张新二维码
/// 任何一处不合约，机器人拿到的就是失败的 JSON，测试随之失败。
/// </summary>
public sealed class MockNapCatWebUi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly object _gate = new();
    private readonly List<string> _loginHashes = new();
    private readonly List<string> _calls = new();
    private readonly List<string> _badCredentials = new();
    private readonly string _credential = "cred-" + Guid.NewGuid().ToString("N")[..10];
    private readonly int _port;

    private string _qrcodeUrl = "https://txz.qq.com/p?k=INITIAL-KEY&f=1600001615";
    private int _rotate;

    public MockNapCatWebUi(int port)
    {
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>WebUI 令牌（对应 napcat/config/webui.json 的 token）。</summary>
    public string WebUiToken { get; init; } = "napcat-webui-token";

    /// <summary>当前二维码内容（txz.qq.com 短链）。</summary>
    public string CurrentQrUrl
    {
        get
        {
            lock (_gate)
            {
                return _qrcodeUrl;
            }
        }
    }

    /// <summary>收到的登录哈希（用于断言哈希算法）。</summary>
    public IReadOnlyList<string> LoginHashes
    {
        get
        {
            lock (_gate)
            {
                return _loginHashes.ToArray();
            }
        }
    }

    /// <summary>收到的调用路径（用于断言确实调了刷新）。</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>用错令牌的请求（应为空）。</summary>
    public IReadOnlyList<string> BadCredentials
    {
        get
        {
            lock (_gate)
            {
                return _badCredentials.ToArray();
            }
        }
    }

    /// <summary>让下一次需要鉴权的调用返回 Unauthorized（检验机器人的自动重登录）。</summary>
    public bool UnauthorizedOnce { get; set; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        lock (_gate)
        {
            _calls.Add(path);
        }

        JsonObject response;
        switch (path)
        {
            case "/api/auth/login":
                response = Login(body);
                break;

            case "/api/QQLogin/GetQQLoginQrcode":
                response = Authorized(context.Request) is { } guard
                    ? guard
                    : new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["qrcode"] = CurrentQrUrl }, ["message"] = "success" };
                break;

            case "/api/QQLogin/RefreshQrcode":
                var unauthorized = Authorized(context.Request);
                if (unauthorized is not null)
                {
                    response = unauthorized;
                    break;
                }

                lock (_gate)
                {
                    _rotate++;
                    _qrcodeUrl = $"https://txz.qq.com/p?k=ROTATED-{_rotate}&f=1600001615";
                }

                response = new JsonObject { ["code"] = 0, ["data"] = null, ["message"] = "success" };
                break;

            default:
                response = new JsonObject { ["code"] = -1, ["message"] = $"未知接口 {path}" };
                break;
        }

        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private JsonObject Login(string body)
    {
        string hash = string.Empty;
        try
        {
            hash = (JsonNode.Parse(body) as JsonObject)?["hash"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            // 非 JSON：按空哈希处理
        }

        lock (_gate)
        {
            _loginHashes.Add(hash);
        }

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WebUiToken + ".napcat")))
            .ToLowerInvariant();

        return string.Equals(hash, expected, StringComparison.Ordinal)
            ? new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["Credential"] = _credential }, ["message"] = "success" }
            : new JsonObject { ["code"] = -1, ["message"] = "token is wrong" };
    }

    /// <summary>鉴权：合格返回 null，不合格返回错误响应。</summary>
    private JsonObject? Authorized(HttpListenerRequest request)
    {
        var provided = request.Headers["Authorization"]?.Replace("Bearer ", string.Empty) ?? string.Empty;

        if (!string.Equals(provided, _credential, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _badCredentials.Add(provided);
            }

            return new JsonObject { ["code"] = -1, ["message"] = "Unauthorized" };
        }

        if (!UnauthorizedOnce)
        {
            return null;
        }

        UnauthorizedOnce = false;
        return new JsonObject { ["code"] = -1, ["message"] = "Unauthorized" };
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // 忽略
        }
    }
}
