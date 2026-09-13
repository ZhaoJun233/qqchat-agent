using QQChatAgent.Services.Agent;
using System.Text.RegularExpressions;

namespace QQChatAgent.Services.Music;

/// <summary>
/// “听音乐”这件事的编排：识别到的分享 → 查歌名歌词 → 下一份低码率音频 → 分析波形 → 交出一段给模型看的事实描述。
///
/// 设计取舍：
/// ① 音频只当“分析原料”，默认分析完就删（<see cref="AppSettings.MusicKeepAudio"/> 可留）。
///    机器人要的是“听过之后的印象”，不是攒一个音乐库。
/// ② 任何一步失败都不影响回消息：拿不到音源就只给歌词，拿不到歌词就只给卡片里的歌名 —— 逐级降级，绝不空转。
/// ③ 同一首歌反复被分享时走台账缓存，不再重复下载解码。
/// </summary>
public sealed partial class MusicService
{
    [GeneratedRegex(@"(?:song|songDetail)[\?/]?(?:id=)?(?<id>\d{3,20})", RegexOptions.IgnoreCase)]
    private static partial Regex SongIdFromUrl();

    private readonly MusicStore _store;
    private readonly NeteaseMusicClient _netease;
    private readonly MusicAudioResolver _audio;
    private readonly OpenAiClient _brain;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string> _log;
    private readonly string _audioDir;

    public MusicService(MusicStore store, NeteaseMusicClient netease, MusicAudioResolver audio, OpenAiClient brain,
        Func<AppSettings> settings, string audioDir, Action<string> log)
    {
        _store = store;
        _netease = netease;
        _audio = audio;
        _brain = brain;
        _settings = settings;
        _audioDir = audioDir;
        _log = log;
    }

    /// <summary>
    /// 按歌名“去听一下”：先在网易云搜歌，搜到就走和分享一样的流程（歌词 + 波形）。
    /// 这是群里说“@机器人 去听一下 XXX”时走的路 —— 以前只认分享卡片，所以那句话完全没反应。
    /// </summary>
    public async Task<string?> DescribeByNameAsync(string keyword, string senderName, CancellationToken ct)
    {
        var song = await _netease.SearchAsync(keyword, ct);
        if (song is null)
        {
            return null;
        }

        _log($"[Music] 搜到「{song.Title} - {song.Artist}」(# {song.SongId})，开始听");
        return await DescribeAsync(song, senderName, ct);
    }

    /// <summary>把歌名解析成歌曲 id（机器人要发分享卡片时用）。</summary>
    public async Task<string?> ResolveSongIdByNameAsync(string keyword, CancellationToken ct)
    {
        var song = await _netease.SearchAsync(keyword, ct);
        return song?.SongId;
    }

    /// <summary>
    /// 按歌名搜歌并返回可用的分享对象（机器人自己发卡片时用）。
    /// </summary>
    public Task<MusicShare?> SearchAsync(string keyword, CancellationToken ct) => _netease.SearchAsync(keyword, ct);

    /// <summary>
    /// 处理一条分享：返回要注入上下文的“事实描述”；完全没信息时返回 null。
    /// </summary>
    public async Task<string?> DescribeAsync(MusicShare share, string senderName, CancellationToken ct)
    {
        var settings = _settings();
        var songId = await ResolveSongIdAsync(share, ct);

        MusicInfo? info = null;
        if (share.IsNetease && songId is { Length: > 0 })
        {
            info = await _netease.FetchAsync(songId, ct);
        }

        var title = info?.Title ?? share.Title ?? "（未知曲目）";
        var artist = FirstNonEmpty(info?.Artist, share.Artist);
        var key = $"{share.Platform}:{songId ?? title}";
        var now = DateTimeOffset.Now;

        // 台账里已经有分析结果、而且够新鲜 → 不重复下载
        var known = _store.Get(key);
        string? features = null;
        if (known is not null && known.HasWaveform && known.IsFresh(now, settings.MusicNoteTtlDays))
        {
            features = known.Features;
        }

        if (features is null && songId is { Length: > 0 })
        {
            // 自建 Enhanced 接口能直接给播放地址（官方接口给不了）→ 优先用它，不用再去求第三方音源
            var directUrl = await _netease.GetAudioUrlAsync(songId, ct);
            var audio = await _audio.DownloadAsync(share with { SongId = songId, DirectAudioUrl = directUrl ?? share.DirectAudioUrl }, ct);
            if (audio is null)
            {
                _log($"[Music] {share.Describe()} 没拿到音源（只按歌词处理）");
            }
            else
            {
                var analysed = WaveformAnalyzer.Analyze(audio.Data, settings.MusicMaxAnalysisSeconds);
                if (analysed is null)
                {
                    _log($"[Music] {share.Describe()} 音频解码失败（{audio.SourceLabel}，{audio.Data.Length / 1024}KB）");
                }
                else
                {
                    features = analysed.Description;
                    _log($"[Music] 听过「{title}」：{analysed.Description}（音源：{audio.SourceLabel}，{audio.Data.Length / 1024}KB）");

                    // 再让**专门的音频识别模型**亲耳听一遍：
                    // 手写 DSP 只能给出响度/动态/BPM/段落这些数字，曲风、编配、人声、情绪得靠真能听到声音的模型。
                    var heard = await _brain.DescribeAudioAsync(audio.Data, AudioFormat(audio.Data), title, artist, ct);
                    if (!string.IsNullOrWhiteSpace(heard))
                    {
                        features = "模型听感：" + heard.Trim() + "\n波形实测：" + analysed.Description;
                    }
                    else
                    {
                        _log("[Music] 音频识别模型没给出听感（未配置/不支持音频/请求失败）→ 只用波形实测数据");
                    }

                    if (settings.MusicKeepAudio)
                    {
                        KeepAudio(key, audio);
                    }
                }
            }
        }

        var lyric = info?.PlainLyric() ?? string.Empty;
        var lyricExcerpt = lyric.Length > 1200 ? lyric[..1200] : lyric;

        _store.Remember(new HeardSong
        {
            Key = key,
            Platform = share.Platform,
            SongId = songId ?? string.Empty,
            Title = title,
            Artist = artist ?? string.Empty,
            Album = info?.Album ?? string.Empty,
            DurationSeconds = info?.DurationSeconds ?? 0,
            Features = features ?? string.Empty,
            LyricExcerpt = lyricExcerpt,
            FirstHeard = known?.FirstHeard ?? now,
            LastHeard = now,
            HeardCount = 1,
        });

        return BuildNote(senderName, title, artist, info, features, lyric);
    }

    /// <summary>把分享里的歌曲 id 定下来：短链跟一次跳转，从最终地址里抠 id。</summary>
    private async Task<string?> ResolveSongIdAsync(MusicShare share, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(share.SongId))
        {
            return share.SongId;
        }

        if (string.IsNullOrWhiteSpace(share.PageUrl) || !share.IsNetease)
        {
            return null;
        }

        var finalUrl = await _netease.ResolveRedirectAsync(share.PageUrl!, ct);
        if (string.IsNullOrWhiteSpace(finalUrl))
        {
            return null;
        }

        var m = SongIdFromUrl().Match(finalUrl);
        if (m.Success)
        {
            _log($"[Music] 短链解析出歌曲 id={m.Groups["id"].Value}");
            return m.Groups["id"].Value;
        }

        return null;
    }

    /// <summary>音频格式（喂给模型时要说清楚）：按文件头判断。</summary>
    private static string AudioFormat(byte[] data)
    {
        if (data.Length > 4 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
        {
            return "wav";
        }

        return data.Length > 3 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g'
            ? "ogg"
            : "mp3";
    }

    /// <summary>分析完的音频要留档时才落盘（默认不留 —— 服务器上不攒版权内容）。</summary>
    private void KeepAudio(string key, MusicAudio audio)
    {
        try
        {
            Directory.CreateDirectory(_audioDir);
            var safe = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or ':' or '_' ? c : '_').ToArray()).Replace(':', '_');
            var path = Path.Combine(_audioDir, safe + ".mp3");
            File.WriteAllBytes(path, audio.Data);
            _log($"[Music] 音频已留存: {path}");
        }
        catch (Exception ex)
        {
            _log($"[Music] 留存音频失败: {ex.Message}");
        }
    }

    /// <summary>拼给模型看的事实描述：歌名/时长/波形实测/歌词节选，全部是“有据可查”的部分。</summary>
    private static string BuildNote(string senderName, string title, string? artist, MusicInfo? info, string? features, string lyric)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("群里 ").Append(senderName).Append(" 分享了一首歌：").Append(title);
        if (!string.IsNullOrWhiteSpace(artist))
        {
            sb.Append(" - ").Append(artist);
        }

        if (!string.IsNullOrWhiteSpace(info?.Album))
        {
            sb.Append("（专辑《").Append(info!.Album).Append("》）");
        }

        if (info is { DurationSeconds: > 1 })
        {
            sb.Append("，时长 ").Append(FormatTime(info.DurationSeconds));
        }

        sb.Append("。\n");
        sb.Append(features is { Length: > 0 }
            ? features + "\n"
            : "（这首歌没能拿到音源做分析，只能看歌词。）\n");

        if (lyric.Length > 0)
        {
            const int maxLines = 40;
            var lines = lyric.Split('\n');
            var excerpt = string.Join("\n", lines.Take(maxLines));
            sb.Append("歌词").Append(lines.Length > maxLines ? $"（前 {maxLines} 行，共 {lines.Length} 行）" : string.Empty).Append("：\n").Append(excerpt);
        }
        else
        {
            sb.Append("（没有歌词。）");
        }

        return sb.ToString();
    }

    private static string FormatTime(double seconds)
    {
        var total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
