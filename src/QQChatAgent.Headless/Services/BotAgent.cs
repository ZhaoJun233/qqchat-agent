using QQChatAgent.Models;
using QQChatAgent.Services.Agent;
using QQChatAgent.Services.OneBot;
using QQChatAgent.Services.Qq;
using System.Text;

using QQChatAgent.Services.Stickers;
using QQChatAgent.Services.Music;
using QQChatAgent.Services.Links;
using QQChatAgent.Services.Voice;
using QQChatAgent.Services.Net;

namespace QQChatAgent.Services;

/// <summary>
/// 机器人主控（headless 版 Agent 循环）。
/// 这是桌面版 <c>ChatPageViewModel</c> 里 Agent 逻辑的无 UI 移植：
/// 白名单过滤 → 落会话 → 写人物档案 → 冷却判断 → 串行请求模型 → 分句发送 → 持久化。
///
/// 与桌面版的差异：
///   • 无 DispatcherQueue/UI 线程，全部在后台线程；并发由锁与串行 worker 保证。
///   • AI 模式由配置控制（容器里没有"AI 开/关"按钮）。
///   • 群历史在首次收到该群消息时拉取（而非"用户点开会话时"）。
///   • 回复支持按句切分、带节奏发送（README 声称的行为，桌面版实际未实现）。
/// </summary>
public sealed class BotAgent : IDisposable
{
    private readonly AppSettings _settings;
    private readonly IQqChatSource _source;
    private readonly OpenAiClient _brain;
    private readonly ConversationStore _store;
    private readonly MemberProfileStore _profiles;

    private readonly object _conversationsGate = new();
    private readonly List<BotConversation> _conversations = new();

    // ───── 表情包（全局共用一个库，不分会话）─────
    private readonly StickerStore _stickers = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _stickerDescribeQueue = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _stickerDescribeQueued = new();
    private Timer? _stickerTimer;
    private int _stickerBusy;
    private long _lastStickerCurateAt;
    private long _stickerDescribeDone;

    /// <summary>表情包库（面板用）。</summary>
    public StickerStore Stickers => _stickers;

    /// <summary>面板用：当前心情（读/写）。写空串 = 交回代码按被戳次数自动描述。</summary>
    public string MoodText => _mood.CurrentText(DateTimeOffset.Now) ?? string.Empty;

    public string MoodSummary => _mood.Describe(DateTimeOffset.Now);

    public void SetMood(string? text)
    {
        var now = DateTimeOffset.Now;
        if (string.IsNullOrWhiteSpace(text))
        {
            _mood.Reset(now);
            EmitLog("心情已交回自动描述（按被戳次数）");
            return;
        }

        if (_mood.SetText(text, now))
        {
            EmitLog($"心情被手动改成：{_mood.Describe(now)}");
        }
    }

    /// <summary>正在等待生成说明的张数。</summary>
    public int StickerPendingDescribe => _stickerDescribeQueue.Count;

    /// <summary>已生成说明的张数（含历史累计）。</summary>
    public long StickerDescribeDone => Interlocked.Read(ref _stickerDescribeDone);

    private HashSet<long> _whitelist = new();
    private bool _whitelistAll;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _historyRequested = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _replyCooldown = new();

    // 待回复队列：**每个会话一条 FIFO 链**。
    // 要点：同一会话的请求必须严格按触发顺序执行（否则回复会错位、引用判定会错），
    //       而不同会话之间可以并发，互不阻塞。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<long?>> _pendingReplies = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BotConversation> _pendingConversations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new();
    private SemaphoreSlim _replyGate;
    private int _replyWorkerRunning;
    private volatile int _replyGatePermits;
    private long _lastActiveRequestTicks = DateTime.MinValue.Ticks; // 最近一次主动请求时间（原子读写）

    /// <summary>当前在途的模型请求数（可观测）。</summary>
    public int InFlightReplies => _inFlight.Count;

    /// <summary>
    /// QQ 账号是否真的在线（null = 未知）。
    /// 与“协议端连接状态”是两件事：连接可以一直是连着的，而账号已经掉线收不到任何消息。
    /// </summary>
    public bool? AccountOnline => Volatile.Read(ref _accountOnline) is var v && v >= 0 ? v == 1 : null;

    /// <summary>当前排队的待回复请求数（可观测）。</summary>
    public int QueuedReplies => _pendingReplies.Values.Sum(q => q.Count);

    // ══════════ 长期记忆：画像摘要 ══════════

    /// <summary>已完成的画像摘要次数（可观测）。</summary>
    public long SummaryDoneCount => Interlocked.Read(ref _summaryDoneCount);

    /// <summary>画像摘要失败次数（可观测）。</summary>
    public long SummaryFailCount => Interlocked.Read(ref _summaryFailCount);

    /// <summary>
    /// 画像巡检：把各会话里新积累的发言用模型压缩成人物画像。
    /// 好处：以后注入的是“一段画像”而不是“几十条原文”，token 降一个数量级且信息密度更高。
    /// 只在后台跑，且独占 1 个并发位，不与回复抢资源。
    /// </summary>
    private async Task RunProfileSummarizationAsync()
    {
        if (!_settings.EnableProfileSummary || _disposed == 1)
        {
            return;
        }

        if (Interlocked.Exchange(ref _summaryRunning, 1) == 1)
        {
            return; // 上一轮还没跑完
        }

        try
        {
            var candidates = _profiles.FindSummarizable(_settings.ProfileSummaryThreshold, maxCandidates: 20);

            foreach (var c in candidates)
            {
                if (_disposed == 1)
                {
                    break;
                }

                await _summaryGate.WaitAsync();
                try
                {
                    var text = await _brain.SummarizePersonaAsync(
                        string.IsNullOrWhiteSpace(c.Name) ? $"QQ{c.Uid}" : c.Name,
                        c.ExistingSummary,
                        c.NewMessages,
                        _settings.ProfileSummaryMaxChars);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Interlocked.Increment(ref _summaryFailCount);
                        continue;
                    }

                    _profiles.ApplySummary(c.Uid, c.Scope, text, c.ThroughSeq, c.FoldedCount);
                    Interlocked.Increment(ref _summaryDoneCount);
                    EmitLog($"画像已生成：{c.Name}({c.Uid}) @ {c.Scope}（{c.NewMessages.Count} 条 → {text.Length} 字）");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _summaryFailCount);
                    FileLog.Write("Profiles", $"画像摘要失败 {c.Uid}@{c.Scope}: {ex.Message}");
                }
                finally
                {
                    _summaryGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write("Profiles", "画像巡检异常: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _summaryRunning, 0);
        }
    }

    /// <summary>最近一次主动请求时间（静默兜底判断用；跨线程原子访问）。</summary>
    private DateTime LastActiveRequestTime
    {
        get => new(Volatile.Read(ref _lastActiveRequestTicks));
        set => Volatile.Write(ref _lastActiveRequestTicks, value.Ticks);
    }

    private Timer? _idleTimer;
    private Timer? _summaryTimer;
    private int _summaryRunning;

    // QQ 账号在线探测：-1 = 未知，0 = 离线，1 = 在线
    private Timer? _healthTimer;
    private int _healthRunning;
    private int _accountOnline = -1;

    // 定时器当前依据的配置值：用于判断“是否真的需要重建”（避免每次保存都重置计时）
    private int _timerIdleSeconds = -1;
    private bool _timerSummaryEnabled;
    private int _timerSummarySeconds = -1;
    private bool _timerStickersEnabled;
    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private long _summaryDoneCount;
    private long _summaryFailCount;
    private long _selfId;
    private int _disposed;

    public BotAgent(
        AppSettings settings,
        IQqChatSource source,
        OpenAiClient brain,
        ConversationStore store,
        MemberProfileStore profiles)
    {
        _settings = settings;
        _source = source;
        _brain = brain;
        _store = store;
        _profiles = profiles;

        (_whitelist, _whitelistAll) = ParseWhitelist(settings.MessageWhitelist);
        _replyGate = new SemaphoreSlim(Math.Clamp(settings.MaxConcurrentReplies, 1, 16));
        _replyGatePermits = Math.Clamp(settings.MaxConcurrentReplies, 1, 16);
    }

    /// <summary>待回复的一条请求（会话 + 触发消息）。</summary>
    /// <summary>日志节流：同一来源的“忽略”类日志最多每分钟一条（否则忙群里会刷爆）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _noisyLogAt = new();

    // ══════════ Web UI 事件与操控接口 ══════════

    /// <summary>新消息落库（sourceKey, 消息）。</summary>
    public event Action<string, ChatMessage>? MessageAdded;

    /// <summary>会话发生变更（新建/改名/未读变化）。</summary>
    public event Action? ConversationsChanged;

    /// <summary>连接状态或 AI 开关变化。</summary>
    public event Action? StateChanged;

    /// <summary>某个会话的「AI 正在思考」状态变化。</summary>
    public event Action<string>? ThinkingChanged;

    /// <summary>一条面向 UI 的运行日志。</summary>
    public event Action<string>? LogLine;

    /// <summary>AI 自动回复开关（可运行时切换，对应桌面版的「AI 开/关」按钮）。</summary>
    public bool AiModeEnabled
    {
        get => _settings.AiModeEnabled;
        set
        {
            if (_settings.AiModeEnabled == value)
            {
                return;
            }

            _settings.AiModeEnabled = value;
            EmitLog(value ? "AI 模式已开启" : "AI 模式已关闭");
            StateChanged?.Invoke();
        }
    }

    /// <summary>当前生效的配置（Web UI 展示与修改用）。</summary>
    public AppSettings Settings => _settings;

    /// <summary>
    /// 运行时改配置：白名单/人设/欲望/阈值/限流等立即生效，并落盘 settings.json。
    /// 模型地址与 OneBot 地址不在运行时生效（属于容器环境变量职责），由 UI 明确标注。
    /// </summary>
    /// <summary>把“心情保留多久”从设置同步给 MoodStore（0 = 不过期）。</summary>
    private void ApplyMoodTtl() => _mood.Ttl = TimeSpan.FromSeconds(Math.Max(0, _settings.MoodTtlSeconds));

    /// <summary>
    /// 组装“听音乐”服务。数据放 data/music/：台账（listened.json）常驻，音频默认分析完就丢
    /// （只在 MusicKeepAudio 打开时才留在 data/music/audio/）。
    /// </summary>
    private void BuildMusicService()
    {
        var dataDir = Path.Combine(AppPaths.DataDir, "music");
        var store = new MusicStore(Path.Combine(dataDir, "listened.json"), () => _settings.MusicLibraryMax, EmitLog);
        var netease = new NeteaseMusicClient(_musicHttp, () => _settings.NeteaseCookie, () => _settings.NeteaseBaseUrl, EmitLog);
        var audio = new MusicAudioResolver(
            _musicHttp,
            () => _settings.MusicSources.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            () => Math.Clamp(_settings.MusicBitrate, 32, 320),
            () => Math.Clamp(_settings.MusicMaxDownloadMb, 1, 64) * 1024 * 1024,
            EmitLog);
        _music = new MusicService(store, netease, audio, _brain, () => _settings, Path.Combine(dataDir, "audio"), EmitLog);
        _links = new LinkPreviewer(_musicHttp, () => _settings, EmitLog);
    }

    /// <summary>
    /// 把模型配置同步给大脑（面板里改 Base URL / 模型名 / 密钥 后调用）。
    /// 双方平时就是同一个 AppSettings 实例，这一步是幂等的；
    /// 之所以显式留着：万一以后谁改成传副本，也不至于“面板改了但请求还走旧地址”。
    /// </summary>
    public void SyncModelSettings() => _brain.UpdateSettings(_settings);

    public void ApplyRuntimeSettings(Action<AppSettings> mutate)
    {
        mutate(_settings);
        ApplyMoodTtl();

        // 白名单重新解析并清理已有会话
        (_whitelist, _whitelistAll) = ParseWhitelist(_settings.MessageWhitelist);
        PruneNonWhitelisted();

        // 同步到模型客户端
        _brain.BotIdentity = string.IsNullOrWhiteSpace(_settings.NormalizedUin) ? null : _settings.NormalizedUin;
        _brain.BotPersona = _settings.BotPersona;
        _brain.AiDesire = _settings.AiDesire;
        _brain.SuitabilityThreshold = _settings.SuitabilityThreshold;
        _brain.MaxContextMessages = _settings.MaxContextMessages;

        // 全局并发数变更时重建闸门
        var permits = Math.Clamp(_settings.MaxConcurrentReplies, 1, 16);
        if (permits != _replyGatePermits)
        {
            _replyGatePermits = permits;

            // 整体替换，而不是 Dispose 旧的：
            // 此刻可能已有 worker 正 await 在旧闸门上，Dispose 会让它抛 ObjectDisposedException，
            // 而该异常会跳过 _inFlight 的清理 —— 结果是该会话**永久不再回复**。
            // 旧对象只有几十字节，且配置变更由人触发（低频），延迟回收即可。
            var previous = _replyGate;
            _replyGate = new SemaphoreSlim(permits);
            RetireGate(previous);
            EmitLog($"模型并发上限已改为 {permits}");
        }

        // 消息窗口变更时同步到已有会话，并立即裁一次（否则要等下一条消息才看得到效果）
        foreach (var c in Conversations)
        {
            c.MaxMessages = _settings.MaxMessagesPerConversation;
            c.TrimToMax();
        }

        // 定时器类配置（静默兜底 / 画像巡检）：只在真的改了时才重建，否则不生效
        RebuildTimersIfNeeded();

        SettingsStore.Save(_settings);
        EmitLog("配置已更新");
        ConversationsChanged?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 按当前配置重建定时器。
    ///
    /// 为什么必须有这个：定时器以前只在 <see cref="Start"/> 里创建一次，
    /// 于是面板里改「启用画像摘要 / 巡检间隔 / 静默兜底」都不生效（要重启才变），
    /// 用起来就像“设置保存不了”。
    ///
    /// 另外：只在值真的变了时才重建 —— 否则每保存一次设置就把计时归零，
    /// 间隔 120s 而用户频繁保存的话，巡检永远不会触发。
    /// </summary>
    private void RebuildTimersIfNeeded()
    {
        var idleSeconds = Math.Max(0, _settings.IdleFallbackSeconds);
        var summaryEnabled = _settings.EnableProfileSummary;
        var summarySeconds = Math.Max(0, _settings.ProfileSummaryIntervalSeconds);
        var stickersEnabled = _settings.EnableStickers && _settings.StickerLibraryMax > 0;

        if (idleSeconds == _timerIdleSeconds &&
            summaryEnabled == _timerSummaryEnabled &&
            summarySeconds == _timerSummarySeconds &&
            stickersEnabled == _timerStickersEnabled)
        {
            return;
        }

        _timerIdleSeconds = idleSeconds;
        _timerSummaryEnabled = summaryEnabled;
        _timerSummarySeconds = summarySeconds;
        _timerStickersEnabled = stickersEnabled;

        // ---- 静默兜底 ----
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (idleSeconds > 0 && _disposed == 0)
        {
            _idleTimer = new Timer(
                _ => IdleFallbackTick(),
                null,
                TimeSpan.FromSeconds(idleSeconds),
                TimeSpan.FromSeconds(idleSeconds));
        }

        // ---- 长期记忆：画像巡检 ----
        _summaryTimer?.Dispose();
        _summaryTimer = null;
        if (summaryEnabled && summarySeconds > 0 && _disposed == 0)
        {
            _summaryTimer = new Timer(
                _ => _ = RunProfileSummarizationAsync(),
                null,
                // 首次很快跑一轮（刚打开就能看到效果），之后按配置间隔
                TimeSpan.FromSeconds(Math.Min(5, summarySeconds)),
                TimeSpan.FromSeconds(summarySeconds));
        }

        // ---- 表情包：补说明 + 自巡检 ----
        _stickerTimer?.Dispose();
        _stickerTimer = null;
        if (stickersEnabled && _disposed == 0)
        {
            // 10 秒一小步：刚收的图很快就能补上说明（不然检索不到它）
            _stickerTimer = new Timer(_ => _ = StickerTickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            // 启动后稍等一会儿先补一轮：重启后积压的未识别图 + 审核功能上线前入库的旧图
            foreach (var item in _stickers.Snapshot().Where(s => (!s.Described || s.IsSticker is null) && s.DescribeAttempts < 2).Take(30))
            {
                QueueStickerDescribe(item.Id);
            }
        }

        EmitLog($"定时器已按新配置重建（静默兜底 {idleSeconds}s，画像巡检 {(summaryEnabled && summarySeconds > 0 ? summarySeconds + "s" : "关")}，" +
                $"表情包巡检 {(stickersEnabled && _settings.StickerCurateIntervalSeconds > 0 ? _settings.StickerCurateIntervalSeconds + "s" : "关")}）");
    }

    /// <summary>
    /// 把群友发的图收进表情包库（同一张内容只存一份），并排队让模型生成说明/关键词。
    /// 完全不阻塞接收线程（下载、写盘、模型调用都在这里）。
    /// </summary>
    private async Task CollectStickersAsync(List<string> urls, string fromUid, long fromGroup)
    {
        try
        {
            var added = 0;
            foreach (var url in urls.Take(3))
            {
                if (_settings.StickerLibraryMax <= 0)
                {
                    break;
                }

                var downloaded = await _brain.DownloadImageAsync(url, CancellationToken.None);
                if (downloaded is not { } image)
                {
                    continue;
                }

                var record = _stickers.Add(image.Data, image.Ext, fromUid, fromGroup);
                if (record is null)
                {
                    continue; // 重复图
                }

                added++;
                QueueStickerDescribe(record.Id);
            }

            if (added == 0)
            {
                return;
            }

            var evicted = _stickers.EnforceLimit(_settings.StickerLibraryMax);
            EmitLog($"表情包库 +{added} 张（现有 {_stickers.Count}/{_settings.StickerLibraryMax}）" +
                    (evicted.Count > 0 ? $"，超限淘汰 {evicted.Count} 张" : string.Empty));
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"收集表情包失败：{ex.Message}");
        }
    }

    private void QueueStickerDescribe(string id)
    {
        if (_stickerDescribeQueued.TryAdd(id, 0))
        {
            _stickerDescribeQueue.Enqueue(id);
        }
    }

    /// <summary>表情包定时器：先补说明，再看要不要自巡检。</summary>
    private async Task StickerTickAsync()
    {
        if (_disposed != 0 || Interlocked.Exchange(ref _stickerBusy, 1) == 1)
        {
            return;
        }

        try
        {
            await DrainStickerDescribeAsync();

            var interval = Math.Max(0, _settings.StickerCurateIntervalSeconds);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (interval > 0 && now - Volatile.Read(ref _lastStickerCurateAt) >= interval)
            {
                Volatile.Write(ref _lastStickerCurateAt, now);
                await CurateStickersAsync(force: false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包巡检异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _stickerBusy, 0);
        }
    }

    /// <summary>给没说明的图补上“一句话说明 + 关键词”（没有它就没法按语境检索）。</summary>
    private async Task DrainStickerDescribeAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            if (!_stickerDescribeQueue.TryDequeue(out var id))
            {
                return;
            }

            _stickerDescribeQueued.TryRemove(id, out _);

            var item = _stickers.Find(id);
            if (item is null || item.DescribeAttempts >= 2)
            {
                continue;
            }

            // 已描述过但还没审核过的（审核功能上线前入库的旧图）也要补审一次
            if (item.Described && item.IsSticker is not null)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(item.AbsolutePath);
            }
            catch
            {
                _stickers.MarkDescribeFailed(id);
                continue;
            }

            var mime = item.Ext switch
            {
                "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                _ => "image/png"
            };

            var (desc, tags, isSticker) = await _brain.DescribeStickerAsync(bytes, mime);
            if (desc is null && tags is null && isSticker is null)
            {
                _stickers.MarkDescribeFailed(id);
                continue;
            }

            // 审核不通过（聊天截图 / 广告 / 纯文字图…）→ 立即丢掉。
            // 线上实测把群友发的**聊天截图**当表情包收了、还准备发出去 —— 这一步就是闸门。
            if (isSticker == false)
            {
                _stickers.Remove(id, "审核：不像表情包（截图/广告/纯文字图）");
                EmitLog($"表情包审核不通过，已丢弃 #{id}：{desc ?? "(无描述)"}");
                continue;
            }

            _stickers.SetDescription(id, desc, tags, isSticker);
            Interlocked.Increment(ref _stickerDescribeDone);
            EmitLog($"表情包入库：#{id} {StickerStore.Describe(_stickers.Find(id) ?? item)}");
        }
    }

    /// <summary>
    /// 让机器人自己巡检表情包库，决定删哪些（用户要求：它应自己删/加）。
    /// 保护规则：24 小时内用过的代码侧直接拦下；一次最多删库里 1/5（至少能给模型 2 个名额）。
    /// </summary>
    public async Task<string> CurateStickersAsync(bool force)
    {
        var all = _stickers.Snapshot();
        if (all.Count == 0)
        {
            return "库里还没有表情包";
        }

        if (!force && all.Count < 6)
        {
            return $"只有 {all.Count} 张，先不删";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var table = string.Join("\n", all.Take(200).Select(s =>
            $"{s.Id} | {StickerStore.Describe(s)} | 用过 {s.Uses} 次 | {(now - s.AddedAt) / 3600} 小时前收藏" +
            (s.LastUsedAt > 0 && now - s.LastUsedAt < 86400 ? " | 24h内用过" : string.Empty)));

        var maxDelete = Math.Max(2, all.Count / 5);
        var (wanted, reason) = await _brain.CurateStickersAsync(table, maxDelete);
        if (wanted.Count == 0)
        {
            EmitLog($"表情包巡检：不删（{all.Count} 张）" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"——{reason}"));
            return "本次没有需要删除的";
        }

        var deleted = 0;
        foreach (var id in wanted)
        {
            var item = _stickers.Find(id);
            if (item is null)
            {
                continue;
            }

            // 代码兜底：刚用过的别删（模型有时看不见标注）
            if (item.LastUsedAt > 0 && now - item.LastUsedAt < 86400)
            {
                continue;
            }

            if (_stickers.Remove(id, "自巡检"))
            {
                deleted++;
            }
        }

        var summary = $"表情包巡检：删除 {deleted} 张（{reason ?? "模型未给理由"}）";
        EmitLog(summary + $"，现有 {_stickers.Count}/{_settings.StickerLibraryMax} 张");

        // 巡检完顺手把容量压回上限（例如用户把上限改小了）
        _stickers.EnforceLimit(_settings.StickerLibraryMax);
        return summary;
    }

    /// <summary>
    /// 从登录账号在 QQ 里的“收藏表情”导入一批（机器人自己“添加”表情包的来源）。
    /// 拉图/存图失败都只是跳过，不影响其它功能。
    /// </summary>
    public async Task<string> ImportStickersFromAlbumAsync(int limit = 30)
    {
        if (_settings.StickerLibraryMax <= 0)
        {
            return "表情包库上限为 0，先在设置里放开";
        }

        var urls = await _source.FetchCustomFacesAsync(Math.Clamp(limit, 1, 200));
        if (urls.Count == 0)
        {
            return "协议端没有返回收藏表情（可能未登录，或协议端不支持 fetch_custom_face）";
        }

        var added = 0;
        foreach (var url in urls)
        {
            var downloaded = await _brain.DownloadImageAsync(url, CancellationToken.None);
            if (downloaded is not { } image)
            {
                continue;
            }

            var record = _stickers.Add(image.Data, image.Ext, null, 0);
            if (record is null)
            {
                continue;
            }

            added++;
            QueueStickerDescribe(record.Id);
        }

        var evicted = _stickers.EnforceLimit(_settings.StickerLibraryMax);
        var summary = $"从 QQ 收藏表情导入 {added} 张（收到 {urls.Count} 个地址，现有 {_stickers.Count}/{_settings.StickerLibraryMax}）" +
                      (evicted.Count > 0 ? $"，超限淘汰 {evicted.Count} 张" : string.Empty);
        EmitLog(summary);
        return summary;
    }

    /// <summary>以机器人身份向会话发送一条消息（对应桌面版的输入框）。</summary>
    public async Task<bool> SendAsBotAsync(string sourceKey, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var conversation = Find(sourceKey);
        if (conversation is null)
        {
            return false;
        }

        var (isGroup, targetId) = conversation.Target;
        if (targetId == 0 || !_source.IsConnected)
        {
            return false;
        }

        var ok = await _source.SendTextAsync(isGroup, targetId, text);
        if (ok)
        {
            conversation.Append(new ChatMessage { Role = MessageRole.Self, Text = text });
            Touch(conversation);
            MessageAdded?.Invoke(conversation.SourceKey, conversation.Messages[^1]);
            Save();
        }

        return ok;
    }

    /// <summary>删除会话（连同磁盘记录）。</summary>
    public bool DeleteConversation(string sourceKey)
    {
        BotConversation? conversation;
        lock (_conversationsGate)
        {
            conversation = _conversations.FirstOrDefault(c => c.SourceKey == sourceKey);
            if (conversation is null)
            {
                return false;
            }

            _conversations.Remove(conversation);
        }

        _replyCooldown.TryRemove(sourceKey, out _);
        _historyRequested.TryRemove(sourceKey, out _);
        DropPending(sourceKey); // 清待回复队列：否则在途回复还会发到 QQ，本地记录却已成孤儿
        Save();
        EmitLog($"已删除会话 {conversation.Name}");
        ConversationsChanged?.Invoke();
        return true;
    }

    /// <summary>标记会话已读。</summary>
    public void MarkRead(string sourceKey)
    {
        var conversation = Find(sourceKey);
        if (conversation?.MarkRead() == true)
        {
            ConversationsChanged?.Invoke();
        }
    }

    /// <summary>按 sourceKey 查找会话。</summary>
    public BotConversation? Find(string sourceKey)
    {
        lock (_conversationsGate)
        {
            return _conversations.FirstOrDefault(c => c.SourceKey == sourceKey);
        }
    }

    /// <summary>人物档案摘要（Web UI 查看成员用）。
    /// allScopes：面板需要看全部会话的画像（含群聊），否则只能看到私聊记录。
    /// 该视图**不用于模型注入**，模型注入始终按会话隔离。</summary>
    public string GetProfileSummary(string uid) => _profiles.GetProfileSummary(uid, limit: 30, allScopes: true);

    /// <summary>已跟踪的会话快照（按最后活跃时间倒序）。</summary>
    public IReadOnlyList<BotConversation> Conversations
    {
        get
        {
            lock (_conversationsGate)
            {
                return _conversations.ToArray();
            }
        }
    }

    private int _savePending;
    private long _saveVersion;

    /// <summary>连接的 QQ 号（协议端上报优先，其次配置）。0 = 未知。</summary>
    public long SelfId => _selfId;

    /// <summary>启动：订阅消息源、恢复磁盘会话、启动静默兜底定时器。</summary>
    public void Start()
    {
        _source.MessageReceived += OnMessageReceived;
        _source.ConnectionChanged += OnConnectionChanged;
        _source.Poked += OnPoked;
        _source.MessageRecalled += OnMessageRecalled;

        RestoreConversations();
        _stickers.Load(AppPaths.RuntimeRoot);
        _mood.Load(AppPaths.RuntimeRoot);
        ApplyMoodTtl();
        BuildMusicService();
        _voice = new VoiceService(_voiceHttp, () => _settings, EmitLog);
        _search = new WebSearchService(_netHttp, () => _settings, EmitLog);

        RebuildTimersIfNeeded(); // 静默兜底 + 画像巡检 + 表情包巡检（运行时改配置走同一段逻辑）

        // QQ 账号在线探测：30 秒一轮（连接建立后会立即先探一次）
        _healthTimer = new Timer(_ => _ = CheckAccountOnlineAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));

        // 白名单严格模式提示：空名单 = 全部忽略，容器里很容易踩
        if (!_whitelistAll && _whitelist.Count == 0)
        {
            FileLog.Warn("Agent",
                "消息白名单为空 → 严格模式下将忽略所有消息。若要接收全部群/私聊，请设 " +
                "QQCHAT_WHITELIST='*'（或填具体群号/QQ号）。");
        }

        FileLog.Write("Agent",
            $"已启动。AI={( _settings.AiModeEnabled ? "开" : "关")}, " +
            $"白名单={(_whitelistAll ? "* (全部)" : _whitelist.Count == 0 ? "(空，忽略全部)" : string.Join(",", _whitelist))}, " +
            $"模型={_settings.Model}, 人设={(string.IsNullOrWhiteSpace(_settings.BotPersona) ? "无" : "已配置")}, " +
            $"表情包={(_settings.EnableStickers ? $"开（{_stickers.Count}/{_settings.StickerLibraryMax} 张，已描述 {_stickers.DescribedCount}）" : "关")}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _source.MessageReceived -= OnMessageReceived;
        _source.ConnectionChanged -= OnConnectionChanged;
        _source.Poked -= OnPoked;
        _source.MessageRecalled -= OnMessageRecalled;
        _idleTimer?.Dispose();
        _summaryTimer?.Dispose();
        _stickerTimer?.Dispose();
        _healthTimer?.Dispose();
        _summaryGate.Dispose();
    }

    // ---------- 连接与自我识别 ----------

    private void OnConnectionChanged(bool connected)
    {
        EmitLog(connected ? "QQ 已连接（协议端在线）" : "QQ 连接断开，等待重连…");

        if (!connected)
        {
            // 连接都没了，账号在线状态回到未知，等重连后再探
            Interlocked.Exchange(ref _accountOnline, -1);
        }

        StateChanged?.Invoke();
        if (connected)
        {
            _ = RefreshSelfIdAsync();
        }
    }

    /// <summary>连接后向协议端确认登录号，用于 @ 识别与身份注入。</summary>
    private async Task RefreshSelfIdAsync()
    {
        try
        {
            if (_source is not OneBotGateway gateway)
            {
                return;
            }

            // 给协议端一点握手时间（NapCat 刚连上时 get_login_info 可能还没就绪）
            await Task.Delay(1500);
            var id = await gateway.GetSelfIdAsync();
            if (id is > 0)
            {
                _selfId = id.Value;
                gateway.SelfIdHint = id.Value;
                _brain.BotIdentity = id.Value.ToString();
                EmitLog($"登录账号确认：QQ {id.Value}");
            }

            // 紧接一次账号在线探测：连接刚建立时就能发现“连上了但账号已掉线”
            await CheckAccountOnlineAsync();
        }
        catch (Exception ex)
        {
            EmitLog("获取登录账号失败: " + ex.Message);
        }
    }

    // ---------- 入站消息 ----------

    private void OnMessageReceived(QqChatMessage msg)
    {
        try
        {
            HandleInbound(msg);
        }
        catch (Exception ex)
        {
            EmitLog("处理入站消息异常: " + ex.Message);
        }
    }

    private void HandleInbound(QqChatMessage msg)
    {
        if (!IsWhitelisted(msg))
        {
            // 忙群里这类日志会把日志文件和面板刷爆 → 同一来源每分钟最多一条
            var label = msg.IsGroup ? "群 " + msg.GroupId : "私聊 " + msg.UserId;
            LogThrottled("ignore:" + label, $"忽略（不在白名单）: {label}");
            return;
        }

        // 机器人自己发的消息不入库（协议端可能回显）
        if (_selfId != 0 && msg.UserId == _selfId)
        {
            return;
        }

        var conversation = GetOrCreateConversation(msg);

        var appended = new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderName = msg.IsGroup ? msg.SenderName : null,
            SenderId = msg.UserId,
            Text = msg.Text,
            Timestamp = msg.Time,
            QqMessageId = msg.MessageId,
            ImageUrls = msg.ImageUrls
        };
        conversation.Append(appended);
        conversation.HasPendingReply = true;
        Touch(conversation);
        MessageAdded?.Invoke(conversation.SourceKey, appended);
        Save();
        // 人物档案（帮助模型认识群友/好友）
        var botUin = _settings.NormalizedUin;
        var isSelfSender = !string.IsNullOrWhiteSpace(botUin) && msg.UserId.ToString() == botUin;
        if (!isSelfSender)
        {
            _profiles.Append(
                msg.UserId.ToString(),
                msg.SenderName,
                msg.Text,
                msg.Time,
                msg.IsGroup ? conversation.Name : null,
                msg.IsGroup ? msg.GroupId : 0,
                appended.Seq);
        }

        EmitLog(
            $"收到 {(msg.IsGroup ? $"群{msg.GroupId}" : "私聊")} {msg.SenderName}({msg.UserId}): " +
            $"{(msg.Text.Length > 80 ? msg.Text[..80] + "…" : msg.Text)}" +
            (msg.ImageUrls is { Count: > 0 } ? $" [+{msg.ImageUrls.Count}图]" : string.Empty));

        // 表情包：群友发的图自动收进库（后台下载，不阻塞接收线程）
        if (_settings.EnableStickers && _settings.StickerLibraryMax > 0 && msg.IsGroup && msg.ImageUrls is { Count: > 0 })
        {
            var urls = msg.ImageUrls.ToList();
            var uid = msg.UserId.ToString();
            var group = msg.GroupId;
            _ = Task.Run(() => CollectStickersAsync(urls, uid, group));
        }

        // 听音乐：识别到分享就后台去查歌词 + 下一份低码率音频分析波形。
        // 有歌的时候**先不回复** —— 等分析结果回来再让模型开口，否则它只能对着一个歌名瞎聊。
        var musicShares = _settings.EnableMusic ? msg.MusicShares : null;
        if (musicShares is { Count: > 0 })
        {
            _ = Task.Run(() => HandleMusicAsync(conversation, msg.SenderName, musicShares.ToList()));
        }

        // 链接：群里发的 URL（包括分享卡片里那个）真去打开看一眼，取回标题/摘要。
        // 有音乐分享时跳过 —— 音乐那条路自己会处理链接，不必看两遍。
        var linkUrls = musicShares is { Count: > 0 } || _links is null || !_settings.EnableLinkPreview
            ? []
            : LinkExtractor.Extract(msg.Text, Math.Clamp(_settings.LinkPreviewMax, 0, 5));
        if (linkUrls.Count > 0)
        {
            var key = conversation.SourceKey;
            var urls = linkUrls.ToList();
            var pending = Task.Run(async () =>
            {
                try
                {
                    var note = await _links!.DescribeAsync(urls, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        _linkNotes[key] = note!;
                        EmitLog($"[Link] 已看过 {urls.Count} 个链接：{string.Join("、", urls.Select(u => u.Length > 48 ? u[..48] + "…" : u))}");
                    }
                }
                catch (Exception ex)
                {
                    EmitLog($"[Link] 预览失败: {ex.Message}");
                }
                finally
                {
                    _linkTasks.TryRemove(key, out _);
                }
            });
            _linkTasks[key] = pending;
        }

        // 首个群消息时补历史上下文（同桌面版"点开会话拉历史"）
        if (msg.IsGroup)
        {
            EnsureGroupContext(conversation, msg.GroupId);
        }

        if (_settings.AiModeEnabled && AllowReply(conversation) && musicShares is not { Count: > 0 })
        {
            EnqueueReply(conversation, msg.MessageId > 0 ? msg.MessageId : null);
        }
    }

    /// <summary>
    /// 后台真去搜一次，把结果留给下一轮（并在允许时叫醒模型）。
    /// 为什么要冷却：搜索是一次真实的模型调用 + 几秒等待；群里连问几个问题就排队了。
    /// </summary>
    private void QueueWebSearchAsync(BotConversation conversation, string query)
    {
        var key = conversation.SourceKey;
        var now = DateTimeOffset.Now;
        if (_lastSearch.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(SearchCooldownSeconds))
        {
            EmitLog($"[Search] 这次不搜（同会话 {SearchCooldownSeconds}s 内刚搜过）：{query}");
            return;
        }

        _lastSearch[key] = now;
        EmitLog($"[Search] 模型想搜「{query}」");
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _search!.SearchAsync(query, CancellationToken.None);
                var note = result.Describe();
                _searchNotes.AddOrUpdate(key, note, (_, old) => old + "\n\n" + note);

                if (!result.HasContent)
                {
                    EmitLog($"[Search] 没搜到「{query}」：{result.Error}");
                }

                // 搜到了就给它一次开口机会（没搜到也给 —— 让它能如实说“没查到”）
                if (_settings.AiModeEnabled && AllowReply(conversation))
                {
                    EnqueueReply(conversation, null);
                }
            }
            catch (Exception ex)
            {
                EmitLog($"[Search] 搜「{query}」失败: {ex.Message}");
            }
        });
    }

    /// <summary>后台读一个网页的正文（模型填 read 时），留给下一轮。</summary>
    private void QueuePageReadAsync(BotConversation conversation, string url)
    {
        var key = conversation.SourceKey;
        EmitLog($"[Search] 模型想读页面 {Shorten(url, 80)}");
        _ = Task.Run(async () =>
        {
            try
            {
                var (text, error) = await _search!.ReadPageAsync(url, CancellationToken.None);
                var note = text is null
                    ? $"读页面「{url}」失败：{error}（如实说没读到就行，别猜页面里写了什么。）"
                    : $"页面 {url} 的正文（已抽取）：\n{text}";

                _searchNotes.AddOrUpdate(key, note, (_, old) => old + "\n\n" + note);
                EmitLog(text is null ? $"[Search] 读页面失败：{error}" : $"[Search] 已读到页面正文（{text.Length} 字）");

                if (_settings.AiModeEnabled && AllowReply(conversation))
                {
                    EnqueueReply(conversation, null);
                }
            }
            catch (Exception ex)
            {
                EmitLog($"[Search] 读页面失败: {ex.Message}");
            }
        });
    }

    /// <summary>面板自测：真跑一次搜索，把结果（或失败原因）原样给面板看。</summary>
    public async Task<WebSearchResult> TestSearchAsync(string query, CancellationToken ct)
        => _search is null
            ? new WebSearchResult(query, null, Array.Empty<WebSearchHit>(), "无", "搜索服务还没初始化")
            : await _search.SearchAsync(query, ct);

    /// <summary>面板自测：真读一个页面。</summary>
    public async Task<(string? Text, string? Error)> TestReadPageAsync(string url, CancellationToken ct)
        => _search is null ? (null, "搜索服务还没初始化") : await _search.ReadPageAsync(url, ct);

    /// <summary>最近一次“面板自测听歌”的歌名（给 /api/music/test 回报用）。</summary>
    public string? LastMusicTestHeader { get; private set; }

    /// <summary>语音（TTS）客户端（面板试听用）。</summary>
    public VoiceService? Voice => _voice;

    /// <summary>
    /// 面板自测：真合成一句语音（走 /speak），把 wav 字节还给面板自己播。
    /// 只合成、不发群 —— 面板里重点验证的是“TTS 服务通不通、音色/语速对不对”。
    /// </summary>
    public async Task<(byte[]? Data, string? Error)> TestVoiceAsync(
        string text,
        string? voiceOverride,
        int? speedOverride,
        CancellationToken ct)
    {
        if (_voice is null)
        {
            return (null, "语音服务还没初始化");
        }

        var (data, error) = await _voice.SynthesizeAsync(text, voiceOverride, speedOverride, ct);
        EmitLog(data is null
            ? $"[Voice] 面板试听失败：{error}"
            : $"[Voice] 面板试听成功（{text.Length} 字 → {data.Length / 1024} KB wav）");
        return (data, error);
    }

    /// <summary>
    /// 面板自测：把“听音乐”整套链路跑一遍（搜歌 → 歌词 → 低码率音源 → 波形分析）。
    /// 返回给模型看的“事实描述”（拿不到就返回 null）。
    /// 本方法只在面板自测时用，不影响群里的正常流程。
    /// </summary>
    public async Task<string?> TestMusicAsync(string song, CancellationToken ct)
    {
        LastMusicTestHeader = null;
        if (_music is null)
        {
            return null;
        }

        var note = await _music.DescribeByNameAsync(song, "面板自测", ct);
        LastMusicTestHeader = song;
        EmitLog(note is null
            ? $"[Music] 面板自测「{song}」：没搜到或没听到"
            : $"[Music] 面板自测「{song}」完成");
        return note;
    }

    /// <summary>
    /// 听音乐：拿歌词 + 低码率音频做波形分析，把实测到的事实留给下一轮回复。
    /// 分析完成后单独触发一次发言机会 —— 这样模型是“听完再说”，而不是先瞎猜一遍再补课。
    /// </summary>
    private async Task HandleMusicAsync(BotConversation conversation, string sender, List<MusicShare> shares)
    {
        if (_music is null)
        {
            return;
        }

        var heard = false;
        foreach (var share in shares)
        {
            try
            {
                var note = await _music.DescribeAsync(share, sender, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(note))
                {
                    continue;
                }

                // 一次发好几首时合并，别让后一首盖掉前一首
                _musicNotes.AddOrUpdate(conversation.SourceKey, note!, (_, old) => old + "\n\n" + note);
                heard = true;
                EmitLog($"[Music] 已听过：{share.Describe()}");
            }
            catch (Exception ex)
            {
                EmitLog($"[Music] 处理失败（{share.Describe()}）: {ex.Message}");
            }
        }

        if (heard && _settings.AiModeEnabled && AllowReply(conversation))
        {
            EnqueueReply(conversation, null); // 没有触发消息 → 不引用（沿用 replyTo 那套规则）
        }
    }

    // ══════════ 戳一戳 ══════════

    private void OnPoked(QqPokeEvent poke)
    {
        try
        {
            HandlePoke(poke);
        }
        catch (Exception ex)
        {
            EmitLog("处理戳一戳事件异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 戳一戳怎么处理：
    ///   • 戳了机器人 → 记进上下文（模型才看得到），并触发一次回复；同一个人连着戳时冷却内只回一次。
    ///   • 别人互戳 → 只记进上下文，**不**主动插话（群里互相戳得很多，每条都回就是刷屏）。
    /// 模型回答时可选地在 JSON 里给 poke 字段（对方 QQ 号）戳回去，安全阀在 GenerateReplyAsync 里校验。
    /// </summary>
    private void HandlePoke(QqPokeEvent poke)
    {
        if (!_settings.EnablePoke)
        {
            return;
        }

        // 自己戳的（协议端可能回显）不管
        if (_selfId != 0 && poke.UserId == _selfId)
        {
            return;
        }

        var sourceId = poke.IsGroup ? poke.GroupId : poke.UserId;
        if (!IsSourceAllowed(poke.IsGroup, sourceId))
        {
            LogThrottled("poke-ignore:" + sourceId,
                $"忽略戳一戳（不在白名单）: {(poke.IsGroup ? "群 " + poke.GroupId : "私聊 " + poke.UserId)}");
            return;
        }

        var conversation = GetOrCreateConversation(new QqChatMessage(
            0, poke.IsGroup, poke.UserId, poke.GroupId, string.Empty, string.Empty, poke.Time, false));

        // 名字尽量从历史里找（notice 事件本身不带昵称/群名片）
        var pokerName = ResolveDisplayName(conversation, poke.UserId);
        var text = poke.IsSelfPoked
            ? $"（戳一戳）{pokerName} 戳了你一下"
            : $"（戳一戳）{pokerName} 戳了 {ResolveDisplayName(conversation, poke.TargetId)} 一下";

        var appended = new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderName = pokerName,
            SenderId = poke.UserId,
            Text = text,
            Timestamp = poke.Time,
            QqMessageId = 0
        };
        conversation.Append(appended);
        Touch(conversation);
        MessageAdded?.Invoke(conversation.SourceKey, appended);
        Save();

        EmitLog($"收到戳一戳：{(poke.IsGroup ? $"群{poke.GroupId}" : "私聊")} {pokerName}({poke.UserId}) → " +
                $"{(poke.IsSelfPoked ? "机器人" : ResolveDisplayName(conversation, poke.TargetId))}({poke.TargetId})");

        if (!poke.IsSelfPoked)
        {
            return; // 别人互戳只进上下文
        }

        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.PokeCooldownSeconds));
        var now = DateTimeOffset.Now;
        if (cooldown > TimeSpan.Zero &&
            _lastPoke.TryGetValue(conversation.SourceKey, out var last) &&
            last.PokerId == poke.UserId &&
            now - last.At < cooldown)
        {
            EmitLog($"同一个人的连续戳 → 这次不回应（{cooldown.TotalSeconds - (now - last.At).TotalSeconds:F0}s 后放行）: {conversation.Name}");
            return;
        }

        _lastPoke[conversation.SourceKey] = (poke.UserId, now);
        // 心情的客观来源：被戳的次数（越频繁越烦，也会随时间自己消）
        _mood.RecordPoke(now);

        if (_settings.AiModeEnabled && AllowReply(conversation))
        {
            // 戳一戳没有消息 id，引用目标交给模型自己用 replyTo 指认
            EnqueueReply(conversation, null);
        }
    }

    /// <summary>最近一次“戳了机器人”的人（用来校验模型想戳回去的号码是否真的存在）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long PokerId, DateTimeOffset At)> _lastPoke = new();

    /// <summary>机器人当前心情：被戳次数（客观）+ 模型自己写的一句心情（主观）。</summary>
    private readonly MoodStore _mood = new();

    /// <summary>每个会话最近一次“按歌名去听”的时间（冷却，防同一话题反复搜歌）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastListen = new();

    /// <summary>每个会话正在跑的链接预览（回复前短暂等一下：快站点能当轮就用上）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _linkTasks = new();

    /// <summary>听音乐专用的 HttpClient（下载音频可能几 MB，超时给宽松点）。</summary>
    private readonly HttpClient _musicHttp = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>语音（TTS）专用 HttpClient：合成一句要几秒（Piper 串行推理），超时给 30 秒。</summary>
    private readonly HttpClient _voiceHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>语音（TTS）客户端：拼 /speak 地址、面板试听时真取 wav。Start() 里组装。</summary>
    private VoiceService? _voice;

    /// <summary>每个会话最近一次发语音的时间（频率门：语音是“稀罕事”，不能每句都发）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastVoice = new();

    /// <summary>联网搜索专用 HttpClient：检索要等上游模型回话（含思考），超时给宽松点。</summary>
    private readonly HttpClient _netHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>联网搜索：模型自带搜索（Gemini grounding）+ 可插拔搜索源兑底。Start() 里组装。</summary>
    private WebSearchService? _search;

    /// <summary>每个会话最近一次“上网查”的时间（冷却：搜索要花模型调用与几秒时间，不能反复搜）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastSearch = new();

    /// <summary>每个会话刚查到的资料，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _searchNotes = new();

    /// <summary>同会话两次联网搜索的最小间隔（秒）。</summary>
    private const int SearchCooldownSeconds = 30;

    /// <summary>听音乐：识别到的分享 → 网易云歌词 + 低码率音频 → 波形分析。Start() 里组装。</summary>
    private MusicService? _music;

    /// <summary>链接预览：群里发链接时真去打开一下，取标题/摘要。Start() 里组装。</summary>
    private LinkPreviewer? _links;

    /// <summary>每个会话最近一次“链接里写了啥”的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _linkNotes = new();

    /// <summary>每个会话最近一次“听过的歌”的事实描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _musicNotes = new();

    /// <summary>最近一次机器人主动戳人（频率门：一次只戳一个，不参与互戳拉锯）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long TargetId, DateTimeOffset At)> _lastPokeSent = new();

    /// <summary>每个会话最近一次“有人撤回消息”的时间（冷却：连着撤几条时不要每条都评论）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _lastRecallAt = new();

    /// <summary>每个会话最近一次撤回事件的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _recallNotes = new();

    /// <summary>同会话两次“评论撤回”的最小间隔（秒）。</summary>
    private const int RecallCommentCooldownSeconds = 90;

    /// <summary>
    /// 有人撤回了一条消息。
    ///
    /// 为什么不能不管：撤回后群友已经看不到那条了，但机器人手里还有 ——
    /// 不管的话它下一轮会去接一句“群里已经不存在的消息”，或者把撤回的内容
    /// 当成公共信息接着聊（对方会觉得“我明明擦掉了”）。
    /// 做三件事：
    ///   ① 在上下文里把那条标成 <c>[已撤回] 原内容</c>（内容保留，但一眼能看出被收回去了）；
    ///   ② 给模型一次开口的机会（“撤回了啥”是人类最常见的反应），带冷却；
    ///   ③ 提示词里明确：“可以记得，但不要引用/复述/当众开玩笑”。
    /// </summary>
    private void OnMessageRecalled(QqRecallEvent recall)
    {
        try
        {
            // 撤回事件不带昵称，先用它给的身份把会话找到（没有就新建，与戳一戳同一套）
            var conversation = GetOrCreateConversation(new QqChatMessage(
                0, recall.IsGroup, recall.UserId, recall.GroupId, string.Empty, string.Empty, recall.Time, false));
            if (conversation is null)
            {
                return;
            }

            var key = conversation.SourceKey;
            var target = conversation.Messages.FirstOrDefault(m => m.QqMessageId == recall.MessageId);
            if (target is null)
            {
                // 常见于：那条消息已经被滚动窗口/归档挤掉了 —— 没什么要改的，也不值得评论
                EmitLog($"[Recall] {conversation.Name}：有一条消息被撤回（id={recall.MessageId}），但它不在当前上下文里");
                return;
            }

            if (target.Recalled)
            {
                return; // 重复事件：已经标过了，也不再评论
            }

            target.Recalled = true;
            Save();

            var sender = target.SenderName ?? ResolveDisplayName(conversation, recall.UserId);            var byOther = recall.OperatorId > 0 && recall.OperatorId != recall.UserId
                ? $"（由 {ResolveDisplayName(conversation, recall.OperatorId)} 撤回）"
                : string.Empty;
            EmitLog($"[Recall] {conversation.Name}：{sender} 撤回了一条消息{byOther} —— 原内容（已标进上下文）：{Shorten(target.Text, 40)}");

            var now = DateTimeOffset.Now;
            if (_lastRecallAt.TryGetValue(key, out var last) &&
                now - last < TimeSpan.FromSeconds(RecallCommentCooldownSeconds))
            {
                EmitLog($"[Recall] 这次不评论（同会话 {RecallCommentCooldownSeconds}s 内已经评论过一次）");
                return;
            }

            _lastRecallAt[key] = now;

            // 手误更正：撤回后同一个人又发了新消息（实测：把“固定bpc”改成“固定npc”）——
            // 这种时候去点评“撤回了啥”很尴尬（群里实测被怼过）。人类的做法是当没看见。
            var corrected = conversation.Messages.Any(m =>
                m.Role == MessageRole.Peer &&
                m.SenderId == target.SenderId &&
                m.Seq > target.Seq &&
                !m.Recalled);
            if (corrected)
            {
                EmitLog("[Recall] 看起来是手误更正（同一个人随后又发了消息）→ 不给模型开口机会，只标记");
                Save();
                return;
            }

            _recallNotes[key] = $"（刚有人撤回了一条消息：{sender}。上下文里那条已标成 [已撤回]。）";
            Touch(conversation);

            if (_settings.AiModeEnabled && AllowReply(conversation))
            {
                EnqueueReply(conversation, null);
            }
        }
        catch (Exception ex)
        {
            EmitLog("[Recall] 处理撤回事件出错: " + ex.Message);
        }
    }

    /// <summary>从历史消息里找一个人的显示名（昵称/群名片）；找不到就写“成员 <qq>”。</summary>
    private string ResolveDisplayName(BotConversation conversation, long userId)
    {
        if (userId <= 0)
        {
            return "某人";
        }

        if (_selfId != 0 && userId == _selfId)
        {
            return "你";
        }

        foreach (var msg in conversation.Messages.AsEnumerable().Reverse())
        {
            if (msg.SenderId == userId && !string.IsNullOrWhiteSpace(msg.SenderName))
            {
                return msg.SenderName!;
            }
        }

        return $"成员 {userId}";
    }

    /// <summary>会话首次出现时：异步补全真实群名 + 拉取近期历史消息。</summary>
    private void EnsureGroupContext(BotConversation conversation, long groupId)
    {
        if (!_historyRequested.TryAdd(conversation.SourceKey, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var name = await _source.GetGroupNameAsync(groupId);
                if (!string.IsNullOrWhiteSpace(name) && name != conversation.Name)
                {
                    conversation.Name = name!;
                    Save();
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("History", "获取群名失败: " + ex.Message);
            }

            if (conversation.HistoryLoaded || _source is not OneBotGateway gateway || !gateway.IsConnected)
            {
                return;
            }

            // 会话已经满了：插入到顶部的旧消息会立刻被裁掉，白调一次协议端接口
            if (conversation.MessageCount >= _settings.MaxMessagesPerConversation)
            {
                conversation.HistoryLoaded = true;
                FileLog.Write("History", $"群 {groupId} 本地已有 {conversation.MessageCount} 条（达上限），跳过历史补录");
                return;
            }

            try
            {
                var history = await gateway.GetGroupMsgHistoryAsync(groupId, 20);
                if (history.Count == 0)
                {
                    return;
                }

                var restored = new List<ChatMessage>(history.Count);

                // 协议端返回的是**最新在前**，而 MergeHistoryAtTop 要求正序（旧→新）。
                // 不反转的话，补录消息在会话里的先后会颠倒，我的序号分配也会与时间相反。
                for (var i = history.Count - 1; i >= 0; i--)
                {
                    var m = history[i];
                    if (string.IsNullOrWhiteSpace(m.Text))
                    {
                        continue;
                    }

                    restored.Add(new ChatMessage
                    {
                        Role = MessageRole.Peer,
                        SenderName = m.IsGroup ? m.SenderName : null,
                        SenderId = m.UserId,
                        Text = m.Text,
                        Timestamp = m.Time,
                        QqMessageId = m.MessageId,
                        ImageUrls = m.ImageUrls
                    });
                }

                var inserted = conversation.MergeHistoryAtTop(restored);
                conversation.HistoryLoaded = true;
                if (inserted > 0)
                {
                    // 历史也写入人物档案
                    var botUin = _settings.NormalizedUin;
                    foreach (var m in restored)
                    {
                        if (m.SenderId is not long uid ||
                            (!string.IsNullOrWhiteSpace(botUin) && uid.ToString() == botUin))
                        {
                            continue;
                        }

                        _profiles.Append(uid.ToString(), m.SenderName ?? string.Empty, m.Text, m.Timestamp, conversation.Name, groupId, m.Seq);
                    }

                    Save();
                    FileLog.Write("History", $"群 {groupId} 补入 {inserted} 条历史消息");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write("History", "拉取群历史失败: " + ex.Message);
            }
        });
    }

    // ---------- 会话管理 ----------

    private BotConversation GetOrCreateConversation(QqChatMessage msg)
    {
        var key = msg.IsGroup ? $"group:{msg.GroupId}" : $"private:{msg.UserId}";

        lock (_conversationsGate)
        {
            var existing = _conversations.FirstOrDefault(c => c.SourceKey == key);
            if (existing is not null)
            {
                return existing;
            }

            var name = msg.IsGroup ? $"群聊 {msg.GroupId}" : (msg.SenderName ?? msg.UserId.ToString());
            var conversation = new BotConversation
            {
                SourceKey = key,
                Kind = msg.IsGroup ? ConversationKind.GroupChat : ConversationKind.PrivateChat,
                Name = name,
                MaxMessages = _settings.MaxMessagesPerConversation
            };
            conversation.OnEvicted = ArchiveEvicted;
            _conversations.Add(conversation);
            EmitLog($"新建会话 {name} ({key})");
            ConversationsChanged?.Invoke();
            return conversation;
        }
    }

    /// <summary>更新活跃时间并重排（最近活跃在前）。</summary>
    private void Touch(BotConversation conversation)
    {
        lock (_conversationsGate)
        {
            _conversations.Remove(conversation);
            _conversations.Insert(0, conversation);
        }

        ConversationsChanged?.Invoke();
    }

    private void RestoreConversations()
    {
        try
        {
            var records = _store.LoadAsync().GetAwaiter().GetResult();
            if (records.Count == 0)
            {
                return;
            }

            var restored = 0;
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.SourceKey) || !IsWhitelistedKey(record.SourceKey))
                {
                    continue;
                }

                lock (_conversationsGate)
                {
                    if (_conversations.Any(c => c.SourceKey == record.SourceKey))
                    {
                        continue;
                    }

                    _conversations.Add(BotConversation.FromRecord(record, _settings.MaxMessagesPerConversation, ArchiveEvicted));
                }

                _historyRequested.TryAdd(record.SourceKey!, 0);
                restored++;
            }

            lock (_conversationsGate)
            {
                _conversations.Sort((a, b) => b.LastTime.CompareTo(a.LastTime));
            }

            FileLog.Write("Store", $"已恢复 {restored} 个会话");
            ConversationsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FileLog.Write("Store", "恢复会话失败: " + ex.Message);
        }
    }

    private void Save()
    {
        Interlocked.Increment(ref _saveVersion);

        if (Interlocked.Exchange(ref _savePending, 1) == 1)
        {
            return; // 已有保存任务在跑，它会带上本次变更
        }

        _ = SaveLoopAsync();
    }

    /// <summary>
    /// 持久化循环（后台线程）。
    /// 以前这里在**WS 接收线程上同步**执行，而且 ToRecord() 是对所有会话做深拷贝 ——
    /// 消息一多就会直接阻塞收消息。现在改成后台任务 + 合并窗口 + 版本号重检。
    /// </summary>
    private async Task SaveLoopAsync()
    {
        try
        {
            while (true)
            {
                var version = Interlocked.Read(ref _saveVersion);

                await Task.Delay(150); // 合并窗口：短时间内的多次变更只写一次盘

                List<ConversationRecord> records;
                lock (_conversationsGate)
                {
                    records = _conversations.Select(c => c.ToRecord()).ToList();
                }

                _store.RequestSave(records);

                // 先放行，再检查期间是否有新变更 ——
                // 顺序不能反，否则会在临界区漏掉最后一次保存。
                Interlocked.Exchange(ref _savePending, 0);

                if (Interlocked.Read(ref _saveVersion) == version)
                {
                    return;
                }

                if (Interlocked.Exchange(ref _savePending, 1) == 1)
                {
                    return; // 已被其他调用接手
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _savePending, 0);
            FileLog.Write("Store", "保存会话失败: " + ex.Message);
        }
    }

    /// <summary>节流日志：同一 key 在窗口内只输出一次（避免忙群里刷爆日志与面板）。</summary>
    private void LogThrottled(string key, string message, int windowSeconds = 60)
    {
        var now = Environment.TickCount64;

        if (_noisyLogAt.TryGetValue(key, out var last) && now - last < windowSeconds * 1000L)
        {
            return;
        }

        _noisyLogAt[key] = now;

        // 白名单外的来源可能很多：键数量做兵底
        if (_noisyLogAt.Count > 2000)
        {
            _noisyLogAt.Clear();
        }

        EmitLog(message);
    }

    // ---------- 白名单 ----------

    /// <summary>解析白名单。返回 (ID集合, 是否通配全部)。支持换行/中英文逗号/分号/空格/Tab 分隔，以及 * 通配。</summary>
    private static (HashSet<long> Ids, bool All) ParseWhitelist(string? whitelist)
    {
        var ids = new HashSet<long>();
        if (string.IsNullOrWhiteSpace(whitelist))
        {
            return (ids, false); // 严格模式：空名单 = 全部忽略
        }

        var parts = whitelist.Split(
            new[] { '\n', '\r', ',', '，', ';', '；', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var all = false;
        foreach (var part in parts)
        {
            if (part is "*" or "all" or "ALL")
            {
                all = true;
                continue;
            }

            if (long.TryParse(part, out var id))
            {
                ids.Add(id);
            }
        }

        return (ids, all);
    }

    private bool IsWhitelisted(QqChatMessage msg)
        => IsSourceAllowed(msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);

    /// <summary>群/私聊是否在白名单里（戳一戳事件没有 QqChatMessage，只能单拎一个判据）。</summary>
    private bool IsSourceAllowed(bool isGroup, long id) => _whitelistAll || _whitelist.Contains(id);

    private bool IsWhitelistedKey(string sourceKey)
    {
        if (_whitelistAll)
        {
            return true;
        }

        var idx = sourceKey.IndexOf(':');
        return idx > 0 && long.TryParse(sourceKey[(idx + 1)..], out var id) && _whitelist.Contains(id);
    }

    // ---------- 回复流程 ----------

    /// <summary>限流：私聊/群聊各自冷却，防止连发刷屏。</summary>
    private bool AllowReply(BotConversation conversation)
    {
        var cooldown = conversation.Kind == ConversationKind.GroupChat
            ? TimeSpan.FromSeconds(Math.Max(0, _settings.GroupCooldownSeconds))
            : TimeSpan.FromSeconds(Math.Max(0, _settings.PrivateCooldownSeconds));

        if (cooldown == TimeSpan.Zero)
        {
            return true;
        }

        var key = conversation.SourceKey;
        var now = DateTimeOffset.Now;
        if (_replyCooldown.TryGetValue(key, out var last) && now - last < cooldown)
        {
            EmitLog($"冷却中（{(cooldown - (now - last)).TotalSeconds:F1}s 后放行）: {conversation.Name}");
            return false;
        }

        _replyCooldown[key] = now;
        return true;
    }

    /// <summary>把一条待回复请求排入该会话的 FIFO 链，并唤醒调度器。</summary>
    private void EnqueueReply(BotConversation conversation, long? triggerMessageId)
    {
        if (triggerMessageId is not null)
        {
            LastActiveRequestTime = DateTime.Now; // 主动请求刷新活跃时间
        }

        _pendingConversations[conversation.SourceKey] = conversation;
        _pendingReplies
            .GetOrAdd(conversation.SourceKey, _ => new System.Collections.Concurrent.ConcurrentQueue<long?>())
            .Enqueue(triggerMessageId);

        _ = DrainReplyQueueAsync();
    }

    /// <summary>
    /// 调度器：从各会话的 FIFO 链头取请求交给并发 worker。
    ///   • 同一会话同时只能有一个在途请求（保证回复顺序与引用正确）
    ///   • 不同会话并行执行（一个慢请求不再阻塞其它群）
    ///   • 全局并发上限由 _replyGate 控制
    /// </summary>
    private async Task DrainReplyQueueAsync()
    {
        if (Interlocked.CompareExchange(ref _replyWorkerRunning, 1, 0) == 1)
        {
            return; // 已有调度器在跑
        }

        try
        {
            while (true)
            {
                var fired = false;

                foreach (var (key, queue) in _pendingReplies)
                {
                    if (_inFlight.ContainsKey(key))
                    {
                        continue; // 该会话已在跑 → 保持顺序，等它完成
                    }

                    if (queue.IsEmpty || !queue.TryDequeue(out var trigger))
                    {
                        continue;
                    }

                    if (!_inFlight.TryAdd(key, 0))
                    {
                        // 并发抢占失败：把触发消息放回队首位置（重新入队到尾部也可，
                        // 因为同一会话此时必定无其它待处理项）
                        queue.Enqueue(trigger);
                        continue;
                    }

                    if (!_pendingConversations.TryGetValue(key, out var conversation))
                    {
                        _inFlight.TryRemove(key, out _);
                        continue;
                    }

                    fired = true;
                    _ = RunReplyAsync(conversation, key, trigger);
                }

                if (fired)
                {
                    continue; // 可能还有别的会话可跑
                }

                // 本轮一无所获：要么全在途（等释放），要么真的没活了
                var waiting = _pendingReplies.Any(kv => !kv.Value.IsEmpty && _inFlight.ContainsKey(kv.Key));
                if (!waiting)
                {
                    CleanupPending();
                    break;
                }

                await Task.Delay(150);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _replyWorkerRunning, 0);
            if (_pendingReplies.Any(kv => !kv.Value.IsEmpty))
            {
                _ = DrainReplyQueueAsync();
            }
        }
    }

    /// <summary>清掉已排空且不在途的会话条目，避免字典无限增长。</summary>
    private void CleanupPending()
    {
        foreach (var key in _pendingReplies.Keys.ToList())
        {
            if (_inFlight.ContainsKey(key))
            {
                continue;
            }

            if (_pendingReplies.TryGetValue(key, out var queue) && queue.IsEmpty)
            {
                _pendingReplies.TryRemove(key, out _);
                _pendingConversations.TryRemove(key, out _);
            }
        }
    }

    /// <summary>延迟回收被替换掉的闸门：等所有可能还在等它的请求都结束再 Dispose。</summary>
    private static void RetireGate(SemaphoreSlim gate)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(90)); // HttpClient 超时 60s，留余量
            try
            {
                gate.Dispose();
            }
            catch
            {
                // 已释放或仍有等待者：忽略（不能影响退出流程）
            }
        });
    }

    // ══════════ QQ 账号在线探测 ══════════

    /// <summary>
    /// 探测 QQ 账号是否真的在线。
    /// 仅凭「WebSocket 连着」无法判定 —— 登录失效/被顶号时连接照旧，
    /// 只是从此一条消息都收不到（这正是本次事故的表现）。
    /// </summary>
    private async Task CheckAccountOnlineAsync()
    {
        if (_disposed == 1 || _source is not OneBotGateway gateway || !gateway.IsConnected)
        {
            return;
        }

        if (Interlocked.Exchange(ref _healthRunning, 1) == 1)
        {
            return; // 上一轮还没回来
        }

        try
        {
            var online = await gateway.GetAccountOnlineAsync();
            if (online is null)
            {
                return; // 协议端没明确答复 → 不误报，保持原状态
            }

            var next = online.Value ? 1 : 0;
            var previous = Interlocked.Exchange(ref _accountOnline, next);
            if (previous == next)
            {
                return; // 状态未变，不刷日志
            }

            if (next == 0)
            {
                EmitLog("⚠ QQ 账号已离线（登录失效或被顶号）：从现在起收不到任何消息。" +
                        "请打开 NapCat 管理面板重新扫码登录（面板地址见部署目录 README）。");
            }
            else
            {
                EmitLog("QQ 账号已上线，恢复正常接收消息。");
            }

            StateChanged?.Invoke();
        }
        catch
        {
            // 探测失败不打扰用户：下一轮自然会重试
        }
        finally
        {
            Interlocked.Exchange(ref _healthRunning, 0);
        }
    }

    /// <summary>执行一次回复（受全局并发闸门限制）。</summary>
    private async Task RunReplyAsync(BotConversation conversation, string sourceKey, long? triggerMessageId)
    {
        // 捕获当前闸门实例：配置变更会整体替换 _replyGate，
        // Wait 与 Release 必须作用在**同一个对象**上。
        var gate = _replyGate;
        var acquired = false;
        try
        {
            await gate.WaitAsync();
            acquired = true;
            conversation.HasPendingReply = false;
            await GenerateReplyAsync(conversation, triggerMessageId);
        }
        catch (Exception ex)
        {
            EmitLog("回复流程异常: " + ex.Message);
        }
        finally
        {
            // 顺序很重要：先释放闸门（可能抛），再清在途标记。
            // 任何一个环节失败都不能让 _inFlight 残留 —— 残留意味着该会话永远不再被调度。
            if (acquired)
            {
                try
                {
                    gate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // 闸门已被回收：忽略，不能阻断下面的清理
                }
            }

            _inFlight.TryRemove(sourceKey, out _);
            LastActiveRequestTime = DateTime.Now;
            _ = DrainReplyQueueAsync(); // 唤醒调度器处理该会话的后续项
        }
    }

    /// <summary>静默兜底：超过 IdleFallbackSeconds 没有主动请求时，为待处理会话补一次请求。</summary>
    private void IdleFallbackTick()
    {
        if (_disposed == 1 || !_settings.AiModeEnabled)
        {
            return;
        }

        if (DateTime.Now - LastActiveRequestTime < TimeSpan.FromSeconds(_settings.IdleFallbackSeconds))
        {
            return;
        }

        foreach (var conversation in Conversations)
        {
            if (!conversation.HasPendingReply)
            {
                continue;
            }

            conversation.HasPendingReply = false;
            EmitLog($"静默兜底触发: {conversation.Name}");
            EnqueueReply(conversation, null);
        }
    }

    private async Task GenerateReplyAsync(BotConversation conversation, long? triggerMessageId)
    {
        var context = conversation.TakeLast(_settings.MaxContextMessages);
        if (context.Count == 0)
        {
            return;
        }

        // 当前上下文最早一条：档案只取比它更早的（按单调序号精确判定，避免时间戳秒级精度误判）
        var contextOldestSeq = context[0].Seq;
        var contextOldestUnix = context[0].Timestamp.ToUnixTimeSeconds();
        var (isGroupScope, scopeGroupId) = conversation.Target;

        // 收集最近 40 条上下文里出现过的发送者 QQ → 读取各自在**本会话**的人物档案（角色卡片）
        var profiles = new List<string>();
        var profileChars = 0;
        var recentSenders = context
            .TakeLast(40)
            .Where(m => m.Role == MessageRole.Peer && m.SenderId.HasValue)
            .Select(m => m.SenderId!.Value)
            .Distinct()
            .Take(Math.Max(0, _settings.ProfileLookupCount))
            .ToList();

        foreach (var uid in recentSenders)
        {
            var summary = _profiles.GetProfileSummary(
                uid.ToString(),
                isGroupScope ? scopeGroupId : 0,
                _settings.ProfileSummaryLines,
                contextOldestSeq,
                contextOldestUnix);

            if (string.IsNullOrWhiteSpace(summary))
            {
                continue; // 本会话没有更早的历史 → 不占提示词预算
            }

            if (profileChars + summary.Length > _settings.MaxProfileChars)
            {
                break; // 超出预算：后面的（发言更早的）丢弃
            }

            profiles.Add(summary);
            profileChars += summary.Length;
        }

        var started = DateTime.Now;
        SetThinking(conversation, true);

        // 表情包候选：拿最近的对话文字当检索词，从库里挑几张给模型选。
        // 先挑后发 —— 库可能有上千张，全塞进提示词既贵又不准。
        var stickerChoices = new List<StickerChoice>();
        if (_settings.EnableStickers && _settings.StickerLibraryMax > 0 && _settings.StickerCandidates > 0)
        {
            var query = string.Join(" ", context.TakeLast(8).Select(m => m.Text));
            stickerChoices = _stickers
                .PickCandidates(query, _settings.StickerCandidates)
                .Select(s => new StickerChoice(s.Id, StickerStore.Describe(s)))
                .ToList();
        }

        EmitLog($"请求模型…（{conversation.Name}，上下文 {context.Count} 条，档案 {profiles.Count} 份/{profileChars} 字" +
                (stickerChoices.Count > 0 ? $"，表情包候选 {stickerChoices.Count} 张" : string.Empty) + "）");

        // 最近被戳过（10 分钟内）才给模型“可以戳回去”的指令，平时不浪费 token
        var pokeContext = _settings.EnablePoke &&
            (_lastPoke.TryGetValue(conversation.SourceKey, out var lastPoke) &&
             DateTimeOffset.Now - lastPoke.At < TimeSpan.FromMinutes(10));

        // 心情（被戳次数客观 + 模型主观写的）：影响还戳不戳回去、话多话少。
        // 只在“刚被戳过”或“模型写过心情”时给，平常不浪费 token。
        var moodNow = DateTimeOffset.Now;
        var moodText = pokeContext || _mood.CurrentText(moodNow) is not null ? _mood.Describe(moodNow) : null;

        // 刚“听过”的歌：把歌词与波形实测交给模型，用完就清（避免以后每轮都背上它）
        var musicText = _musicNotes.TryRemove(conversation.SourceKey, out var pendingMusic) ? pendingMusic : null;

        // 刚有人撤回了消息：把这件事告诉模型（但不告诉它撤回了什么）——用完就清
        var recallText = _recallNotes.TryRemove(conversation.SourceKey, out var pendingRecall) ? pendingRecall : null;

        // 刚上网查到的资料 / 读到的页面正文：交给模型，用完就清
        var searchText = _searchNotes.TryRemove(conversation.SourceKey, out var pendingSearch) ? pendingSearch : null;

        // 链接预览：给快站点 2.5 秒的机会当轮用上；太慢就先不等（完成后留给下一轮）
        if (_linkTasks.TryGetValue(conversation.SourceKey, out var linkTask))
        {
            await Task.WhenAny(linkTask, Task.Delay(TimeSpan.FromMilliseconds(2500)));
        }

        var linkText = _linkNotes.TryRemove(conversation.SourceKey, out var pendingLink) ? pendingLink : null;

        CompletionResult result;
        try
        {
            result = await _brain.CompleteAsync(
                context,
                profiles.Count > 0 ? string.Join("\n\n", profiles) : null,
                stickers: stickerChoices.Count > 0 ? stickerChoices : null,
                pokeContext: pokeContext,
                moodText: moodText,
                musicText: musicText,
                linkText: linkText,
                recallText: recallText,
                enableWebSearch: _settings.EnableWebSearch,
                searchText: searchText,
                enableListen: _settings.EnableMusic,
                enableVoice: _settings.EnableVoice);
        }
        catch (Exception ex)
        {
            SetThinking(conversation, false);
            EmitLog($"模型请求失败: {ex.Message}");
            return;
        }

        var elapsed = (DateTime.Now - started).TotalMilliseconds;
        SetThinking(conversation, false);

        // 发言适合度门槛：以前只写在提示词里、代码不执行；现在真正生效。
        // 模型未按 JSON 输出（Suitability == null）时按普通文本回复处理，不拦截。
        var threshold = Math.Clamp(_settings.SuitabilityThreshold, 0, 100);
        if (result.Suitability is int score && score < threshold)
        {
            EmitLog($"适合度不足 → 沉默（评分 {score} < 阈值 {threshold}，{elapsed:F0}ms）: {conversation.Name}");
            return;
        }

        // 表情包：模型可以只发图不说话，也可以“文字 + 图”。
        // 校验一下 id（模型偶发会编造/多空格），拿不到就把这次当成纯文字。
        StickerRecord? sticker = null;
        // 联网搜索（search / read）：后台去查，拿到结果后再给它一次开口的机会。
        // 这两个是“两轮动作”—— 模型这轮照常接话（reply 可以写“我去查查”），下一轮拿着事实说。
        // search 优先于 read：模型一般只会填一个。
        if (_settings.EnableWebSearch && _search is not null && result.Search is { Length: > 0 } wantedQuery)
        {
            QueueWebSearchAsync(conversation, wantedQuery);
        }
        else if (_settings.EnableWebSearch && _search is not null && result.Read is { Length: > 0 } pageUrl)
        {
            QueuePageReadAsync(conversation, pageUrl);
        }

        // 模型想听一首歌（listen 字段）：后台去搜、去听，听完再给它一次开口的机会。
        // 这是群里说“去听一下 XXX”的唯一入口 —— 不靠正则猜句子，交给模型自己决定。
        if (_settings.EnableMusic && result.Listen is { Length: > 0 } wantedSong && _music is not null)
        {
            var key = conversation.SourceKey;
            var nowListen = DateTimeOffset.Now;
            var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.MusicListenCooldownSeconds));
            if (!_lastListen.TryGetValue(key, out var lastAt) || nowListen - lastAt >= cooldown)
            {
                _lastListen[key] = nowListen;
                EmitLog($"[Music] 模型想听「{wantedSong}」");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var note = await _music.DescribeByNameAsync(wantedSong, "群友", CancellationToken.None);
                        if (string.IsNullOrWhiteSpace(note))
                        {
                            EmitLog($"[Music] 没搜到/没听到「{wantedSong}」");
                            return;
                        }

                        _musicNotes.AddOrUpdate(key, note!, (_, old) => old + "\n\n" + note);
                        if (_settings.AiModeEnabled && AllowReply(conversation))
                        {
                            EnqueueReply(conversation, null);
                        }
                    }
                    catch (Exception ex)
                    {
                        EmitLog($"[Music] 听「{wantedSong}」失败: {ex.Message}");
                    }
                });
            }
            else
            {
                EmitLog($"[Music] 「{wantedSong}」还在冷却中（{Math.Round((nowListen - lastAt).TotalSeconds)}s 前刚听过）");
            }
        }

        // 模型想把某首歌分享给群里 → 搜到就发一张网易云卡片，顺手“听”一遍（下一轮它就能聊这首歌）。
        if (_settings.EnableMusic && result.ShareSong is { Length: > 0 } songToShare && _music is not null)
        {
            var key = conversation.SourceKey;
            var (shareIsGroup, shareTargetId) = conversation.Target;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (shareTargetId == 0 || !_source.IsConnected)
                    {
                        return;
                    }

                    var songId = await _music.ResolveSongIdByNameAsync(songToShare, CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(songId))
                    {
                        EmitLog($"[Music] 想分享「{songToShare}」但没搜到，不发卡片");
                        return;
                    }

                    var ok = await _source.SendMusicAsync(shareIsGroup, shareTargetId, "163", songId, CancellationToken.None);
                    EmitLog(ok ? $"[Music] 已分享卡片「{songToShare}」(# {songId})" : $"[Music] 卡片发送失败，改用链接分享: {songToShare}");

                    // 协议端不接卡片（NapCat 各版本对 music 段的接受程度不一样）时退化成发链接：
                    // QQ 客户端会把网易云链接自己渲染成卡片，效果差不多，但绝不能什么都不发
                    if (!ok)
                    {
                        var link = $"https://music.163.com/song?id={songId}";
                        var sentLink = await _source.SendTextAsync(shareIsGroup, shareTargetId, link, CancellationToken.None);
                        EmitLog(sentLink ? $"[Music] 已用链接分享：{link}" : $"[Music] 链接也发送失败：{link}");
                    }

                    // 卡片发出去了，接着真去听一遍：下一轮发言时它就“听过这首歌”
                    var note = await _music.DescribeByNameAsync(songToShare, "（自己分享的）", CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(note) && !_musicNotes.ContainsKey(key))
                    {
                        _musicNotes[key] = note!;
                    }
                }
                catch (Exception ex)
                {
                    EmitLog($"[Music] 分享「{songToShare}」失败: {ex.Message}");
                }
            });
        }

        if (_settings.EnableStickers && result.StickerId is { } sid)
        {
            sticker = _stickers.Find(sid);
            if (sticker is null)
            {
                EmitLog($"模型挑的表情包 #{sid} 不在库里，已忽略（只发文字）");
            }
            else if (sticker.IsSticker != true)
            {
                // 还没通过“是不是表情包”审核的图不当表情包用：
                // 宁可这一轮不发，也不要把聊天截图/广告发出去
                EmitLog($"这张 #{sid} 还没通过“是不是表情包”审核，本轮不发");
                sticker = null;
            }
            else if (!AllowSticker(conversation, sticker.Id, out var stickerWhy))
            {
                // 频率门：库小的时候模型会每句都挂同一张（群里直接开愤：
                // “你别老是发这个表情包了”）。表情包是调味品，不是主食。
                EmitLog($"这次不发表情包（{stickerWhy}）: #{sticker.Id} {StickerStore.Describe(sticker)}");
                sticker = null;
            }
        }

        var reply = (result.Reply ?? string.Empty).Trim();

        // 模型想“用语音说这句”（speak）。真正的发送在下边（要等 isGroup/targetId），
        // 这里先把文本取出来：它得参与“沉默判定”与消息落库，否则“只发语音不说话”会被当成空回复。
        var voiceText = (result.Speak ?? string.Empty).Trim();

        // 模型可以顺手写一句“我现在的心情”——存下来，下一轮提示词里带上（空/太长会被忽略）
        if (_mood.SetText(result.Mood, DateTimeOffset.Now))
        {
            EmitLog($"心情变成：{_mood.Describe(DateTimeOffset.Now)}");
        }

        // 模型可以“只戳不说话”：这样也算有动作，不算沉默
        var pokeTarget = result.PokeTargetId;

        if (reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0)
        {
            var why = result.Suitability is int s2 ? $"自评 {s2}" : "空回复";
            EmitLog($"模型选择沉默（{why}，{elapsed:F0}ms）: {conversation.Name}");
            return;
        }

        // 复读守卫：模型偶尔会把上下文里自己上一条发言一字不差地再说一遍 ——
        // 群里实测过单字“悼”连发 6 条：第一条件来自上游截断，之后模型读到自己那条“悼”，
        // 就把它当成“可以接的梗”反复发。一模一样的话紧接着再来一遍，对群里就是刷屏。
        if (reply.Length > 0 && IsRepeatingOwnLastMessage(conversation, reply))
        {
            EmitLog($"检测到复读（与上一条自己的发言完全相同）→ 沉默（{elapsed:F0}ms）: {Shorten(reply, 40)}");
            return;
        }

        // 回复引用目标怎么定（两件事一起决定，别只看排队时记录的那个 id）：
        //   ① **模型自己指认的最准**：提示词里最近几条别人的消息都带了 (#id)，
        //      它在 JSON 里用 replyTo 说明“我在回哪条”。这是唯一能从根上对上号的办法 ——
        //      启发式只能猜“最新那条”或“排队时的触发”，都猜不准（线上两度因此看起来回错人）。
        //      采信条件：它必须在本次上下文里（防模型报个不存在的编号）。
        //   ② 没指认时用启发式：触发消息只有“还是本轮最新诉求”时才能当引用目标 ——
        //      即它比机器人上一条发言还新；否则用上下文里最后一条别人发的消息。
        //   ③ 紧挨着回就不引用：目标后面没有别的新消息时，引用是多余的（保留原有手感）。
        var messages = conversation.Messages;
        var lastSelfIndex = LastSelfMessageIndex(messages);
        var triggerIndex = IndexOfMessage(messages, triggerMessageId);
        var triggerIsCurrent = triggerIndex >= 0 && triggerIndex > lastSelfIndex;

        long? replyTo = null;
        if (result.ReplyToMessageId is long chosen &&
            // 已撤回的不算：引用一条群里已经看不到的消息，群友看到的就是莫名其妙
            // （实测踩过：模型从历史里拿了一个已被撤回的 id 填 replyTo，BotAgent 这边只校验
            //  “它在不在上下文里”—— 结果是给一条已撤回的消息挂了引用）
            context.Any(m => m.QqMessageId == chosen && !m.Recalled) &&
            IndexOfMessage(messages, chosen) >= 0)
        {
            replyTo = chosen;
        }
        else if (triggerMessageId is not null)
        {
            replyTo = triggerIsCurrent
                ? triggerMessageId
                : context.LastOrDefault(m => m.Role == MessageRole.Peer && !m.Recalled && m.QqMessageId is > 0)?.QqMessageId;
        }

        // 没有触发消息就**不引用**（戳一戳、主动发言都是这种）。
        // 线上实测（18:40）：被小明戳了之后回“手欠啊你”，引用却挂到了 55 分钟前另一条消息上 ——
        // 因为戳一戳不是消息、没有可引用的目标，启发式只能抽“上下文里最后一条别人的消息”。
        // QQ 客户端会把引用显示成“回复某某”，群友看到的就是“回复错人”。

        if (replyTo is long quoteTarget)
        {
            var targetIndex = IndexOfMessage(messages, quoteTarget);
            // 目标已被滚动窗口裁掉 → 不引用；目标后面还有人说话 → 需要引用指明回的是哪条
            replyTo = targetIndex >= 0 && targetIndex < messages.Count - 1 ? quoteTarget : null;
        }

        var (isGroup, targetId) = conversation.Target;

        // ───── 语音（模型填了 speak）─────
        // 怎么发：只把 TTS 的 /speak URL 交给协议端，让 NapCat 自己去下载 → 转 silk → 上传
        // （见 OneBotGateway.SendVoiceAsync）—— 机器人这边不碰音频编码。
        // 为什么得克制：合成要几秒 CPU、音频占流量、群里语音连发就是刷屏。
        // 提示词让它“偶尔用”，代码侧再加一道同会话 45 秒的闸门。
        string? voiceUrl = null;
        string? voiceSkipWhy = null;
        if (voiceText.Length > 0)
        {
            var maxChars = Math.Clamp(_settings.VoiceMaxChars, 10, 300);
            if (!_settings.EnableVoice || _voice is null)
            {
                voiceSkipWhy = "语音消息开关是关的";
            }
            else if (voiceText.Length > maxChars)
            {
                voiceSkipWhy = $"{voiceText.Length} 字超过上限 {maxChars}";
            }
            else if (!AllowVoice(conversation, out var voiceReason))
            {
                voiceSkipWhy = voiceReason;
            }
            else if ((voiceUrl = _voice.BuildSpeakUrl(voiceText)) is null)
            {
                voiceSkipWhy = "TTS 服务地址没配置（应形如 http://tts:5000）";
            }
        }

        var voiceSent = false;
        if (voiceUrl is not null)
        {
            voiceSent = await _source.SendVoiceAsync(isGroup, targetId, voiceUrl);
            if (voiceSent)
            {
                _lastVoice[conversation.SourceKey] = DateTimeOffset.Now;
                EmitLog($"[Voice] 已发语音（{voiceText.Length} 字，音色 {_voice!.VoiceName}）：{Shorten(voiceText, 40)}");
            }
            else
            {
                // 失败就退化成文字：内容一定要落到群里（最差也得让群友看到它想说什么）。
                // 具体原因已由 SendVoiceAsync 把 retcode + 响应体打进日志。
                EmitLog("[Voice] record 段没发出去 → 改发文字");
            }
        }
        else if (voiceSkipWhy is not null)
        {
            EmitLog($"[Voice] 模型想用语音说，但{voiceSkipWhy} → 改发文字");
        }

        // 到底还发不发文字：
        //   • 语音发成功了、且 reply 就是那句话（或没写 reply）→ 不再重复发同一句；
        //   • 语音发成功了、但 reply 另写了内容 → 那是模型自己想补的话，照发；
        //   • 语音没发出去 → 至少把要说的话当文字发出去。
        var textReply = reply.Length > 0 ? reply : null;
        if (voiceText.Length > 0)
        {
            if (voiceSent)
            {
                if (string.Equals(textReply, voiceText, StringComparison.Ordinal))
                {
                    textReply = null;
                }
            }
            else
            {
                textReply ??= voiceText;
            }
        }

        // 落库的这条“自己说过的话”：要让模型下一轮知道自己刚才是用声音说的、说了什么
        var recordedText = voiceSent
            ? textReply is null ? $"[语音] {voiceText}" : $"{textReply}（同时用语音说：{voiceText}）"
            : reply.Length > 0 ? reply
            : voiceText.Length > 0 ? voiceText
            : "[表情包]";

        var appended = new ChatMessage
        {
            Role = MessageRole.Self,
            // 只发图（或只发语音）时也得在上下文里留个痕迹，否则模型下一轮不知道自己刚发过什么
            Text = recordedText,
            Timestamp = DateTimeOffset.Now
        };
        conversation.Append(appended);
        Touch(conversation);
        MessageAdded?.Invoke(conversation.SourceKey, appended);
        Save();

        var sent = voiceSent;
        if (textReply is not null && await SendWithCadenceAsync(isGroup, targetId, textReply, replyTo))
        {
            sent = true;
        }

        if (sticker is not null)
        {
            // 引用只给第一条消息，避免“文字 + 图”两条都带引用
            var sentImage = await SendStickerAsync(isGroup, targetId, sticker, textReply is not null ? null : replyTo);
            sent = sent || sentImage;
            _stickers.MarkUsed(sticker.Id);
            if (sentImage)
            {
                // 发出去才记账（失败不算）：这条日志也是排查“为什么又发了”的唯一现场
                var sinceText = _lastSticker.TryGetValue(conversation.SourceKey, out var prev)
                    ? $"（距上次发表情包 {(DateTimeOffset.Now - prev.At).TotalSeconds:F0} 秒）"
                    : string.Empty;
                _lastSticker[conversation.SourceKey] = (DateTimeOffset.Now, sticker.Id);
                EmitLog($"已发表情包 #{sticker.Id}{sinceText}");
            }
        }

        // 戳一戳（模型的可选动作）。只在“这个号码确实出现在本次上下文里”时才发 ——
        // 否则模型随口报个号也能戳到陌生人（同 replyTo 的防编造思路）。
        var pokeSent = false;
        if (pokeTarget is long pokeUserId)
        {
            var known = context.Any(m => m.SenderId == pokeUserId) ||
                        (_lastPoke.TryGetValue(conversation.SourceKey, out var lp) && lp.PokerId == pokeUserId);
            if (!known)
            {
                EmitLog($"模型想戳 {pokeUserId}，但这个人没在本次上下文里出现过 → 忽略（防编造号码）");
            }
            else if (!_mood.WillPokeBack(DateTimeOffset.Now, out var moodWhy))
            {
                // “不必每次被戳都回戳”：被戳太频繁时心情不好，代码侧直接拦下（不听模型的）
                EmitLog($"这次不戳 {pokeUserId}（{moodWhy}）");
            }
            else if (!AllowPokeSend(conversation, pokeUserId, out var pokeWhy))
            {
                EmitLog($"这次不戳 {pokeUserId}（{pokeWhy}）");
            }
            else
            {
                pokeSent = await _source.SendPokeAsync(isGroup, targetId, pokeUserId);
                if (pokeSent)
                {
                    _lastPokeSent[conversation.SourceKey] = (pokeUserId, DateTimeOffset.Now);
                }
                else
                {
                    EmitLog($"戳 {pokeUserId} 失败（协议端可能不支持戳一戳）");
                }
            }
        }

        EmitLog(
            $"{(sent ? "已回复" : "回复失败")} {conversation.Name}（{elapsed:F0}ms 生成" +
            $"{(result.Suitability is int sc ? $"，自评 {sc}" : string.Empty)}" +
            (reply.Length > 0 ? $"，{reply.Length} 字" : string.Empty) +
            (voiceSent ? $"，语音 {voiceText.Length} 字" : string.Empty) +
            (sticker is not null ? $"，表情包 #{sticker.Id}（{StickerStore.Describe(sticker)}）" : string.Empty) +
            (pokeSent ? $"，戳了 {pokeTarget}" : string.Empty) +
            $"{(replyTo is not null ? "，带引用" : string.Empty)}）" +
            (reply.Length > 0 ? $": {reply}" : string.Empty));
    }

    /// <summary>每个会话最近一次主动戳人：用于戳一戳频率门。</summary>
    private bool AllowPokeSend(BotConversation conversation, long targetId, out string reason)
    {
        reason = string.Empty;
        if (!_lastPokeSent.TryGetValue(conversation.SourceKey, out var last))
        {
            return true;
        }

        var since = DateTimeOffset.Now - last.At;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.PokeCooldownSeconds));
        if (cooldown > TimeSpan.Zero && since < cooldown)
        {
            reason = $"距上次戳人才 {since.TotalSeconds:F0}s（下限 {cooldown.TotalSeconds:F0}s）";
            return false;
        }

        // 同一个人不反复戳：模型很容易顺着“你戳我我戳你”一直戳下去，群里看着就是刷屏
        if (last.TargetId == targetId && since < TimeSpan.FromMinutes(5))
        {
            reason = $"{since.TotalSeconds:F0}s 前刚戳过这个人";
            return false;
        }

        return true;
    }

    /// <summary>同一个会话两次发语音的最小间隔（秒）。见 <see cref="AllowVoice" />。</summary>
    private const int VoiceMinIntervalSeconds = 45;

    /// <summary>
    /// 语音频率门。为什么要它：
    ///   • 语音在群里是“稀罕事”，连发就是刷屏（和表情包同一个道理）；
    ///   • Piper 是 CPU 串行推理，一条要几秒，群里一热就是排队。
    /// 提示词里已经反复要求模型克制，这里再加一道代码闸门 —— 模型不听话时也能兜住。
    /// 想让它更松/更紧：改这个常量（故意不做成设置项，免得面板上多一个没人调的旋钮）。
    /// </summary>
    private bool AllowVoice(BotConversation conversation, out string reason)
    {
        reason = string.Empty;
        if (!_lastVoice.TryGetValue(conversation.SourceKey, out var last))
        {
            return true;
        }

        var since = DateTimeOffset.Now - last;
        if (since < TimeSpan.FromSeconds(VoiceMinIntervalSeconds))
        {
            reason = $"{since.TotalSeconds:F0}s 前刚发过语音（同一会话下限 {VoiceMinIntervalSeconds}s）";
            return false;
        }

        return true;
    }

    /// <summary>每个会话最近一次（实际发出去了的）表情包：用于频率门。key = 会话 key。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Id)> _lastSticker = new();

    /// <summary>
    /// 表情包频率门。为什么要它：
    /// 线上实测库里只有两张时，模型会**每句都挂同一张**，群友直接开愤
    /// （“你别老是发这个表情包了”“这个bot只发奶龙”）。表情包是调味品，不是主食。
    /// </summary>
    private bool AllowSticker(BotConversation conversation, string stickerId, out string reason)
    {
        reason = string.Empty;
        var cooldown = Math.Max(0, _settings.StickerCooldownSeconds);
        if (!_lastSticker.TryGetValue(conversation.SourceKey, out var last))
        {
            return true;
        }

        var since = DateTimeOffset.Now - last.At;
        if (cooldown > 0 && since.TotalSeconds < cooldown)
        {
            reason = $"距上次发表情包只有 {since.TotalSeconds:F0} 秒（冷却 {cooldown} 秒）";
            return false;
        }

        // 同一张别在同一个会话里反复用（库里就那几张时最容易退化成“只会发这一张”）
        if (string.Equals(last.Id, stickerId, StringComparison.OrdinalIgnoreCase) && since.TotalMinutes < 10)
        {
            reason = $"这张 {stickerId} 十分钟内已经发过";
            return false;
        }

        return true;
    }

    /// <summary>发送一张表情包（读文件 → base64 → 协议端 image 段）。失败只记日志，不影响文字回复。</summary>
    private async Task<bool> SendStickerAsync(bool isGroup, long targetId, StickerRecord sticker, long? replyTo)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(sticker.AbsolutePath);
            return await _source.SendImageAsync(isGroup, targetId, bytes, replyToMessageId: replyTo);
        }
        catch (Exception ex)
        {
            EmitLog($"表情包发送失败（#{sticker.Id}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>日志用短文本（过长会把一行日志撞成好几行）。</summary>
    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    /// <summary>在会话里找某条 QQ 消息的下标（找不到返回 -1，例如已被滚动窗口裁掉）。</summary>
    private static int IndexOfMessage(IReadOnlyList<ChatMessage> messages, long? qqMessageId)
    {
        if (qqMessageId is not long id)
        {
            return -1;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].QqMessageId == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>机器人自己最后一条发言的下标（还没说过话返回 -1）。用来判断“这条触发消息是不是本轮的新诉求”。</summary>
    private static int LastSelfMessageIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.Self)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 本次要发的这句话，是不是和会话里自己上一条发言完全相同？
    /// （用于断掉“模型把自己上一条读进上下文 → 原样再发”的死循环）
    /// </summary>
    private static bool IsRepeatingOwnLastMessage(BotConversation conversation, string reply)
    {
        var messages = conversation.Messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != MessageRole.Self)
            {
                continue;
            }

            return string.Equals(messages[i].Text?.Trim() ?? string.Empty, reply.Trim(), StringComparison.Ordinal);
        }

        return false; // 自己还没说过话：不算复读
    }

    /// <summary>
    /// 发送回复。开启分句时按句末标点切分并留出打字间隔（更像真人）；
    /// 只有第一句带 QQ 的"回复"引用，后续分句不带。
    /// </summary>
    private async Task<bool> SendWithCadenceAsync(bool isGroup, long targetId, string reply, long? replyTo)
    {
        if (!_settings.SplitReplies)
        {
            return await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo);
        }

        var segments = SplitSentences(reply);
        if (segments.Count <= 1)
        {
            return await _source.SendTextAsync(isGroup, targetId, reply, replyToMessageId: replyTo);
        }

        var allOk = true;
        for (var i = 0; i < segments.Count; i++)
        {
            var ok = await _source.SendTextAsync(
                isGroup,
                targetId,
                segments[i],
                replyToMessageId: i == 0 ? replyTo : null);
            allOk &= ok;

            if (!ok)
            {
                EmitLog($"第 {i + 1}/{segments.Count} 段发送失败，停止后续分段");
                break;
            }

            // 打字节奏：基础间隔 + 按字数估算的输入时间
            if (i < segments.Count - 1)
            {
                var delay = Math.Max(0, _settings.SegmentDelayMs);
                await Task.Delay(delay);
            }
        }

        return allOk;
    }

    /// <summary>按句末标点分句；过短的句子合并到相邻段，最多切 4 段（避免连发刷屏）。</summary>
    private static List<string> SplitSentences(string text)
    {
        const int MinSegmentLength = 6;
        const int MaxSegments = 4;

        var trimmed = text.Trim();
        if (trimmed.Length <= MinSegmentLength * 2)
        {
            return new List<string> { trimmed };
        }

        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in trimmed)
        {
            current.Append(ch);
            if (!IsBreakChar(ch) || current.Length < MinSegmentLength)
            {
                continue; // 太短不单独成段，继续往后攒
            }

            segments.Add(current.ToString().Trim());
            current.Clear();
        }

        // 尾部残句并入上一段，避免丢字
        if (current.Length > 0)
        {
            var tail = current.ToString().Trim();
            if (segments.Count == 0)
            {
                segments.Add(tail);
            }
            else
            {
                segments[^1] = (segments[^1] + tail).Trim();
            }
        }

        // 超过段数上限：多余内容合并进最后一段（不丢内容）
        if (segments.Count > MaxSegments)
        {
            var head = segments.Take(MaxSegments - 1).ToList();
            head.Add(string.Concat(segments.Skip(MaxSegments - 1)));
            segments = head;
        }

        return segments.Where(s => s.Length > 0).ToList();
    }

    private static bool IsBreakChar(char ch) => ch is '。' or '！' or '？' or '!' or '?' or '…' or '\n' or '.';

    // ══════════ 内部辅助 ══════════

    /// <summary>写文件日志，并推送给 Web UI 日志面板。</summary>
    private void EmitLog(string message)
    {
        FileLog.Write("Agent", message);
        try
        {
            LogLine?.Invoke(message);
        }
        catch
        {
            // UI 推送失败不影响主流程
        }
    }

    /// <summary>切换「AI 正在思考」状态并通知 UI。</summary>
    private void SetThinking(BotConversation conversation, bool thinking)
    {
        if (conversation.Thinking == thinking)
        {
            return;
        }

        conversation.Thinking = thinking;
        ThinkingChanged?.Invoke(conversation.SourceKey);
    }

    /// <summary>把超出滚动窗口的旧消息追加到归档文件（JSONL），不丢历史但也不占内存。</summary>
    private void ArchiveEvicted(string sourceKey, IReadOnlyList<ChatMessage> evicted)
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "archive");
            Directory.CreateDirectory(dir);

            var safe = string.Concat(sourceKey.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var path = Path.Combine(dir, safe + ".jsonl");

            var sb = new System.Text.StringBuilder();
            foreach (var m in evicted)
            {
                sb.Append(System.Text.Json.JsonSerializer.Serialize(new
                {
                    t = m.Timestamp.ToUnixTimeSeconds(),
                    role = m.Role.ToString(),
                    sender = m.SenderName,
                    uid = m.SenderId,
                    mid = m.QqMessageId,
                    text = m.Text
                })).Append('\n');
            }

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            FileLog.Write("Archive", "归档失败: " + ex.Message);
        }
    }

    /// <summary>丢掉某会话的待回复项（删会话/改白名单时用）。在途那次不中斷，但不再补发后续。</summary>
    private void DropPending(string sourceKey)
    {
        _pendingReplies.TryRemove(sourceKey, out _);
        _pendingConversations.TryRemove(sourceKey, out _);
    }

    /// <summary>白名单变更后清掉不再允许的会话（与桌面版 ApplyWhitelistFilter 语义一致）。</summary>
    private void PruneNonWhitelisted()
    {
        List<BotConversation> removed;
        lock (_conversationsGate)
        {
            removed = _conversations.Where(c => !IsWhitelistedKey(c.SourceKey)).ToList();
            foreach (var c in removed)
            {
                _conversations.Remove(c);
            }
        }

        foreach (var c in removed)
        {
            _replyCooldown.TryRemove(c.SourceKey, out _);
            _historyRequested.TryRemove(c.SourceKey, out _);
            DropPending(c.SourceKey);
        }

        if (removed.Count > 0)
        {
            Save();
        }
    }
}
