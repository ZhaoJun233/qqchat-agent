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

    /// <summary>
    /// 两套接口风格：
    ///  • official：music.163.com 官方接口（匿名时音频一律 404，搜索结果质量很差）
    ///  • enhanced：自建的 NeteaseCloudMusicApiEnhanced（路径不同，但能搜准、能直接给音频地址）
    /// 判断方式就用 base URL —— 指向了自建服务就一定是 enhanced，不用多一个开关。
    /// </summary>
    private bool Enhanced => !_baseUrl().Contains("music.163.com", StringComparison.OrdinalIgnoreCase);

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
            var detail = await GetJsonAsync(Enhanced ? $"/song/detail?ids={songId}" : string.Format(DetailPath, songId), ct);
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
            var lrc = await GetJsonAsync(Enhanced ? $"/lyric?id={songId}" : string.Format(LyricPath, songId), ct);
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

    /// <summary>
    /// 按歌名搜歌（网易云搜索接口）：群里说“去听一下 XXX”时走这条路。
    /// 返回能直接交给后续流程的 <see cref="MusicShare"/>（带歌曲 id 与歌名歌手）。
    /// </summary>
    public async Task<MusicShare?> SearchAsync(string keyword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return null;
        }

        try
        {
            var path = Enhanced
                ? "/search?keywords=" + Uri.EscapeDataString(keyword.Trim()) + "&limit=10"
                : "/api/search/get?s=" + Uri.EscapeDataString(keyword.Trim()) + "&type=1&limit=10&offset=0";
            var json = await GetJsonAsync(path, ct);
            var candidates = json is { } root && root.TryGetProperty("result", out var result) &&
                             result.TryGetProperty("songs", out var songs) && songs.ValueKind == JsonValueKind.Array
                ? songs.EnumerateArray().ToList()
                : [];

            if (candidates.Count == 0)
            {
                _log($"[Music] 搜不到「{keyword}」");
                return null;
            }

            // 搜索接口的第一条经常不是人要的那首（搜“起风了 买辣椒也用券”可能先给别的）——
            // 所以在前几条里按“歌名/歌手是否出现在这句话里”打个分，取最像的。
            var wanted = keyword.Trim();
            var best = candidates
                .Select((s, index) => (Song: s, Score: MatchScore(s, wanted) - index * 0.01))
                .OrderByDescending(x => x.Score)
                .First()
                .Song;

            var song = (JsonElement?)best;

            if (song is not { } s || !s.TryGetProperty("id", out var idNode))
            {
                _log($"[Music] 搜不到「{keyword}」");
                return null;
            }

            var id = idNode.ValueKind == JsonValueKind.Number ? idNode.GetInt64().ToString() : idNode.GetString();
            var title = s.TryGetProperty("name", out var n) ? n.GetString() : null;
            var artist = s.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join(" / ", artists.EnumerateArray().Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
                : null;

            return string.IsNullOrWhiteSpace(id) ? null : new MusicShare("netease", id, title, artist, SourceLabel: "搜索");
        }
        catch (Exception ex)
        {
            _log($"[Music] 搜索「{keyword}」失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>候选歌曲与这句话的匹配程度：歌名命中最低分最高，歌手命中次之（用于从搜索结果里选最像的那首）。</summary>
    private static double MatchScore(JsonElement s, string wanted)
    {
        var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        var artists = s.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
            ? string.Join(" ", a.EnumerateArray().Select(x => x.TryGetProperty("name", out var an) ? an.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)))
            : string.Empty;

        var score = 0.0;
        if (name.Length > 0)
        {
            if (wanted.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;                       // 歌名原样出现在这句话里 = 最像
            }
            else if (name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
            else if (wanted.Contains(name[..Math.Min(2, name.Length)], StringComparison.OrdinalIgnoreCase))
            {
                score += 0.5;                     // 至少开头两字对得上（“起风了”→“起风”类截断）
            }
        }

        foreach (var artist in artists.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (wanted.Contains(artist, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.5;                     // 点名的歌手也加分：同一个歌名很多版本，认歌手
            }
        }

        return score;
    }

    /// <summary>
    /// 向自建 Enhanced API 要音频播放地址（官方接口拿不到，自建的能）。
    /// 拿到就直接当“直链”用，不用再去求第三方 Meting 实例。
    /// </summary>
    public async Task<string?> GetAudioUrlAsync(string songId, CancellationToken ct)
    {
        if (!Enhanced || string.IsNullOrWhiteSpace(songId))
        {
            return null;
        }

        try
        {
            var json = await GetJsonAsync($"/song/url/v1?id={songId}&level=standard", ct);
            var url = json is { } root && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
                      data[0].TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                _log($"[Music] 自建接口没给音频地址（id={songId}，可能是 VIP 歌，需登录 cookie）");
            }

            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception ex)
        {
            _log($"[Music] 取自建接口音频地址失败（id={songId}）: {ex.Message}");
            return null;
        }
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
