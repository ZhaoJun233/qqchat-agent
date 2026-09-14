using System.Globalization;
using QQChatAgent.Services;

namespace QQChatAgent.Configuration;

/// <summary>
/// 十二要素配置：settings.json 提供默认值，环境变量覆盖。
/// 支持 <c>XXX_FILE</c> 形式读取 Docker secrets（/run/secrets/*），避免密钥出现在环境变量里。
///
/// 优先级分三类（重要）：
///   • 基础设施（协议端地址、Token、QQ 号…）：**环境变量永远优先** —— 它们属于部署环境职责。
///   • 模型配置（Base URL / 模型名 / API Key）：**面板改过就以面板为准**（存在 ModelBaseUrlOverride /
///     ModelOverride / data/secrets.json），因为用户会在这里换模型、换中转；环境变量只在面板没改过时当种子。
///   • 行为类（人设、白名单、欲望、阈值、冷却、分句…）：**配置文件优先**。
///     环境变量只在 settings.json 还不存在（首次部署）时当种子用；
///     一旦面板保存过一次，行为配置就归 settings.json 所有，不会被环境变量静默回滚。
/// </summary>
public static class BotConfig
{
    /// <summary>被环境变量提供但被 settings.json 接管的行为项（启动时提示用户）。</summary>
    public static List<string> IgnoredBehaviorEnvVars { get; } = new();

    /// <summary>被面板设置覆盖掉的环境变量（启动时提示用户，否则“改了 .env 怎么不生效”很难排查）。</summary>
    public static List<string> PanelOverriddenEnvVars { get; } = new();

    /// <summary>加载配置：配置文件 → 环境变量 → 必要的合规修正。</summary>
    public static AppSettings Load()
    {
        // “首次部署”的判据：**配置里还没有被存过**（以前是“settings.json 文件不存在”）。
        // ⚠ 这里不能再看文件是否存在 —— 数据搬到 SQLite 之后，库文件是启动时刚建的、永远存在，
        // 用它当判据会让“首次部署用环境变量当种子”永远不生效（踩过：白名单直接变空，机器人谁都不理）。
        var hasStored = SettingsStore.HasStoredSettings();
        var settings = SettingsStore.Load();
        ApplyInfrastructureEnvironment(settings);
        ApplyBehaviorEnvironment(settings, seedOnly: !hasStored);
        ApplyPanelOverrides(settings); // 面板改过的模型配置：优先于环境变量
        Normalize(settings);

        if (hasStored)
        {
            SettingsStore.Save(settings); // 把环境变量带来的基础设施值写回，保持库与实态一致
        }

        return settings;
    }

    /// <summary>基础设施与密钥：环境变量始终覆盖（改这些要重启容器）。</summary>
    private static void ApplyInfrastructureEnvironment(AppSettings s)
    {
        s.ApiKey = Secret("QQCHAT_API_KEY", "OPENAI_API_KEY") ?? s.ApiKey;
        s.ModelBaseUrl = Str("QQCHAT_BASE_URL", "OPENAI_BASE_URL") ?? s.ModelBaseUrl;
        s.Model = Str("QQCHAT_MODEL", "OPENAI_MODEL") ?? s.Model;
        s.OneBotToken = Secret("QQCHAT_ONEBOT_TOKEN") ?? s.OneBotToken;
        s.QuickLoginUin = Str("QQCHAT_UIN", "QQCHAT_QUICK_LOGIN_UIN") ?? s.QuickLoginUin;
        s.OneBotProtocol = Str("QQCHAT_ONEBOT_PROTOCOL") ?? s.OneBotProtocol;
        s.OneBotAddress = Str("QQCHAT_ONEBOT_URL", "QQCHAT_ONEBOT_ADDRESS") ?? s.OneBotAddress;
        s.HealthPort = Int("QQCHAT_HEALTH_PORT") ?? s.HealthPort;
        s.PanelToken = Str("QQCHAT_PANEL_TOKEN") ?? s.PanelToken;
        s.NapCatWebUiUrl = Str("QQCHAT_NAPCAT_WEBUI_URL") ?? s.NapCatWebUiUrl;
        s.NapCatWebUiToken = Secret("QQCHAT_NAPCAT_WEBUI_TOKEN") ?? s.NapCatWebUiToken;
        s.VerboseLog = Bool("QQCHAT_VERBOSE") ?? s.VerboseLog;
    }

    /// <summary>
    /// 行为类配置：仅当 settings.json 不存在（首次部署）时用环境变量做种子；
    /// 已存在则完全不碰，由 Web 面板 / 配置文件说了算。
    /// </summary>
    private static void ApplyBehaviorEnvironment(AppSettings s, bool seedOnly)
    {
        // 需要种子的字段（名字 → 环境变量）
        var behaviorVars = new Dictionary<string, string[]>
        {
            [nameof(AppSettings.BotPersona)] = new[] { "QQCHAT_PERSONA" },
            [nameof(AppSettings.MessageWhitelist)] = new[] { "QQCHAT_WHITELIST" },
            [nameof(AppSettings.AiDesire)] = new[] { "QQCHAT_AI_DESIRE" },
            [nameof(AppSettings.SuitabilityThreshold)] = new[] { "QQCHAT_SUITABILITY_THRESHOLD" },
            [nameof(AppSettings.AiModeEnabled)] = new[] { "QQCHAT_AI_MODE" },
            [nameof(AppSettings.MaxTokens)] = new[] { "QQCHAT_MAX_TOKENS" },
            [nameof(AppSettings.PrivateCooldownSeconds)] = new[] { "QQCHAT_PRIVATE_COOLDOWN" },
            [nameof(AppSettings.GroupCooldownSeconds)] = new[] { "QQCHAT_GROUP_COOLDOWN" },
            [nameof(AppSettings.IdleFallbackSeconds)] = new[] { "QQCHAT_IDLE_FALLBACK" },
            [nameof(AppSettings.SplitReplies)] = new[] { "QQCHAT_SPLIT_REPLIES" },
            [nameof(AppSettings.SegmentDelayMs)] = new[] { "QQCHAT_SEGMENT_DELAY_MS" },
            [nameof(AppSettings.MaxContextMessages)] = new[] { "QQCHAT_MAX_CONTEXT" },
            [nameof(AppSettings.ProfileLookupCount)] = new[] { "QQCHAT_PROFILE_LOOKUP" },
            [nameof(AppSettings.ProfileSummaryLines)] = new[] { "QQCHAT_PROFILE_LINES" },
            [nameof(AppSettings.MaxProfileChars)] = new[] { "QQCHAT_PROFILE_CHARS" },
            [nameof(AppSettings.EnableProfileSummary)] = new[] { "QQCHAT_PROFILE_SUMMARY" },
            [nameof(AppSettings.ProfileSummaryThreshold)] = new[] { "QQCHAT_PROFILE_SUMMARY_THRESHOLD" },
            [nameof(AppSettings.ProfileSummaryMaxChars)] = new[] { "QQCHAT_PROFILE_SUMMARY_CHARS" },
            [nameof(AppSettings.ProfileSummaryIntervalSeconds)] = new[] { "QQCHAT_PROFILE_SUMMARY_INTERVAL" },
            [nameof(AppSettings.MaxMessagesPerConversation)] = new[] { "QQCHAT_MAX_MESSAGES" },
            [nameof(AppSettings.MaxConcurrentReplies)] = new[] { "QQCHAT_CONCURRENCY" },
            [nameof(AppSettings.EnableStickers)] = new[] { "QQCHAT_STICKERS" },
            [nameof(AppSettings.StickerLibraryMax)] = new[] { "QQCHAT_STICKER_MAX" },
            [nameof(AppSettings.StickerCandidates)] = new[] { "QQCHAT_STICKER_CANDIDATES" },
            [nameof(AppSettings.StickerCurateIntervalSeconds)] = new[] { "QQCHAT_STICKER_CURATE_INTERVAL" },
            [nameof(AppSettings.StickerCooldownSeconds)] = new[] { "QQCHAT_STICKER_COOLDOWN" },
    [nameof(AppSettings.EnablePoke)] = new[] { "QQCHAT_ENABLE_POKE" },
    [nameof(AppSettings.PokeCooldownSeconds)] = new[] { "QQCHAT_POKE_COOLDOWN" },
    [nameof(AppSettings.MoodTtlSeconds)] = new[] { "QQCHAT_MOOD_TTL" },
    [nameof(AppSettings.EnableMusic)] = new[] { "QQCHAT_ENABLE_MUSIC" },
    [nameof(AppSettings.MusicListenCooldownSeconds)] = new[] { "QQCHAT_MUSIC_LISTEN_COOLDOWN" },
    [nameof(AppSettings.MusicUnderstandModel)] = new[] { "QQCHAT_MUSIC_MODEL" },
    [nameof(AppSettings.MusicSendAudioToModel)] = new[] { "QQCHAT_MUSIC_SEND_AUDIO" },
    [nameof(AppSettings.MusicAudioToModelMaxKb)] = new[] { "QQCHAT_MUSIC_AUDIO_MAX_KB" },
    [nameof(AppSettings.MusicSources)] = new[] { "QQCHAT_MUSIC_SOURCES" },
    [nameof(AppSettings.NeteaseBaseUrl)] = new[] { "QQCHAT_NETEASE_BASE_URL" },
    [nameof(AppSettings.EnableLinkPreview)] = new[] { "QQCHAT_LINK_PREVIEW" },
    [nameof(AppSettings.EnableWebSearch)] = new[] { "QQCHAT_WEB_SEARCH" },
    [nameof(AppSettings.WebSearchUseModelSearch)] = new[] { "QQCHAT_SEARCH_USE_MODEL" },
    [nameof(AppSettings.WebSearchSources)] = new[] { "QQCHAT_SEARCH_SOURCES" },
    [nameof(AppSettings.WebSearchMaxResults)] = new[] { "QQCHAT_SEARCH_MAX_RESULTS" },
    [nameof(AppSettings.WebSearchCooldownSeconds)] = new[] { "QQCHAT_SEARCH_COOLDOWN" },
    [nameof(AppSettings.WebSearchTimeoutSeconds)] = new[] { "QQCHAT_SEARCH_TIMEOUT" },
    [nameof(AppSettings.WebSearchReadMaxChars)] = new[] { "QQCHAT_SEARCH_READ_CHARS" },
    [nameof(AppSettings.EnableVoice)] = new[] { "QQCHAT_ENABLE_VOICE" },    [nameof(AppSettings.VoiceName)] = new[] { "QQCHAT_VOICE" },
    [nameof(AppSettings.VoiceSpeed)] = new[] { "QQCHAT_VOICE_SPEED" },
    [nameof(AppSettings.VoiceMaxChars)] = new[] { "QQCHAT_VOICE_MAX_CHARS" },
    [nameof(AppSettings.TtsServiceUrl)] = new[] { "QQCHAT_TTS_URL" },
    [nameof(AppSettings.LinkPreviewTimeoutSeconds)] = new[] { "QQCHAT_LINK_PREVIEW_TIMEOUT" },
    [nameof(AppSettings.LinkPreviewMax)] = new[] { "QQCHAT_LINK_PREVIEW_MAX" },
    [nameof(AppSettings.MusicBitrate)] = new[] { "QQCHAT_MUSIC_BITRATE" },
    [nameof(AppSettings.MusicMaxDownloadMb)] = new[] { "QQCHAT_MUSIC_MAX_MB" },
    [nameof(AppSettings.MusicMaxAnalysisSeconds)] = new[] { "QQCHAT_MUSIC_ANALYSIS_SECONDS" },
    [nameof(AppSettings.MusicLibraryMax)] = new[] { "QQCHAT_MUSIC_LIBRARY_MAX" },
    [nameof(AppSettings.MusicNoteTtlDays)] = new[] { "QQCHAT_MUSIC_NOTE_TTL_DAYS" },
    [nameof(AppSettings.MusicKeepAudio)] = new[] { "QQCHAT_MUSIC_KEEP_AUDIO" }
        };

        if (!seedOnly)
        {
            // 文件已存在：这些环境变量不再生效。
            // 只在“环境变量的值与文件里的值不一致”时提醒 —— 这正是“改了 .env 却不生效”的场景；
            // 两者一致就不必刷屏。
            foreach (var (field, names) in behaviorVars)
            {
                var envValue = names
                    .Select(Environment.GetEnvironmentVariable)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (envValue is null)
                {
                    continue;
                }

                var property = typeof(AppSettings).GetProperty(field);
                var fileValue = property?.GetValue(s);
                if (!ValuesMatch(property?.PropertyType, envValue, fileValue))
                {
                    IgnoredBehaviorEnvVars.Add(
                        $"{names[0]}={Truncate(envValue)}  ≠ 配置文件的 {Truncate(fileValue?.ToString())}");
                }
            }

            return;
        }

        // 首次部署：环境变量当种子
        s.BotPersona = Secret("QQCHAT_PERSONA") ?? s.BotPersona;
        s.MessageWhitelist = Str("QQCHAT_WHITELIST") ?? s.MessageWhitelist;
        s.AiDesire = Int("QQCHAT_AI_DESIRE") ?? s.AiDesire;
        s.SuitabilityThreshold = Int("QQCHAT_SUITABILITY_THRESHOLD") ?? s.SuitabilityThreshold;
        s.AiModeEnabled = Bool("QQCHAT_AI_MODE") ?? s.AiModeEnabled;
        s.MaxTokens = Int("QQCHAT_MAX_TOKENS") ?? s.MaxTokens;
        s.PrivateCooldownSeconds = Int("QQCHAT_PRIVATE_COOLDOWN") ?? s.PrivateCooldownSeconds;
        s.GroupCooldownSeconds = Int("QQCHAT_GROUP_COOLDOWN") ?? s.GroupCooldownSeconds;
        s.IdleFallbackSeconds = Int("QQCHAT_IDLE_FALLBACK") ?? s.IdleFallbackSeconds;
        s.SplitReplies = Bool("QQCHAT_SPLIT_REPLIES") ?? s.SplitReplies;
        s.SegmentDelayMs = Int("QQCHAT_SEGMENT_DELAY_MS") ?? s.SegmentDelayMs;
        s.MaxContextMessages = Int("QQCHAT_MAX_CONTEXT") ?? s.MaxContextMessages;
        s.ProfileLookupCount = Int("QQCHAT_PROFILE_LOOKUP") ?? s.ProfileLookupCount;
        s.ProfileSummaryLines = Int("QQCHAT_PROFILE_LINES") ?? s.ProfileSummaryLines;
        s.MaxProfileChars = Int("QQCHAT_PROFILE_CHARS") ?? s.MaxProfileChars;
        s.EnableProfileSummary = Bool("QQCHAT_PROFILE_SUMMARY") ?? s.EnableProfileSummary;
        s.ProfileSummaryThreshold = Int("QQCHAT_PROFILE_SUMMARY_THRESHOLD") ?? s.ProfileSummaryThreshold;
        s.ProfileSummaryMaxChars = Int("QQCHAT_PROFILE_SUMMARY_CHARS") ?? s.ProfileSummaryMaxChars;
        s.ProfileSummaryIntervalSeconds = Int("QQCHAT_PROFILE_SUMMARY_INTERVAL") ?? s.ProfileSummaryIntervalSeconds;
        s.MaxMessagesPerConversation = Int("QQCHAT_MAX_MESSAGES") ?? s.MaxMessagesPerConversation;
        s.MaxConcurrentReplies = Int("QQCHAT_CONCURRENCY") ?? s.MaxConcurrentReplies;
        s.EnableStickers = Bool("QQCHAT_STICKERS") ?? s.EnableStickers;
        s.StickerLibraryMax = Int("QQCHAT_STICKER_MAX") ?? s.StickerLibraryMax;
        s.StickerCandidates = Int("QQCHAT_STICKER_CANDIDATES") ?? s.StickerCandidates;
        s.StickerCurateIntervalSeconds = Int("QQCHAT_STICKER_CURATE_INTERVAL") ?? s.StickerCurateIntervalSeconds;
        s.StickerCooldownSeconds = Int("QQCHAT_STICKER_COOLDOWN") ?? s.StickerCooldownSeconds;
        s.EnablePoke = Bool("QQCHAT_ENABLE_POKE") ?? s.EnablePoke;
        s.PokeCooldownSeconds = Int("QQCHAT_POKE_COOLDOWN") ?? s.PokeCooldownSeconds;
        s.MoodTtlSeconds = Int("QQCHAT_MOOD_TTL") ?? s.MoodTtlSeconds;
        s.EnableMusic = Bool("QQCHAT_ENABLE_MUSIC") ?? s.EnableMusic;
        s.MusicListenCooldownSeconds = Int("QQCHAT_MUSIC_LISTEN_COOLDOWN") ?? s.MusicListenCooldownSeconds;
        s.MusicUnderstandModel = Str("QQCHAT_MUSIC_MODEL") ?? s.MusicUnderstandModel;
        s.MusicSendAudioToModel = Bool("QQCHAT_MUSIC_SEND_AUDIO") ?? s.MusicSendAudioToModel;
        s.MusicAudioToModelMaxKb = Int("QQCHAT_MUSIC_AUDIO_MAX_KB") ?? s.MusicAudioToModelMaxKb;
        s.MusicSources = Str("QQCHAT_MUSIC_SOURCES") ?? s.MusicSources;
        s.NeteaseBaseUrl = Str("QQCHAT_NETEASE_BASE_URL") ?? s.NeteaseBaseUrl;
        s.EnableLinkPreview = Bool("QQCHAT_LINK_PREVIEW") ?? s.EnableLinkPreview;
        s.EnableWebSearch = Bool("QQCHAT_WEB_SEARCH") ?? s.EnableWebSearch;
        s.WebSearchUseModelSearch = Bool("QQCHAT_SEARCH_USE_MODEL") ?? s.WebSearchUseModelSearch;
        s.WebSearchSources = Str("QQCHAT_SEARCH_SOURCES") ?? s.WebSearchSources;
        s.WebSearchMaxResults = Int("QQCHAT_SEARCH_MAX_RESULTS") ?? s.WebSearchMaxResults;
        s.WebSearchCooldownSeconds = Int("QQCHAT_SEARCH_COOLDOWN") ?? s.WebSearchCooldownSeconds;
        s.WebSearchTimeoutSeconds = Int("QQCHAT_SEARCH_TIMEOUT") ?? s.WebSearchTimeoutSeconds;
        s.WebSearchReadMaxChars = Int("QQCHAT_SEARCH_READ_CHARS") ?? s.WebSearchReadMaxChars;
        s.EnableVoice = Bool("QQCHAT_ENABLE_VOICE") ?? s.EnableVoice;
        s.VoiceName = Str("QQCHAT_VOICE") ?? s.VoiceName;
        s.VoiceSpeed = Int("QQCHAT_VOICE_SPEED") ?? s.VoiceSpeed;
        s.VoiceMaxChars = Int("QQCHAT_VOICE_MAX_CHARS") ?? s.VoiceMaxChars;
        s.TtsServiceUrl = Str("QQCHAT_TTS_URL") ?? s.TtsServiceUrl;
        s.LinkPreviewTimeoutSeconds = Int("QQCHAT_LINK_PREVIEW_TIMEOUT") ?? s.LinkPreviewTimeoutSeconds;
        s.LinkPreviewMax = Int("QQCHAT_LINK_PREVIEW_MAX") ?? s.LinkPreviewMax;
        s.MusicBitrate = Int("QQCHAT_MUSIC_BITRATE") ?? s.MusicBitrate;
        s.MusicMaxDownloadMb = Int("QQCHAT_MUSIC_MAX_MB") ?? s.MusicMaxDownloadMb;
        s.MusicMaxAnalysisSeconds = Int("QQCHAT_MUSIC_ANALYSIS_SECONDS") ?? s.MusicMaxAnalysisSeconds;
        s.MusicLibraryMax = Int("QQCHAT_MUSIC_LIBRARY_MAX") ?? s.MusicLibraryMax;
        s.MusicNoteTtlDays = Int("QQCHAT_MUSIC_NOTE_TTL_DAYS") ?? s.MusicNoteTtlDays;
        s.MusicKeepAudio = Bool("QQCHAT_MUSIC_KEEP_AUDIO") ?? s.MusicKeepAudio;
        s.NeteaseCookie = Str("QQCHAT_NETEASE_COOKIE") ?? s.NeteaseCookie;
    }

    /// <summary>
    /// 面板里改过的模型配置（端点 / 模型名 / API Key）优先于环境变量。
    /// 以前这三项只读环境变量：想换个中转、换模型、换密钥都得改 .env 重启容器；
    /// 现在面板里改完立即生效并落盘（密钥单独存 data/secrets.json，权限 600，不进 settings.json）。
    /// 环境变量退居“首次部署的种子”：面板没改过时照旧生效。
    /// </summary>
    private static void ApplyPanelOverrides(AppSettings s)
    {
        SecretsStore.Init(AppPaths.RuntimeRoot);
        s.ApiKeyOverride ??= SecretsStore.LoadApiKey();

        if (!string.IsNullOrWhiteSpace(s.ModelBaseUrlOverride))
        {
            if (!string.IsNullOrWhiteSpace(Str("QQCHAT_BASE_URL", "OPENAI_BASE_URL")))
            {
                PanelOverriddenEnvVars.Add("QQCHAT_BASE_URL → 面板里的 Base URL");
            }

            s.ModelBaseUrl = s.ModelBaseUrlOverride!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(s.ModelOverride))
        {
            if (!string.IsNullOrWhiteSpace(Str("QQCHAT_MODEL", "OPENAI_MODEL")))
            {
                PanelOverriddenEnvVars.Add("QQCHAT_MODEL → 面板里的模型名");
            }

            s.Model = s.ModelOverride!.Trim();
        }

        if (!string.IsNullOrWhiteSpace(s.ApiKeyOverride))
        {
            if (!string.IsNullOrWhiteSpace(Secret("QQCHAT_API_KEY", "OPENAI_API_KEY")))
            {
                PanelOverriddenEnvVars.Add("QQCHAT_API_KEY → 面板里填的密钥");
            }

            s.ApiKey = s.ApiKeyOverride!.Trim();
        }
    }

    /// <summary>修正非法/矛盾配置，避免容器里静默跑错。</summary>
    private static void Normalize(AppSettings s)
    {
        s.ModelBaseUrl = s.ModelBaseUrl.Trim();
        s.ApiKey = s.ApiKey.Trim();
        s.Model = s.Model.Trim();
        s.OneBotAddress = s.OneBotAddress.Trim();
        s.OneBotToken = s.OneBotToken.Trim();
        s.QuickLoginUin = s.QuickLoginUin.Trim();
        s.AiDesire = Math.Clamp(s.AiDesire, 0, 100);
        s.SuitabilityThreshold = Math.Clamp(s.SuitabilityThreshold, 0, 100);
        s.MaxTokens = s.MaxTokens > 0 ? s.MaxTokens : 2048;
        s.MaxContextMessages = Math.Clamp(s.MaxContextMessages, 10, 1000);
        s.ProfileLookupCount = Math.Clamp(s.ProfileLookupCount, 0, 50);
        s.ProfileSummaryLines = Math.Clamp(s.ProfileSummaryLines, 0, 50);
        s.MaxProfileChars = Math.Clamp(s.MaxProfileChars, 0, 20000);
        s.ProfileSummaryThreshold = Math.Clamp(s.ProfileSummaryThreshold, 5, 500);
        s.ProfileSummaryMaxChars = Math.Clamp(s.ProfileSummaryMaxChars, 40, 2000);
        s.ProfileSummaryIntervalSeconds = Math.Clamp(s.ProfileSummaryIntervalSeconds, 0, 86400);
        s.MaxMessagesPerConversation = Math.Clamp(s.MaxMessagesPerConversation, 20, 100000);
        s.MaxConcurrentReplies = Math.Clamp(s.MaxConcurrentReplies, 1, 16);
        s.StickerLibraryMax = Math.Clamp(s.StickerLibraryMax, 0, 2000);
        s.StickerCandidates = Math.Clamp(s.StickerCandidates, 0, 20);
        s.StickerCurateIntervalSeconds = Math.Clamp(s.StickerCurateIntervalSeconds, 0, 86400);
        s.StickerCooldownSeconds = Math.Clamp(s.StickerCooldownSeconds, 0, 86400);
        s.PokeCooldownSeconds = Math.Clamp(s.PokeCooldownSeconds, 0, 86400);
        s.MoodTtlSeconds = Math.Clamp(s.MoodTtlSeconds, 0, 86400 * 7);
        s.SegmentDelayMs = Math.Max(0, s.SegmentDelayMs);
        s.HealthPort = s.HealthPort is >= 0 and <= 65535 ? s.HealthPort : 8080;
        s.NapCatWebUiUrl = s.NapCatWebUiUrl.Trim().TrimEnd('/');
        s.NapCatWebUiToken = s.NapCatWebUiToken.Trim();

        // 兼容：桌面版固定 ForwardWebSocket，但配置文件可能是遗留值
        if (string.IsNullOrWhiteSpace(s.OneBotProtocol))
        {
            s.OneBotProtocol = "ForwardWebSocket";
        }
    }

    /// <summary>启动前校验；返回致命问题列表（非空则拒绝启动）。</summary>
    public static List<string> Validate(AppSettings s)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(s.ApiKey))
        {
            errors.Add("未配置 API Key（设 QQCHAT_API_KEY 或在 settings.json 里填 ApiKey）");
        }

        if (string.IsNullOrWhiteSpace(s.Model))
        {
            errors.Add("未配置模型名（QQCHAT_MODEL）");
        }

        if (!Uri.TryCreate(s.ModelBaseUrl, UriKind.Absolute, out var modelUri) ||
            (modelUri.Scheme != "http" && modelUri.Scheme != "https"))
        {
            errors.Add($"ModelBaseUrl 不是合法 URL: '{s.ModelBaseUrl}'");
        }

        if (s.OneBotProtocol is not ("ForwardWebSocket" or "ReverseWebSocket" or "Http"))
        {
            errors.Add($"OneBotProtocol 只能是 ForwardWebSocket / ReverseWebSocket / Http，当前 '{s.OneBotProtocol}'");
        }

        // 正向 WS：连出去，必须是 ws(s)；反向 WS：本地监听地址，http(s) 与 ws(s) 都常见；HTTP：http(s)
        var allowedSchemes = s.OneBotProtocol switch
        {
            "ForwardWebSocket" => new[] { "ws", "wss" },
            "ReverseWebSocket" => new[] { "http", "https", "ws", "wss" },
            _ => new[] { "http", "https" }
        };

        if (!Uri.TryCreate(s.OneBotAddress, UriKind.Absolute, out var obUri) || !allowedSchemes.Contains(obUri.Scheme))
        {
            var hint = s.OneBotProtocol switch
            {
                "ForwardWebSocket" => "例：ws://napcat:3001",
                "ReverseWebSocket" => "例：http://0.0.0.0:3001（本地监听，等协议端连入）",
                _ => "例：http://napcat:3000"
            };
            errors.Add($"OneBotAddress 与协议不匹配（{s.OneBotProtocol} 需要 {string.Join('/', allowedSchemes)}://，{hint}）: '{s.OneBotAddress}'");
        }

        if (!string.IsNullOrWhiteSpace(s.QuickLoginUin) && !long.TryParse(s.QuickLoginUin, out _))
        {
            errors.Add($"QQ 号必须是纯数字: '{s.QuickLoginUin}'");
        }

        return errors;
    }

    /// <summary>比较环境变量文本与配置值：布尔型归一化 1/0/true/false，避免误报。</summary>
    private static bool ValuesMatch(Type? type, string envValue, object? fileValue)
    {
        var env = envValue.Trim();

        if (type == typeof(bool))
        {
            var parsed = env.ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "y" or "on" => true,
                "0" or "false" or "no" or "n" or "off" => false,
                _ => (bool?)null
            };
            return parsed is null || parsed == (bool?)fileValue;
        }

        return string.Equals(env, fileValue?.ToString()?.Trim(), StringComparison.Ordinal);
    }

    private static string Truncate(string? s, int max = 28)
    {
        s = (s ?? string.Empty).Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    // ---------- 读取辅助 ----------

    private static string? Str(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>读取密钥：优先 <c>{NAME}_FILE</c>（Docker secrets），其次 <c>{NAME}</c>。</summary>
    private static string? Secret(params string[] names)
    {
        foreach (var name in names)
        {
            var path = Environment.GetEnvironmentVariable(name + "_FILE");
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    if (File.Exists(path))
                    {
                        return File.ReadAllText(path).Trim();
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Config] 读取 {path} 失败: {ex.Message}");
                }
            }
        }

        return Str(names);
    }

    private static int? Int(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Console.Error.WriteLine($"[Config] {name}='{raw}' 不是整数，已忽略");
        return null;
    }

    private static bool? Bool(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => Warn(name, raw)
        };

        static bool? Warn(string n, string v)
        {
            Console.Error.WriteLine($"[Config] {n}='{v}' 不是布尔值，已忽略");
            return null;
        }
    }
}
