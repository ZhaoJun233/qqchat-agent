using System.Net;
using System.Text;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S22 用的假音乐服务：一个 HttpListener 同时扮演两个角色 ——
/// ① 网易云接口（歌曲详情 / 歌词）；
/// ② 音源（返回一段**合成音频**，让机器人真去解码、真去分析波形）。
///
/// 为什么音频用 WAV 而不是 mp3：仓库里不能塞真实歌曲片段（版权），
/// 而 WAV 可以现场合成 —— 振幅、节奏、段落全在我们的掌控里，
/// 于是“分析出来的数字对不对”这件事变成了可断言的：2Hz 的颤动就是 120 BPM。
/// 机器人的解码器按文件头分派，WAV 走 PCM 直读，mp3 走 NLayer，两条路都覆盖得到。
/// </summary>
public sealed class MockMusicHost : IDisposable
{
    private const int SampleRate = 8000;
    private const int DurationSeconds = 24;

    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly byte[] _wav;
    private int _apiHits;
    private int _audioHits;

    public MockMusicHost(int port, string title = "测试小夜曲", string artist = "测试歌手")
    {
        _port = port;
        Title = title;
        Artist = artist;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _wav = BuildWav();
    }

    public string Title { get; }

    public string Artist { get; }

    /// <summary>搜索接口被访问次数（验证“按歌名去听”真的发起了搜索）。</summary>
    public int SearchHits => Volatile.Read(ref _searchHits);

    private int _searchHits;

    /// <summary>有音频可下的歌曲 id。不在里面的（如 999002）音频接口返回 404 —— 用来验证“没音源也能降级”。</summary>
    public HashSet<long> AudioSongIds { get; } = [999001];

    /// <summary>接口被访问次数（详情 + 歌词）。</summary>
    public int ApiHits => Volatile.Read(ref _apiHits);

    /// <summary>音频被下载次数。</summary>
    public int AudioHits => Volatile.Read(ref _audioHits);

    /// <summary>
    /// 最近一次请求带的 Cookie（带 cookie 的请求才记）。
    /// 用来钉“网易云登录态真的被带上了”——不然 VIP 歌拿不到地址，而这正是线上老毛病的一半。
    /// </summary>
    public string LastCookie => _lastCookie;

    private volatile string _lastCookie = string.Empty;

    /// <summary>扫码接口被访问次数。</summary>
    public int QrHits => Volatile.Read(ref _qrHits);

    private int _qrHits;

    /// <summary>扫码成功后上游给的登录态（存进机器人库里的就是它）。</summary>
    public const string QrCookie = "MUSIC_U=mock-login-token-abc; __csrf=mockcsrf";

    /// <summary>清掉记录的 Cookie（下一步验证“这次请求到底带没带”）。</summary>
    public void LastCookieReset() => _lastCookie = string.Empty;

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>音源模板（音源名 | 模板），形如 mock|http://.../audio/{id}.mp3。</summary>
    public string SourceTemplate(long songId) => $"mock|http://127.0.0.1:{_port}/audio/{{id}}.mp3";

    /// <summary>一个一定拿不到音频的模板（用于验证“没音源也能降级”）。</summary>
    public string DeadSourceTemplate => $"dead|http://127.0.0.1:{_port}/missing/{{id}}.mp3";

    /// <summary>歌词（LRC；前半段是元信息，用来验证时间轴被剥掉）。</summary>
    public string Lyric =>
        $"[00:00.000] 作词 : {Artist}\n[00:01.000] 作曲 : {Artist}\n" +
        "[00:28.950]测试歌词第一句\n[00:32.310]测试歌词第二句\n[00:36.120]测试歌词第三句\n";

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
            byte[] body;
            string contentType;

            if (ctx.Request.Headers["Cookie"] is { Length: > 0 } seenCookie)
            {
                _lastCookie = seenCookie;
            }

            // 扫码登录三连：key → create → check（check 直接回 803 = 扫成功，并给 cookie）
            if (path.StartsWith("/login/qr/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _qrHits);
                if (path.StartsWith("/login/qr/key", StringComparison.Ordinal))
                {
                    body = Encoding.UTF8.GetBytes("{\"code\":200,\"data\":{\"unikey\":\"mock-unikey\"}}");
                }
                else if (path.StartsWith("/login/qr/create", StringComparison.Ordinal))
                {
                    body = Encoding.UTF8.GetBytes("{\"code\":200,\"qrimg\":\"data:image/png;base64,TU9DS1FS\"}");
                }
                else
                {
                    body = Encoding.UTF8.GetBytes(
                        "{\"code\":803,\"cookie\":\"" + QrCookie.Replace("\"", "") + "\"}");
                }

                contentType = "application/json";
            }
            else if (path.StartsWith("/api/song/detail", StringComparison.Ordinal) || path.StartsWith("/song/detail", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _apiHits);
                body = Encoding.UTF8.GetBytes(
                    $$"""{"songs":[{"name":"{{Title}}","duration":45000,"album":{"name":"测试专辑"},"artists":[{"name":"{{Artist}}"}]}]}""");
                contentType = "application/json";
            }
            else if (path.StartsWith("/api/search/get", StringComparison.Ordinal) || path.StartsWith("/search", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _searchHits);
                body = Encoding.UTF8.GetBytes(
                    "{\"result\":{\"songs\":[{\"id\":999001,\"name\":\"" + Title + "\",\"duration\":45000,\"artists\":[{\"name\":\"" + Artist + "\"}]}]}}");
                contentType = "application/json";
            }
            else if (path.StartsWith("/api/song/lyric", StringComparison.Ordinal) || path.StartsWith("/lyric", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _apiHits);
                body = Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["lrc"] = new Dictionary<string, object> { ["lyric"] = Lyric },
                        ["tlyric"] = new Dictionary<string, object> { ["lyric"] = string.Empty },
                    }));
                contentType = "application/json";
            }
            // 自建 Enhanced API 的音频地址接口：/song/url/v1?id=…&level=standard
            else if (path.StartsWith("/song/url", StringComparison.Ordinal))
            {
                var idText = System.Text.RegularExpressions.Regex.Match(path + "?", "[?&]id=(\\d+)").Groups[1].Value;
                var id = long.TryParse(idText, out var parsed) ? parsed : 0;
                var playable = AudioSongIds.Contains(id);
                body = Encoding.UTF8.GetBytes(
                    "{\"data\":[{\"id\":" + id + ",\"url\":" +
                    (playable ? "\"http://127.0.0.1:" + _port + "/audio/" + id + ".mp3\"" : "null") + ",\"code\":" + (playable ? 200 : 404) + "}]}");
                contentType = "application/json";
            }
            else if (path.StartsWith("/audio/", StringComparison.Ordinal))
            {
                var idText = Path.GetFileNameWithoutExtension(path);
                if (!long.TryParse(idText, out var songId) || !AudioSongIds.Contains(songId))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                Interlocked.Increment(ref _audioHits);
                body = _wav;
                contentType = "audio/wav";
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
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

    /// <summary>
    /// 合成 24 秒音频：8s 很轻 → 8s 很响 → 8s 中等，整体叠 2Hz 的振幅颤动。
    /// 于是可以断言：段落轮廓出现“弱/强/中”，最热的一段落在 0:08，速度约 120 BPM。
    /// </summary>
    private static byte[] BuildWav()
    {
        var samples = SampleRate * DurationSeconds;
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / SampleRate;
            var section = t < 8 ? 0.05 : t < 16 ? 0.60 : 0.18;
            var tremolo = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 2.0 * t); // 2Hz → 120 BPM
            var value = section * tremolo * Math.Sin(2 * Math.PI * 440 * t);
            var s = (short)Math.Clamp(value * 32000, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        var dataSize = pcm.Length;
        var wav = new byte[44 + dataSize];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + dataSize).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);   // PCM
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);   // 单声道
        BitConverter.GetBytes(SampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(SampleRate * 2).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataSize).CopyTo(wav, 40);
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
