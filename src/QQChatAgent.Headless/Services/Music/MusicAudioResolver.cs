using System.Net;
using System.Text;
using System.Text.Json;

namespace QQChatAgent.Services.Music;

/// <summary>
/// 音频解析：按配置的音源模板顺序去拿**低码率**的曲子本体。
///
/// 为什么是多条模板而不是写死一个 API：
/// 这类公开音源（Meting 系的实例、聚合站 API）都是第三方服务，随时会挂、会改参数、会加签名。
/// 实测就踩到过：injahow 实例只给 45 秒试听片段，qijieya 实例给整首；
/// gdstudio 的老域名（music-api.*）直接 522，新域名（music.gdstudio.xyz）改用自定义 crc32 签名。
/// 所以音源做成模板 + 可配置，挂了就在面板里改一行，不用改代码。
///
/// 模板占位符：{id} 歌曲 id、{br} 码率、{crc32} 标准 CRC32 的 8 位大写十六进制（给 gdstudio 用）。
/// </summary>
public sealed class MusicAudioResolver
{
    private readonly HttpClient _http;
    private readonly Func<IReadOnlyList<string>> _templates;
    private readonly Func<int> _bitrate;
    private readonly Func<int> _maxBytes;
    private readonly Action<string> _log;

    public MusicAudioResolver(HttpClient http, Func<IReadOnlyList<string>> templates, Func<int> bitrate, Func<int> maxBytes, Action<string> log)
    {
        _http = http;
        _templates = templates;
        _bitrate = bitrate;
        _maxBytes = maxBytes;
        _log = log;
    }

    /// <summary>依次尝试各音源，返回第一个拿到的可用音频；全失败返回 null。</summary>
    public async Task<MusicAudio?> DownloadAsync(MusicShare share, CancellationToken ct)
    {
        // 卡片自带直链（QQ 音乐卡片常见）优先：不用求人
        if (!string.IsNullOrWhiteSpace(share.DirectAudioUrl))
        {
            var direct = await TryDownloadAsync(share.DirectAudioUrl!, "卡片直链", ct);
            if (direct is not null)
            {
                return direct;
            }
        }

        if (string.IsNullOrWhiteSpace(share.SongId))
        {
            return null;
        }

        foreach (var template in _templates())
        {
            var (name, urlTemplate) = SplitTemplate(template);
            if (string.IsNullOrWhiteSpace(urlTemplate))
            {
                continue;
            }

            var url = urlTemplate
                .Replace("{id}", Uri.EscapeDataString(share.SongId), StringComparison.Ordinal)
                .Replace("{br}", _bitrate().ToString(), StringComparison.Ordinal)
                .Replace("{crc32}", Crc32Hex(share.SongId), StringComparison.Ordinal);

            var audio = await TryDownloadAsync(url, name, ct);
            if (audio is not null)
            {
                return audio;
            }
        }

        return null;
    }

    /// <summary>下载一个地址；若拿到的是 JSON（很多聚合 API 先返回 {"url":…}）就再跟进它的直链。</summary>
    private async Task<MusicAudio?> TryDownloadAsync(string url, string label, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[Music] 音源 {label} 返回 {(int)resp.StatusCode}，跳过");
                return null;
            }

            var body = await resp.Content.ReadAsByteArrayAsync(ct);
            if (body.Length == 0)
            {
                _log($"[Music] 音源 {label} 返回空内容，跳过");
                return null;
            }

            if (body.Length > _maxBytes())
            {
                _log($"[Music] 音源 {label} 文件过大（{body.Length / 1024 / 1024}MB > {_maxBytes() / 1024 / 1024}MB），跳过");
                return null;
            }

            if (LooksLikeAudio(body))
            {
                return new MusicAudio(body, label);
            }

            // 不是音频：多半是 {"url":"https://…"} 这种中转结果
            var nested = TryReadUrlFromJson(body);
            if (!string.IsNullOrWhiteSpace(nested) && !string.Equals(nested, url, StringComparison.OrdinalIgnoreCase))
            {
                _log($"[Music] 音源 {label} 返回中转地址，继续跟进");
                return await TryDownloadAsync(nested!, label + "+直链", ct);
            }

            _log($"[Music] 音源 {label} 返回的不是音频（{body.Length} 字节，开头 {Preview(body)}）");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"[Music] 音源 {label} 下载失败: {ex.Message}");
            return null;
        }
    }

    private static (string Name, string Template) SplitTemplate(string template)
    {
        var idx = template.IndexOf('|');
        return idx > 0 ? (template[..idx], template[(idx + 1)..].Trim()) : ("音源", template.Trim());
    }

    /// <summary>文件头判断：ID3 / MPEG 帧同步 / RIFF(WAV) / OggS / fLaC。</summary>
    private static bool LooksLikeAudio(byte[] data)
    {
        if (data.Length < 12)
        {
            return false;
        }

        if (data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            return true; // mp3 + ID3v2
        }

        if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0)
        {
            return true; // mp3 裸帧
        }

        if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
        {
            return true; // wav
        }

        if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
        {
            return true; // ogg
        }

        return data[0] == 'f' && data[1] == 'L' && data[2] == 'a' && data[3] == 'C';
    }

    private static string? TryReadUrlFromJson(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "url", "data", "src", "location" })
                {
                    if (!root.TryGetProperty(key, out var v))
                    {
                        continue;
                    }

                    if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s && s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        return s;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 不是 JSON（HTML 错误页等）
        }

        return null;
    }

    private static string Preview(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 60)).Replace('\n', ' ').Replace('\r', ' ');
        return text.Length > 40 ? text[..40] + "…" : text;
    }

    /// <summary>标准 CRC32（IEEE）的 8 位大写十六进制 —— gdstudio 接口的 s 参数就是这个形式。</summary>
    private static string Crc32Hex(string text)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return (~crc).ToString("X8");
    }
}
