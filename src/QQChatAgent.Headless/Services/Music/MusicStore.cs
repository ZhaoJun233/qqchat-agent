using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQChatAgent.Services.Music;

/// <summary>机器人“听过”的一首歌（存盘，用于避免重复下载/分析，也让面板能看到听过什么）。</summary>
public sealed class HeardSong
{
    /// <summary>唯一键：平台 + 歌曲 id（如 netease:186016）。</summary>
    public string Key { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string SongId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }

    /// <summary>波形分析的客观描述（拿不到音源时为空）。</summary>
    public string Features { get; set; } = string.Empty;

    /// <summary>歌词节选（给面板与后续对话复用）。</summary>
    public string LyricExcerpt { get; set; } = string.Empty;

    public DateTimeOffset FirstHeard { get; set; }

    public DateTimeOffset LastHeard { get; set; }

    public int HeardCount { get; set; }

    [JsonIgnore]
    public bool HasWaveform => !string.IsNullOrWhiteSpace(Features);

    /// <summary>分析结果是否还新鲜（超过 ttlDays 天就重新分析，音源/算法可能变过）。</summary>
    public bool IsFresh(DateTimeOffset now, int ttlDays) => now - LastHeard < TimeSpan.FromDays(Math.Max(1, ttlDays));
}

/// <summary>
/// “听过的歌”台账：data/music/listened.json。
/// 存它是为了两件事：① 同一首歌被反复分享时不再重复下载/解码（省流量也省 CPU）；
/// ② 面板/日志能看清机器人到底听过什么、分析过哪些。
/// </summary>
public sealed class MusicStore
{
    private readonly string _path;
    private readonly Func<int> _maxItems;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly List<HeardSong> _songs = [];

    public MusicStore(string path, Func<int> maxItems, Action<string> log)
    {
        _path = path;
        _maxItems = maxItems;
        _log = log;
        Load();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _songs.Count;
            }
        }
    }

    /// <summary>最近听过的歌（面板用）。</summary>
    public IReadOnlyList<HeardSong> Recent(int count)
    {
        lock (_gate)
        {
            return _songs.OrderByDescending(s => s.LastHeard).Take(Math.Max(1, count)).ToList();
        }
    }

    public HeardSong? Get(string key)
    {
        lock (_gate)
        {
            return _songs.FirstOrDefault(s => s.Key == key);
        }
    }

    /// <summary>记一笔：同键更新（次数 +1、刷新时间），否则新增；超出上限按最久没听到的淘汰。</summary>
    public HeardSong Remember(HeardSong song)
    {
        lock (_gate)
        {
            var existing = _songs.FirstOrDefault(s => s.Key == song.Key);
            if (existing is null)
            {
                _songs.Add(song);
                existing = song;
            }
            else
            {
                existing.LastHeard = song.LastHeard;
                existing.HeardCount++;
                existing.Title = string.IsNullOrWhiteSpace(song.Title) ? existing.Title : song.Title;
                existing.Artist = string.IsNullOrWhiteSpace(song.Artist) ? existing.Artist : song.Artist;
                existing.Album = string.IsNullOrWhiteSpace(song.Album) ? existing.Album : song.Album;
                existing.DurationSeconds = song.DurationSeconds > 0 ? song.DurationSeconds : existing.DurationSeconds;
                existing.Features = string.IsNullOrWhiteSpace(song.Features) ? existing.Features : song.Features;
                existing.LyricExcerpt = string.IsNullOrWhiteSpace(song.LyricExcerpt) ? existing.LyricExcerpt : song.LyricExcerpt;
            }

            var max = Math.Max(10, _maxItems());
            if (_songs.Count > max)
            {
                foreach (var old in _songs.OrderBy(s => s.LastHeard).Take(_songs.Count - max).ToList())
                {
                    _songs.Remove(old);
                }
            }

            Save();
            return existing;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<List<HeardSong>>(json);
            if (loaded is { Count: > 0 })
            {
                _songs.AddRange(loaded);
                _log($"[Music] 已加载听过的歌 {loaded.Count} 首");
            }
        }
        catch (Exception ex)
        {
            _log($"[Music] 读取 {Path.GetFileName(_path)} 失败（按空处理）: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_songs, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"[Music] 保存 {Path.GetFileName(_path)} 失败: {ex.Message}");
        }
    }
}
