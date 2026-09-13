namespace QQChatAgent.Services.Music;

/// <summary>
/// 从 QQ 消息里识别出来的一条音乐分享。
/// 来源可能是：OneBot 的 music 段（网易云/QQ音乐/酷狗…）、QQ 音乐卡片（json 段）、或者纯文本里的分享链接。
/// </summary>
/// <param name="Platform">平台标识：netease / qq / kugou / kuwo / migu / custom / unknown。</param>
/// <param name="SongId">平台内的歌曲 id（网易云就是数字 id）；拿不到时为 null。</param>
/// <param name="Title">卡片自带的歌名（有就用，省一次查询）。</param>
/// <param name="Artist">卡片自带的歌手。</param>
/// <param name="DirectAudioUrl">卡片自带的音频直链（如 QQ 音乐卡片里的 musicUrl），有就优先用。</param>
/// <param name="PageUrl">卡片指向的网页（如网易云分享短链），必要时用来反查歌曲 id。</param>
/// <param name="SourceLabel">识别来源，仅用于日志与排查（music段 / json卡片 / 文本链接）。</param>
public sealed record MusicShare(
    string Platform,
    string? SongId,
    string? Title = null,
    string? Artist = null,
    string? DirectAudioUrl = null,
    string? PageUrl = null,
    string SourceLabel = "unknown")
{
    /// <summary>是不是网易云来源（只有它能查歌词 + 走网易云音源）。</summary>
    public bool IsNetease => Platform == "netease";

    /// <summary>日志/提示用的一行描述。</summary>
    public string Describe()
    {
        var name = string.IsNullOrWhiteSpace(Title) ? "（未知曲目）" : Title;
        var who = string.IsNullOrWhiteSpace(Artist) ? string.Empty : " - " + Artist;
        var id = string.IsNullOrWhiteSpace(SongId) ? string.Empty : $" #{SongId}";
        return $"{name}{who}{id}（{Platform}/{SourceLabel}）";
    }
}

/// <summary>网易云歌曲信息（详情接口 + 歌词接口）。</summary>
public sealed record MusicInfo(
    string SongId,
    string Title,
    string Artist,
    string? Album,
    double DurationSeconds,
    string? Lyric,
    string? TranslatedLyric,
    string? CoverUrl)
{
    /// <summary>歌词去掉时间轴：给模型看的是纯文本，不是 LRC。</summary>
    public string PlainLyric()
    {
        if (string.IsNullOrWhiteSpace(Lyric))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in Lyric.Split('\n'))
        {
            var text = StripTimestamp(raw);
            if (text.Length == 0 || (lines.Count > 0 && lines[^1] == text))
            {
                continue;
            }

            // 网易云歌词里夹着“作词 : X”这类元信息，一样留着没坏处（模型知道是谁写的）
            lines.Add(text);
        }

        return string.Join("\n", lines);
    }

    private static string StripTimestamp(string line)
    {
        var text = line.Trim();
        while (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                break;
            }

            text = text[(close + 1)..].Trim();
        }

        return text;
    }
}

/// <summary>下载到的音频。</summary>
/// <param name="Data">原始字节（mp3/wav）。</param>
/// <param name="SourceLabel">由哪条音源模板拿到的（排查用）。</param>
public sealed record MusicAudio(byte[] Data, string SourceLabel);
