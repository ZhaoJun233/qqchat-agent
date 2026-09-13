using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// 假 OpenAI 兼容服务：记录收到的请求，按脚本返回模型回复。
/// 用来断言「喂给模型的上下文」是否正确（人设/人物档案/图片/上下文条数）。
/// </summary>
public sealed class MockOpenAi : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<JsonObject> _requests = new();
    private readonly object _gate = new();
    private readonly Queue<string> _scriptedReplies = new();
    private readonly Queue<string> _curationReplies = new();
    private readonly Queue<string> _stickerDescribeReplies = new();
    private readonly int _port;
    private int _portraitRequests;
    private int _stickerDescribeRequests;
    private int _stickerCurateRequests;

    public MockOpenAi(int port, string bind = "127.0.0.1")
    {
        _port = port;
        _listener.Prefixes.Add($"http://{bind}:{port}/");
        BaseUrl = $"http://127.0.0.1:{_port}/v1";
    }

    /// <summary>喂给被测机器人的地址（外部容器测试时会改成 host.docker.internal）。</summary>
    public string BaseUrl { get; set; }

    /// <summary>让 mock 监听监听的端口（外部容器测试用）。</summary>
    public int Port => _port;

    /// <summary>人为拖慢响应，用于可靠地测「触发消息与回复之间被插话」的引用行为。</summary>
    public int ResponseDelayMs { get; init; }

    /// <summary>收到的请求体（已解析）。</summary>
    public IReadOnlyList<JsonObject> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>清空已记录的请求（重启验证时用：同一个 mock 要区分“重启前/后”）。</summary>
    public void ClearRequests()
    {
        lock (_gate)
        {
            _requests.Clear();
        }
    }

    /// <summary>排入一条脚本文本（模型 message.content 原文），先进先出。</summary>
    public void EnqueueReply(string content)
    {
        lock (_gate)
        {
            _scriptedReplies.Enqueue(content);
        }
    }

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
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        JsonObject? payload = null;
        try
        {
            payload = JsonNode.Parse(body) as JsonObject;
        }
        catch
        {
            // 非 JSON：忽略
        }

        if (payload is not null)
        {
            lock (_gate)
            {
                _requests.Add(payload);
            }
        }

        // 上游（Gemini 等）会给“以模型发言结尾”的请求直接返回 400：
        //   Requests ending with a model turn are not supported.
        // 机器人自己触发的后续发言（听完歌回来接话 / 被戳 / 静默兜底）恰好就是这种形状，
        // 所以这里当**默认行为**把这件事变成红灯 —— 否则要等到线上 Gemini 报 400 才发现。
        // （修法：OpenAiClient 在末尾补一条“系统口吻的 user 轮”。）
        var lastRole = payload?["messages"]?.AsArray().LastOrDefault()?["role"]?.GetValue<string>();
        if (lastRole is null or "assistant")
        {
            Console.WriteLine($"      [mock] 拒绝以模型发言结尾的请求（roles={string.Join(",", payload?["messages"]?.AsArray().Select(m => m?["role"]?.GetValue<string>()) ?? Array.Empty<string?>())}）");
            var rejectBody = Encoding.UTF8.GetBytes(
                "{\"error\": {\"code\": 400, \"message\": \"Requests ending with a model turn are not supported.\", \"status\": \"INVALID_ARGUMENT\"}}");
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = rejectBody.Length;
            await context.Response.OutputStream.WriteAsync(rejectBody);
            context.Response.Close();
            return;
        }

        // 人物画像请求：提示词里会要求“压缩成一段人物画像”。
        // 这类请求期望纯文本画像，不能拿聊天脚本（JSON）当回应。
        var systemPrompt = payload?["messages"]?.AsArray()
            .FirstOrDefault(m => m?["role"]?.GetValue<string>() == "system")?["content"]?.GetValue<string>() ?? "";

        if (systemPrompt.Contains("人物画像"))
        {
            lock (_gate)
            {
                _portraitRequests++;
            }

            await WriteCompletionAsync(context, payload, "老王：后端开发，常在群里问编译与部署报错；说话直接、爱用“哈”，与小李常互揭老底。");
            return;
        }

        // 表情包入库审核：机器人让它看图给“一句话说明 + 关键词 + 是不是表情包”
        if (systemPrompt.Contains("入库审核") || systemPrompt.Contains("编目"))
        {
            lock (_gate)
            {
                _stickerDescribeRequests++;
            }

            string verdict;
            lock (_gate)
            {
                // 默认：是表情包。测审核闸门时用 EnqueueStickerVerdict 排入 {"sticker": false, ...}
                verdict = _stickerDescribeReplies.Count > 0
                    ? _stickerDescribeReplies.Dequeue()
                    : """{"sticker": true, "desc": "憋笑失败的猫", "tags": ["大笑", "憋笑", "沙雕"]}""";
            }

            await WriteCompletionAsync(context, payload, verdict);
            return;
        }

        // 表情包巡检：机器人让它自己决定删哪些（脚本由测试用 ScriptedCuration 控制）
        if (systemPrompt.Contains("整理表情包库"))
        {
            lock (_gate)
            {
                _stickerCurateRequests++;
            }

            string curation;
            lock (_gate)
            {
                curation = _curationReplies.Count > 0
                    ? _curationReplies.Dequeue()
                    : """{"delete": [], "reason": "都还行"}""";
            }

            await WriteCompletionAsync(context, payload, curation);
            return;
        }

        string content;
        lock (_gate)
        {
            content = _scriptedReplies.Count > 0
                ? _scriptedReplies.Dequeue()
                : """{"suitability": 90, "reply": "默认回复"}""";
        }

        if (ResponseDelayMs > 0)
        {
            await Task.Delay(ResponseDelayMs);
        }

        await WriteCompletionAsync(context, payload, content);
    }

    /// <summary>收到的画像摘要请求数。</summary>
    public int PortraitRequests
    {
        get
        {
            lock (_gate)
            {
                return _portraitRequests;
            }
        }
    }

    /// <summary>收到的表情包编目请求数（看图给说明/关键词）。</summary>
    public int StickerDescribeRequests
    {
        get
        {
            lock (_gate)
            {
                return _stickerDescribeRequests;
            }
        }
    }

    /// <summary>收到的表情包巡检请求数（机器人自己决定删哪些）。</summary>
    public int StickerCurateRequests
    {
        get
        {
            lock (_gate)
            {
                return _stickerCurateRequests;
            }
        }
    }

    /// <summary>排入一条巡检脚本（模型返回的 JSON 原文），先进先出。</summary>
    public void EnqueueCuration(string content)
    {
        lock (_gate)
        {
            _curationReplies.Enqueue(content);
        }
    }

    /// <summary>
    /// 排入一条表情包审核结果（默认是“是表情包 + 憋笑失败的猫”）。
    /// 用来测审核闸门：返回 {"sticker": false, ...} 时机器人必须把它丢掉。
    /// </summary>
    public void EnqueueStickerVerdict(string content)
    {
        lock (_gate)
        {
            _stickerDescribeReplies.Enqueue(content);
        }
    }

    private static async Task WriteCompletionAsync(HttpListenerContext context, JsonObject? payload, string content)
    {
        var response = new JsonObject
        {
            ["id"] = "chatcmpl-mock",
            ["object"] = "chat.completion",
            ["model"] = payload?["model"]?.GetValue<string>() ?? "mock",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
                    ["finish_reason"] = "stop"
                }
            }
        };

        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public string DescribeRequest(int index)
    {
        var req = Requests.ElementAtOrDefault(index);
        // 不要转义非 ASCII：断言要能直接拿中文去 Contains（默认编码器会把“波形实测”写成 \u6CE2…，
        // 一度让“提示词里有没有这句话”查不出来）
        return req is null
            ? "(无请求)"
            : JsonSerializer.Serialize(req, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
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
