using System.Net;
using System.Text.Json;

namespace QQChatAgent.Services.Music;

/// <summary>
/// 网易云官方接口客户端：只用来拿**歌曲信息与歌词**（详情 + LRC）。
///
/// 为什么不拿音频：网易云的播放地址接口现在要求登录态，匿名请求一律返回 404，
/// 而且服务器在海外时绝大多数曲目还有地域限制 —— 实测（日本节点）匿名拿不到任何音频。
/// 音频交给 <see cref="MusicAudioResolver"/> 的多条音源去解决，这里专职“文字信息”，它很稳。
/// </summary>
public sealed class NeteaseMusicClient
{
    private const string DetailPath = "/api/song/detail?ids=%5B{0}%5D";
    private const string LyricPath = "/api/song/lyric?id={0}&lv=1&kv=1&tv=-1";

    private readonly HttpClient _http;
    private readonly Func<string?> _cookieProvider;
    private readonly Func<string> _baseUrl;
    private readonly Action<string> _log;

    public NeteaseMusicClient(HttpClient http, Func<string?> cookieProvider, Func<string> baseUrl, Action<string> log)
    {
        _http = http;
        _cookieProvider = cookieProvider;
        _baseUrl = baseUrl;
        _log = log;
    }

    /// <summary>查歌曲详情 + 歌词。失败返回 null（调用方要能优雅降级成“只有标题”）。</summary>
    public async Task<MusicInfo?> FetchAsync(string songId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(songId) || !songId.All(char.IsDigit))
        {
            return null;
        }

        string title = string.Empty, artist = string.Empty, album = string.Empty;
        double duration = 0;
        string? cover = null;

        try
        {
            var detail = await GetJsonAsync(string.Format(DetailPath, songId), ct);
            var song = detail is { } d && d.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array && songs.GetArrayLength() > 0
                ? songs[0]
                : (JsonElement?)null;

            if (song is { } s)
            {
                title = s.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                album = s.TryGetProperty("album", out var al) && al.TryGetProperty("name", out var an) ? an.GetString() ?? string.Empty : string.Empty;
                duration = s.TryGetProperty("duration", out var du) && du.TryGetDouble(out var ms) ? ms / 1000.0 : 0;
                if (s.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
                {
                    artist = string.Join(" / ", artists.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var an2) ? an2.GetString() : null)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[Music] 取歌曲详情失败（id={songId}）: {ex.Message}");
        }

        string? lyric = null, tlyric = null;
        try
        {
            var lrc = await GetJsonAsync(string.Format(LyricPath, songId), ct);
            if (lrc is { } l)
            {
                if (l.TryGetProperty("lrc", out var lrcObj) && lrcObj.TryGetProperty("lyric", out var text))
                {
                    lyric = text.GetString();
                }

                if (l.TryGetProperty("tlyric", out var tObj) && tObj.TryGetProperty("lyric", out var tText))
                {
                    tlyric = tText.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[Music] 取歌词失败（id={songId}）: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(lyric))
        {
            return null;
        }

        return new MusicInfo(songId, title.Length > 0 ? title : "（未知曲目）", artist, album, duration, lyric, tlyric, cover);
    }

    /// <summary>跟进短链跳转，拿到最终地址（网易云分享短链 163cn.tv / 手机分享链）。</summary>
    public async Task<string?> ResolveRedirectAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var final = resp.RequestMessage?.RequestUri?.ToString();
            return final;
        }
        catch (Exception ex)
        {
            _log($"[Music] 跟进短链失败（{url}）: {ex.Message}");
            return null;
        }
    }

    private async Task<JsonElement?> GetJsonAsync(string path, CancellationToken ct)
    {
        var baseUrl = _baseUrl().TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/122 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Referer", "https://music.163.com/");
        if (_cookieProvider() is { Length: > 0 } cookie)
        {
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }
}
