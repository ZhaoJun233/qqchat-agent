using System.Text.Json.Serialization;

namespace QQChatAgent.Services;

/// <summary>
/// 机器人配置（headless）。
/// 与 WinUI 版 <c>settings.json</c> 字段兼容：把桌面版的 runtime/data/settings.json 直接挂进容器即可复用。
///
/// ⚠ 所有权规则（避免“改了却没生效”）：
///   • **密钥**（ApiKey / OneBotToken / PanelToken）：只从环境变量读，**不落盘**，因此不进 settings.json。
///     好处：配置文件可以随便备份/分享/贴日志，不会泄密。
///   • **基础设施**（模型地址、模型名、OneBot 地址、端口…）：环境变量始终覆盖。
///   • **行为**（人设、白名单、阈值、限流…）：settings.json 是唯一所有者；环境变量只在首次部署当种子。
/// </summary>
public sealed class AppSettings
{
    // ---------- Agent 大脑（OpenAI 兼容） ----------

    public string ModelBaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>模型密钥。环境变量专属（QQCHAT_API_KEY），不写入 settings.json。</summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// 面板里改过的模型端点；空 = 用环境变量 QQCHAT_BASE_URL。
    /// 为什么要这层“覆盖”：以前这三项只读环境变量，想换个中转/换模型就得改 .env 重启容器；
    /// 现在面板是“最新意图”，环境变量退居“首次部署的种子”（面板没改过时才用它）。
    /// </summary>
    public string? ModelBaseUrlOverride { get; set; }

    /// <summary>面板里改过的模型名；空 = 用环境变量 QQCHAT_MODEL。</summary>
    public string? ModelOverride { get; set; }

    /// <summary>面板里填的 API Key（**不落 settings.json**，单独存 data/secrets.json，文件权限 600）。</summary>
    [JsonIgnore]
    public string? ApiKeyOverride { get; set; }

    public int MaxTokens { get; set; } = 2048;

    // ---------- OneBot 通道 ----------

    /// <summary>ForwardWebSocket（推荐）/ ReverseWebSocket / Http。</summary>
    public string OneBotProtocol { get; set; } = "ForwardWebSocket";

    /// <summary>正向：ws://napcat:3001；反向：http://0.0.0.0:3001；HTTP：http://napcat:3000。</summary>
    public string OneBotAddress { get; set; } = "ws://127.0.0.1:3001";

    /// <summary>OneBot 访问令牌。环境变量专属（QQCHAT_ONEBOT_TOKEN），不写入 settings.json。</summary>
    [JsonIgnore]
    public string OneBotToken { get; set; } = string.Empty;

    /// <summary>机器人登录 QQ 号（用于自我识别与 @ 判断；桌面版沿用此字段名）。</summary>
    public string QuickLoginUin { get; set; } = string.Empty;

    // ---------- 行为 ----------

    /// <summary>AI 对话欲望（0-100：越高越主动参与群聊；默认 50）。</summary>
    public int AiDesire { get; set; } = 50;

    /// <summary>发言适合度阈值（0-100）：模型评分低于此值则沉默，默认 10。</summary>
    public int SuitabilityThreshold { get; set; } = 10;

    /// <summary>消息白名单（每行/逗号分隔一个 QQ 号或群号；留空=全部忽略，严格模式）。</summary>
    public string MessageWhitelist { get; set; } = string.Empty;

    /// <summary>模型人设档案（可选，定义机器人角色的性格/说话风格）。</summary>
    public string BotPersona { get; set; } = string.Empty;

    /// <summary>是否开启 AI 自动回复（容器里无人点按钮，故用配置控制）。</summary>
    public bool AiModeEnabled { get; set; } = true;

    // ---------- 限流与节奏 ----------

    /// <summary>私聊回复冷却（秒）。</summary>
    public int PrivateCooldownSeconds { get; set; } = 3;

    /// <summary>群聊回复冷却（秒）。</summary>
    public int GroupCooldownSeconds { get; set; } = 8;

    /// <summary>静默兜底：超过该秒数没有主动请求时，为待处理会话补一次请求。0=关闭。</summary>
    public int IdleFallbackSeconds { get; set; } = 60;

    /// <summary>长回复按句末标点分句发送（更像真人打字）。</summary>
    public bool SplitReplies { get; set; } = true;

    /// <summary>
    /// 主动开口（陪伴感）：群里安静下来、又有人情绪低落或者刚聊得热闹时，它可能自己开一句。
    /// 不是“定时发广告”：还受同会话冷却（<see cref="ProactiveCooldownSeconds" />）、
    /// “最后一条不是自己说的”、“群聊限定”等多道限制，没由头就不出声。
    /// </summary>
    public bool EnableProactive { get; set; } = true;

    /// <summary>同一会话两次主动开口的最小间隔（秒），防自说自话；默认半小时一句。</summary>
    public int ProactiveCooldownSeconds { get; set; } = 1800;

    /// <summary>
    /// 群里要安静多久才算“安静下来”（秒）：没到这个时长就不主动插话（否则就是抢话）。
    /// 默认 120；测试/自己调折腾时可以调小。
    /// </summary>
    public int ProactiveQuietSeconds { get; set; } = 120;

    /// <summary>
    /// 忽略“只有括号”的群消息（如“（笑）”“（bushi）”“( 跑 )”）。
    /// 为什么要这个开关：群里这类旁白很多，它们不针对任何人、也没什么信息，
    /// 却会占上下文并可能把机器人拉出来接话（“（笑）”接什么？）。
    /// 判定口径：去掉所有括号段、空白与标点后不剩内容才算 —— “今天天气不错（大概）”不会误伤。
    /// 只影响群聊；带图、带 @ 机器人、私聊的消息永远不忽略（宁可多回也不装死）。
    /// </summary>
    public bool IgnoreBracketMessages { get; set; }

    /// <summary>分段发送时每段之间的时间基准（毫秒）。</summary>
    public int SegmentDelayMs { get; set; } = 700;

    /// <summary>给模型的最大上下文消息条数。</summary>
    public int MaxContextMessages { get; set; } = 200;

    /// <summary>回复时附带上下文里出现的人物档案数量上限。</summary>
    public int ProfileLookupCount { get; set; } = 8;

    /// <summary>每个人物档案向模型注入的历史条数上限。</summary>
    public int ProfileSummaryLines { get; set; } = 8;

    /// <summary>人物档案注入的总字符预算（超出则丢弃最早发言者的档案）。</summary>
    public int MaxProfileChars { get; set; } = 1200;

    /// <summary>用模型把历史发言压缩成人物画像（长期记忆）。</summary>
    public bool EnableProfileSummary { get; set; } = true;

    /// <summary>某会话范围累计多少条新发言后重新做一次画像。</summary>
    public int ProfileSummaryThreshold { get; set; } = 20;

    /// <summary>画像字数上限。</summary>
    public int ProfileSummaryMaxChars { get; set; } = 160;

    /// <summary>画像巡检间隔（秒）。0 = 关闭。</summary>
    public int ProfileSummaryIntervalSeconds { get; set; } = 120;

    /// <summary>单个会话在内存/磁盘保留的消息条数（超出部分归档到 archive/*.jsonl）。</summary>
    public int MaxMessagesPerConversation { get; set; } = 500;

    // ---------- 表情包（全局共用一个库） ----------

    /// <summary>启用表情包：自动收集群友发的图 + 按语境发出去 + 定期自巡检。</summary>
    public bool EnableStickers { get; set; } = true;

    /// <summary>表情包库存储上限（张）。超出按“用得少 + 最久没用”淘汰。</summary>
    public int StickerLibraryMax { get; set; } = 120;

    /// <summary>每次给模型看的候选张数（按语境检索出来的）。</summary>
    public int StickerCandidates { get; set; } = 6;

    /// <summary>表情包自巡检间隔（秒）：机器人自己看一遍库，决定删哪些。0 = 关。</summary>
    public int StickerCurateIntervalSeconds { get; set; } = 3600;

    /// <summary>同一会话两次发表情包的最小间隔（秒）。0 = 不限。</summary>
    public int StickerCooldownSeconds { get; set; } = 120;

    /// <summary>戳一戳：是否处理戳一戳事件（被人戳时按语境回话或戳回去）。</summary>
    public bool EnablePoke { get; set; } = true;

    /// <summary>
    /// 戳一戳冷却（秒）：同一个人连着戳时至少隔这么久才回应一次；
    /// 也是机器人主动戳人的最小间隔。0 = 不限。
    /// </summary>
    public int PokeCooldownSeconds { get; set; } = 45;

    /// <summary>
    /// 模型写的心情保留多久（秒）：超过这个时间没更新就回落到“按被戳次数自动描述”。
    /// 0 = 不过期（一直用模型写的那句）。
    /// </summary>
    public int MoodTtlSeconds { get; set; } = 7200;

    /// <summary>同时向模型发起的最大请求数（按会话串行、跨会话并发）。</summary>
    public int MaxConcurrentReplies { get; set; } = 2;

    // ---------- 语音消息（TTS）----------

    /// <summary>
    /// 启用语音消息：机器人可以用语音说话（模型在 JSON 里填 speak 字段时）。
    /// 默认关 —— 语音比文字“重”，且需要先部署 TTS 服务。
    /// </summary>
    public bool EnableVoice { get; set; }

    /// <summary>音色（Piper 模型名）。可选：zh_CN-huayan-medium / zh_CN-huayan-x_low / zh_CN-xiao_ya-medium / zh_CN-chaowen-medium。</summary>
    public string VoiceName { get; set; } = "zh_CN-huayan-medium";

    /// <summary>语速（百分比，100 = 原速）。</summary>
    public int VoiceSpeed { get; set; } = 100;

    /// <summary>单条语音的字数上限：超过就不发语音（长了又慢又费流量，不如打字）。</summary>
    public int VoiceMaxChars { get; set; } = 80;

    /// <summary>TTS 服务地址（Piper 旁路容器，提供 /speak?text=… 返回 wav）。</summary>
    public string TtsServiceUrl { get; set; } = "http://tts:5000";

    // ---------- 链接与分享卡片 ----------

    /// <summary>群里发的链接要不要真打开看一下（取标题/摘要）——给模型“看看里面写了什么”的根据。</summary>
    public bool EnableLinkPreview { get; set; } = true;

    /// <summary>单个链接的抓取超时（秒）。慢站点不能拖住机器人。</summary>
    public int LinkPreviewTimeoutSeconds { get; set; } = 5;

    /// <summary>一条消息里最多预览几个链接。</summary>
    public int LinkPreviewMax { get; set; } = 3;

    // ---------- 联网搜索（模型可以要求“去查一下”） ----------

    /// <summary>启用联网搜索：模型在 JSON 里填 search 字段时，机器人真去搜，拿到事实后再回。默认开。</summary>
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>
    /// 优先用“模型自带搜索”（OpenAI 兼容网关背后的 Gemini 原生端点 + google_search 工具）。
    /// 为什么默认走它：机房 IP 上爬网页搜索基本拿不到结果（Google/Bing/DDG/百度 都拦），
    /// 而模型订阅本来就能搜 —— 不额外要密钥、不爬虫、结果还带来源。
    /// 关掉就只用下面的搜索源模板。
    /// </summary>
    public bool WebSearchUseModelSearch { get; set; } = true;

    /// <summary>
    /// 搜索源模板（每行一条，name|url；{q} = 查询词，会做 URL 编码）。兜底用：
    /// 模型搜索不可用（不是 Gemini、代理不转发工具）时才走这里。
    /// name 决定解析方式：searx* = SearxNG JSON；wiki* = MediaWiki JSON；其余当通用 HTML 抽链接。
    /// 默认给 Wikipedia（实测这台服务器上唯一能直接用的）。
    /// </summary>
    public string WebSearchSources { get; set; } =
        "wiki|https://zh.wikipedia.org/w/api.php?action=query&list=search&srsearch={q}&format=json&srlimit=5&utf8=1";

    /// <summary>每次给模型看几条搜索结果。</summary>
    public int WebSearchMaxResults { get; set; } = 5;

    /// <summary>
    /// 同一个会话两次联网搜索的最小间隔（秒）；0 = 不限。
    /// 为什么要有它：一次搜索 = 一次真实模型调用 + 几秒等待，群里连问几个问题就排队了。
    /// 为什么做成设置项：不同部署的网络快慢、群多少差很多，写死 30 秒众口难调。
    /// </summary>
    public int WebSearchCooldownSeconds { get; set; } = 30;

    /// <summary>搜索/读页面的超时（秒）。</summary>
    public int WebSearchTimeoutSeconds { get; set; } = 20;

    /// <summary>read 页面正文截断长度（字）。</summary>
    public int WebSearchReadMaxChars { get; set; } = 1800;

    // ---------- 听音乐（识别群里的音乐分享 + 网易云歌词 + 波形分析） ----------

    /// <summary>启用“听音乐”：有人分享歌时，自动查歌词、下一份低码率音频分析波形，再交模型接话。</summary>
    public bool EnableMusic { get; set; } = true;

    /// <summary>
    /// 音源模板（分号分隔，按顺序尝试）。占位符：{id} 歌曲 id、{br} 码率、{crc32} 标准 CRC32 的 8 位大写十六进制。
    /// 这类公开音源都是第三方服务，随时会挂、会改参数 —— 所以做成可配置：挂了就在面板里换一条，不用改代码。
    /// </summary>
    public string MusicSources { get; set; } =
        "meting|https://api.qijieya.cn/meting/?type=url&id={id}&br={br};" +
        "gdstudio|https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={id}&br={br}&s={crc32}";

    /// <summary>下载音频的码率（低码率足够分析波形，也省流量）。</summary>
    public int MusicBitrate { get; set; } = 128;

    /// <summary>
    /// 网易云接口地址（默认官方）。做成可配置是为了能指向自建代理或测试用的假接口；
    /// 服务器在海外时官方接口的音频部分会被地域限制，但歌词/详情没问题。
    /// </summary>
    public string NeteaseBaseUrl { get; set; } = "https://music.163.com";

    /// <summary>单个音频文件体积上限（MB），超过就跳过该音源。</summary>
    public int MusicMaxDownloadMb { get; set; } = 12;

    /// <summary>最多分析多少秒（超出部分截断，控制 CPU）。</summary>
    public int MusicMaxAnalysisSeconds { get; set; } = 180;

    /// <summary>“听过的歌”台账上限（条）。</summary>
    public int MusicLibraryMax { get; set; } = 300;

    /// <summary>分析结果保留天数：超过就重新分析一次（音源/算法可能变过）。</summary>
    public int MusicNoteTtlDays { get; set; } = 30;

    /// <summary>模型想听一首歌时的最小间隔（秒），防同一个话题反复搜歌；0 = 不限。</summary>
    public int MusicListenCooldownSeconds { get; set; } = 120;

    /// <summary>
    /// 专门用来“听”的音频识别模型（**留空 = 不听**，只用手写 DSP 的客观数据）。
    /// 为什么要单独配：很多网关/中转会把音频静默丢掉，主模型根本收不到声音 ——
    /// 这时配一个确实支持音频输入、而且该网关愿意转发的模型，才能“听”出曲风情绪。
    /// 默认留空：具体填哪个模型完全取决于你用的网关，不在代码里替用户指定。
    /// </summary>
    public string MusicUnderstandModel { get; set; } = string.Empty;

    /// <summary>是否把音频片段交给上面的音频识别模型（关掉 = 只用 DSP 实测数据）。</summary>
    public bool MusicSendAudioToModel { get; set; } = true;

    /// <summary>交给模型的音频最多多少 KB（默认 1MB ≈ 60 秒 128kbps）。</summary>
    public int MusicAudioToModelMaxKb { get; set; } = 1024;

    /// <summary>是否把下载的音频留在 data/music/audio（默认不留：服务器上不攒版权内容）。</summary>
    public bool MusicKeepAudio { get; set; }

    /// <summary>
    /// 网易云 Cookie（可选）：带上登录态能少踩一些接口限制。
    /// 属于密钥，环境变量专属（QQCHAT_NETEASE_COOKIE），不写入 settings.json、面板只显示是否已设置。
    /// </summary>
    [JsonIgnore]
    public string NeteaseCookie { get; set; } = string.Empty;

    // ---------- 运维 ----------

    /// <summary>面板访问令牌（可空）。设置后访问面板与 /api/* 需携带令牌；
    /// /healthz 与 /readyz 不受影响（留给容器健康检查）。
    /// 环境变量专属（QQCHAT_PANEL_TOKEN），不写入 settings.json。</summary>
    [JsonIgnore]
    public string PanelToken { get; set; } = string.Empty;

    /// <summary>健康检查 HTTP 端口（0=关闭）。用于容器 HEALTHCHECK。</summary>
    public int HealthPort { get; set; } = 8080;

    /// <summary>是否输出详细日志到 stdout。</summary>
    public bool VerboseLog { get; set; } = true;

    /// <summary>桌面版遗留字段（headless 不使用）。</summary>
    public string Theme { get; set; } = "Default";

    // ---------- 派生（不参与序列化，避免污染 settings.json） ----------

    /// <summary>规范化后的登录 QQ 号（空字符串按未配置处理）。</summary>
    [JsonIgnore]
    public string NormalizedUin => QuickLoginUin?.Trim() ?? string.Empty;

    /// <summary>登录 QQ 号；解析失败返回 0。</summary>
    [JsonIgnore]
    public long UinOrZero => long.TryParse(NormalizedUin, out var u) ? u : 0;

    // ---------- NapCat WebUI（面板内扫码登录） ----------
    // 只用于把登录二维码搬进机器人面板；不影响 OneBot 消息通道。

    /// <summary>NapCat WebUI 地址（容器网络里的服务名即可）。环境变量专属。</summary>
    [JsonIgnore]
    public string NapCatWebUiUrl { get; set; } = "http://napcat:6099";

    /// <summary>NapCat WebUI 令牌（napcat/config/webui.json 的 token 字段）。环境变量专属，不写入 settings.json。</summary>
    [JsonIgnore]
    public string NapCatWebUiToken { get; set; } = string.Empty;
}
