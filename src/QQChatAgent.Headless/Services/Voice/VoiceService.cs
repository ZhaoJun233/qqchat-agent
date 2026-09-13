using System.Text.Json.Nodes;

namespace QQChatAgent.Services.Voice;

/// <summary>
/// 语音（TTS）服务客户端 —— 对接 Piper 旁路容器（仓库里的 <c>tools/tts-server.py</c>）：
///   <c>GET /speak?text=…&amp;voice=…&amp;speed=…</c> → 返回 wav
///   <c>GET /health</c>                            → 列出可用音色
///
/// 这里有个关键设计：**机器人不搬运音频**。发语音时只把 <c>/speak</c> 的 URL 交给协议端
/// （见 <c>OneBotGateway.SendVoiceAsync</c>），由 NapCat 自己去下载、转 silk、上传。
/// 好处是机器人这边完全不用碰 silk 编码与 base64，也不用把音频塞进 WebSocket；
/// 代价是协议端必须能访问到这个地址（容器同网段时就是 <c>http://tts:5000</c>）。
///
/// 只有面板「试听一句」才真把 wav 拉到本地 —— 那是给浏览器播的。
/// </summary>
public sealed class VoiceService
{
    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string> _log;

    public VoiceService(HttpClient http, Func<AppSettings> settings, Action<string> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    /// <summary>当前配置的 TTS 服务根地址（去掉尾部斜杠）；没配/不是 http(s) 绝对地址时返回 null。</summary>
    public string? BaseUrl => Normalize(_settings().TtsServiceUrl);

    /// <summary>当前音色（Piper 模型名），空则回落到默认。</summary>
    public string VoiceName
    {
        get
        {
            var name = _settings().VoiceName?.Trim();
            return string.IsNullOrWhiteSpace(name) ? "zh_CN-huayan-medium" : name;
        }
    }

    /// <summary>语速倍数（设置里是百分比，100 = 原速）。</summary>
    public double Speed => Math.Clamp(_settings().VoiceSpeed, 50, 200) / 100.0;

    /// <summary>
    /// 拼出发给协议端的 <c>/speak</c> 绝对地址。text 为空、服务地址不合法时返回 null
    /// （上层据此降级成发文字，而不是把一条空语音发出去）。
    /// </summary>
    public string? BuildSpeakUrl(string text, string? voiceOverride = null, int? speedOverride = null)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var baseUrl = BaseUrl;
        if (baseUrl is null)
        {
            return null;
        }

        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? VoiceName : voiceOverride!.Trim();
        var speedPercent = Math.Clamp(speedOverride ?? _settings().VoiceSpeed, 50, 200);
        var speed = speedPercent / 100.0;

        // 只对参数做转义：文本里可能有 & # 空格和中文，不转义会把查询串撕碎
        return $"{baseUrl}/speak?text={Uri.EscapeDataString(trimmed)}" +
               $"&voice={Uri.EscapeDataString(voice)}" +
               $"&speed={speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 真把一段文本合成成 wav 字节（面板「试听一句」用）。
    /// 失败时返回 (null, 原因)，原因直接来自 TTS 服务的 JSON error 字段（如"音色不存在"）。
    /// </summary>
    public async Task<(byte[]? Data, string? Error)> SynthesizeAsync(
        string text,
        string? voiceOverride,
        int? speedOverride,
        CancellationToken ct)
    {
        var url = BuildSpeakUrl(text, voiceOverride, speedOverride);
        if (url is null)
        {
            return (null, string.IsNullOrWhiteSpace(text) ? "没有要合成的文本" : "TTS 服务地址没配置（应形如 http://tts:5000）");
        }

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 服务端失败时返回的是 JSON（{"error": "…"}），把这句话原样带给面板
                var detail = System.Text.Encoding.UTF8.GetString(bytes);
                try
                {
                    detail = JsonNode.Parse(detail)?["error"]?.GetValue<string>() ?? detail;
                }
                catch
                {
                    // 不是 JSON 就用原文（截断，别把整页 HTML 塞进面板）
                }

                if (detail.Length > 300)
                {
                    detail = detail[..300];
                }

                _log($"[Voice] TTS 合成失败 HTTP {(int)resp.StatusCode}：{detail}");
                return (null, $"HTTP {(int)resp.StatusCode}：{detail}");
            }

            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                // 不是 wav：多数是配置错（地址指到了一个网页），说出来比"静默无声"强
                return (null, $"TTS 返回的不是 wav（{bytes.Length} 字节，HTTP {resp.Content.Headers.ContentType}）");
            }

            return (bytes, null);
        }
        catch (Exception ex)
        {
            _log($"[Voice] TTS 请求异常：{ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>问一下 TTS 服务自己：活着吗、有哪些音色（面板用来校验地址与音色名）。</summary>
    public async Task<(bool Ok, JsonNode? Payload, string? Error)> HealthAsync(CancellationToken ct)
    {
        var baseUrl = BaseUrl;
        if (baseUrl is null)
        {
            return (false, null, "TTS 服务地址没配置（应形如 http://tts:5000）");
        }

        try
        {
            using var resp = await _http.GetAsync($"{baseUrl}/health", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, null, $"HTTP {(int)resp.StatusCode}：{(text.Length > 200 ? text[..200] : text)}");
            }

            return (true, JsonNode.Parse(text), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>把设置里的地址规整成 "http://host:port"（无尾部斜杠）；不合法返回 null。</summary>
    private static string? Normalize(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return trimmed.TrimEnd('/');
    }
}
