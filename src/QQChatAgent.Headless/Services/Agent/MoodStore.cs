using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQChatAgent.Services.Agent;

/// <summary>
/// 机器人的“当前心情”。
///
/// 为什么要它：被戳一戳时，代码原来只有“回话 + 想戳回去就戳回去”这一条路，
/// 群里实测就是**每次被戳都戳回去**（18:40 那次 4 次被戳回了 3 次），看着像个复读机。
/// 群主的要求是“按当前心情决定，不必每次都回戳”。
///
/// 心情由两部分组成，互相配合：
///   ① **客观部分（代码算的）**：最近一段时间被戳了几次 —— 越频繁越烦躁。这是可解释、可测的。
///   ② **主观部分（模型写的）**：模型可以在回复里给 mood 字段，一句话说明它现在的心情
///      （“被戳烦了”“今天心情不错”），代码存下来，下一次提示词里带上。
/// 心情只影响行为倾向（还戳不戳回去、话多话少），不改变安全阀（频率门、防编造那些照旧）。
/// </summary>
public sealed class MoodStore
{
    /// <summary>滚动窗口：只数窗口内被戳过几次，过期的不算（“烦”会随时间自己消）。</summary>
    private static readonly TimeSpan PokeWindow = TimeSpan.FromMinutes(15);

    /// <summary>窗口内被戳到第几次就不想回戳了（代码侧硬拦，不看模型怎么说）。</summary>
    private const int PokeBackLimit = 2;

    /// <summary>
    /// 模型写的心情保留多久。超过这个时间没再更新就认为“过时了”，回落到按被戳次数自动描述 ——
    /// 否则早上写的“被戳烦了”会一路影响整天（面板上还会显示“这是 692 分钟前的心情”，很迷惑）。
    /// 由设置项 MoodTtlSeconds 控制（默认 2 小时）。
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(2);

    private readonly object _gate = new();
    private readonly List<long> _pokeTimes = new();
    private string _filePath = string.Empty;

    /// <summary>模型写的心情（一句话）；空 = 还没写过，用代码按被戳次数描述。</summary>
    public string? Text { get; private set; }

    /// <summary>心情更新时间（用来在提示词里说明“这是多久前的心情”）。</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    public void Load(string dataRoot)
    {
        _filePath = Path.Combine(dataRoot, "data", "mood.json");
        lock (_gate)
        {
            _pokeTimes.Clear();
            Text = null;
            UpdatedAt = null;

            try
            {
                if (!File.Exists(_filePath))
                {
                    return;
                }

                var state = JsonSerializer.Deserialize<PersistedMood>(File.ReadAllText(_filePath, Encoding.UTF8));
                if (state is null)
                {
                    return;
                }

                Text = string.IsNullOrWhiteSpace(state.Text) ? null : state.Text.Trim();
                UpdatedAt = state.UpdatedAt is null ? null : DateTimeOffset.FromUnixTimeSeconds(state.UpdatedAt.Value);
                foreach (var t in state.PokeTimes ?? new List<long>())
                {
                    _pokeTimes.Add(t);
                }

                Prune(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                FileLog.Warn("Mood", $"心情状态读取失败（按无心情继续）：{ex.Message}");
            }
        }
    }

    /// <summary>记一次“机器人被戳”（心情的客观来源）。</summary>
    public void RecordPoke(DateTimeOffset at)
    {
        lock (_gate)
        {
            _pokeTimes.Add(at.ToUnixTimeSeconds());
            Prune(at);
            SaveLocked();
        }
    }

    /// <summary>窗口内被戳了几次。</summary>
    public int RecentPokes(DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _pokeTimes.Count;
        }
    }

    /// <summary>
    /// 现在还想不想回戳。被戳太频繁（<see cref="PokeBackLimit"/> 次以内还行）就不想了 ——
    /// 这是“不必每次都戳回去”的代码侧兜底：模型想戳也拦下，并写明理由。
    /// </summary>
    public bool WillPokeBack(DateTimeOffset now, out string reason)
    {
        var count = RecentPokes(now);
        if (count >= PokeBackLimit)
        {
            reason = $"最近 {PokeWindow.TotalMinutes:F0} 分钟已经被戳 {count} 次了，现在没心情回戳";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>模型自己写的心情（空 = 忽略；太长的不收，避免污染提示词）。</summary>
    public bool SetText(string? text, DateTimeOffset now)
    {
        var cleaned = text?.Trim().Trim('。', '.', '！', '!', '，', ',');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length > 24)
        {
            return false;
        }

        lock (_gate)
        {
            if (cleaned == Text)
            {
                return false; // 没变就不写盘，也不刷日志
            }

            Text = cleaned;
            UpdatedAt = now;
            SaveLocked();
        }

        return true;
    }

    /// <summary>给提示词用的一段话（心情 + 客观依据）。永远有值，模型才知道自己“现在什么状态”。</summary>
    public string Describe(DateTimeOffset now)
    {
        ExpireIfStale(now);

        var count = RecentPokes(now);
        var auto = count switch
        {
            0 => "心情挺平静的",
            1 => "还好，就是刚才被戳了一下",
            _ => $"有点烦，{PokeWindow.TotalMinutes:F0} 分钟内被戳了 {count} 次"
        };

        var text = string.IsNullOrWhiteSpace(Text) ? auto : Text!;
        if (string.IsNullOrWhiteSpace(Text))
        {
            return text;
        }

        var age = UpdatedAt is null ? string.Empty : $"，这是 {(now - UpdatedAt.Value).TotalMinutes:F0} 分钟前的心情";
        return count > 0 ? $"{text}（最近 {PokeWindow.TotalMinutes:F0} 分钟被戳 {count} 次{age}）" : $"{text}{age}";
    }

    /// <summary>面板显示用：过期的（模型写的那句）要当作没有，否则界面上留着一句早就不算数的心情。</summary>
    public string? CurrentText(DateTimeOffset now)
    {
        ExpireIfStale(now);
        lock (_gate)
        {
            return Text;
        }
    }

    /// <summary>
    /// 过期的模型心情当作没写过（顺便落盘清掉）。
    /// 为什么在读取时就清：“过期”是个时间事实，不需要定时器；下一次用到它时顺手收掉最省事。
    /// </summary>
    private void ExpireIfStale(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(Text) || UpdatedAt is null || Ttl <= TimeSpan.Zero)
            {
                return;
            }

            var age = now - UpdatedAt.Value;
            if (age <= Ttl)
            {
                return;
            }

            FileLog.Write("Mood", $"心情“{Text}”已过期（{age.TotalHours:F1} 小时没更新，上限 {Ttl.TotalHours:F1} 小时）→ 回落到按被戳次数自动描述");
            Text = null;
            SaveLocked();
        }
    }

    /// <summary>面板手改心情（留空 = 交回代码按被戳次数自动描述）。</summary>
    public void Reset(DateTimeOffset now)
    {
        lock (_gate)
        {
            Text = null;
            UpdatedAt = now;
            SaveLocked();
        }
    }

    /// <summary>过期时间点丢掉：窗口外的戳不该继续影响心情。必须在锁内调用。</summary>
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.ToUnixTimeSeconds() - (long)PokeWindow.TotalSeconds;
        _pokeTimes.RemoveAll(t => t < cutoff);
        if (_pokeTimes.Count > 50)
        {
            _pokeTimes.RemoveRange(0, _pokeTimes.Count - 50);
        }
    }

    private void SaveLocked()
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(new PersistedMood
            {
                Text = Text,
                UpdatedAt = UpdatedAt?.ToUnixTimeSeconds(),
                PokeTimes = _pokeTimes.ToList()
            }, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

            File.WriteAllText(_filePath, json, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            FileLog.Warn("Mood", $"心情状态写盘失败：{ex.Message}");
        }
    }

    private sealed class PersistedMood
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("updatedAt")] public long? UpdatedAt { get; set; }
        [JsonPropertyName("pokeTimes")] public List<long>? PokeTimes { get; set; }
    }
}
