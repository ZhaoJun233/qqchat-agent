using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S24 用的假 TTS 服务（扮演 Piper 旁路容器 tools/tts-server.py）：
///   <c>GET /health</c>  → 列出音色
///   <c>GET /speak?text=…&amp;voice=…&amp;speed=…</c> → 返回一小段 wav
///
/// 为什么要假服务而不是真的起 Piper：
///   • 集成测试要能在 CI/开发机上跑，不能依赖 60MB 的语音模型与 cpu 推理；
///   • 真正要断言的是**调用契约**（谁、带什么参数、调了几次、失败了怎么办），
///     而不是"华研的声音像不像人"—— 那是人耳验收的事。
///
/// 它会记录每一次 /speak 的原文参数，于是"机器人生成的 URL 对不对"
/// （文本、音色、语速）都能变成断言，而不是靠肉眼看日志。
/// </summary>
public sealed class MockTtsHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly List<SpeakCall> _speakCalls = new();
    private readonly object _gate = new();
    private int _healthHits;

    public MockTtsHost(int port, string bind = "127.0.0.1")
    {
        _port = port;
        _listener.Prefixes.Add($"http://{bind}:{port}/");
        Wav = BuildWav();
    }

    /// <summary>一次 /speak 调用的参数（从查询串里取出来的原文）。</summary>
    public readonly record struct SpeakCall(string Text, string Voice, string Speed);

    /// <summary>可用音色（/health 会列出来；不在里面的音色会返回 404）。</summary>
    public string[] Voices { get; set; } = ["zh_CN-huayan-medium", "zh_CN-xiao_ya-medium"];

    /// <summary>打开后所有请求都返回 500（模拟 TTS 容器挂了）。</summary>
    public bool Broken { get; set; }

    /// <summary>/health 被访问次数。</summary>
    public int HealthHits => Volatile.Read(ref _healthHits);

    public IReadOnlyList<SpeakCall> SpeakCalls
    {
        get
        {
            lock (_gate)
            {
                return _speakCalls.ToArray();
            }
        }
    }

    public int SpeakHits => SpeakCalls.Count;

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>返回给 /speak 的 wav（0.3 秒 440Hz，头部是真 RIFF —— 机器人会校验）。</summary>
    public byte[] Wav { get; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // 监听器已关闭
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";

            if (Broken)
            {
                await WriteJsonAsync(ctx, 500, new JsonObject { ["error"] = "piper 挂了（测试用）" });
                return;
            }

            if (path.Equals("/health", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _healthHits);
                var voices = new JsonArray();
                foreach (var v in Voices)
                {
                    voices.Add(v);
                }

                await WriteJsonAsync(ctx, 200, new JsonObject
                {
                    ["ok"] = true,
                    ["voices"] = voices,
                    ["default"] = Voices.Length > 0 ? Voices[0] : "zh_CN-huayan-medium"
                });
                return;
            }

            if (path.Equals("/speak", StringComparison.Ordinal))
            {
                var query = ctx.Request.QueryString;
                var text = query["text"] ?? string.Empty;
                var voice = query["voice"] ?? string.Empty;
                var speed = query["speed"] ?? string.Empty;

                lock (_gate)
                {
                    _speakCalls.Add(new SpeakCall(text, voice, speed));
                }

                if (Voices.Length > 0 && !Voices.Contains(voice))
                {
                    await WriteJsonAsync(ctx, 404, new JsonObject
                    {
                        ["error"] = $"音色不存在：{voice}",
                        ["hint"] = "音色就是 /data/<name>.onnx"
                    });
                    return;
                }

                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "audio/wav";
                ctx.Response.ContentLength64 = Wav.Length;
                await ctx.Response.OutputStream.WriteAsync(Wav);
                ctx.Response.Close();
                return;
            }

            await WriteJsonAsync(ctx, 404, new JsonObject { ["error"] = "用法：GET /speak?text=…（或 /health）" });
        }
        catch (Exception)
        {
            try
            {
                ctx.Response.Abort();
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, JsonObject payload)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }

    /// <summary>0.3 秒 440Hz 单声道 wav：只求“是个合法 wav”，不追求好听。</summary>
    private static byte[] BuildWav()
    {
        const int sampleRate = 22050;
        const int samples = sampleRate * 3 / 10;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = 0.2 * Math.Sin(2 * Math.PI * 440 * i / sampleRate);
            var s = (short)(value * 32000);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        var wav = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + pcm.Length).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(pcm.Length).CopyTo(wav, 40);
        pcm.CopyTo(wav, 44);
        return wav;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
