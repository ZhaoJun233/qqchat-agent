using QQChatAgent.Services.OneBot;

namespace QQChatAgent.Services.Qq;

/// <summary>
/// QQ 消息源统一抽象：上层（会话/Agent）只依赖此接口收发消息，
/// 不关心背后是内置账号登录（Lagrange.Core）还是外部 OneBot 协议端。
/// </summary>
public interface IQqChatSource
{
    /// <summary>收到一条 QQ 入站消息（在后台线程触发，上层负责回 UI 线程）。</summary>
    event Action<QqChatMessage>? MessageReceived;

    /// <summary>收到戳一戳（后台线程触发）。别人互戳也会报上来，是否回应由上层决定。</summary>
    event Action<QqPokeEvent>? Poked;

    /// <summary>连接状态变化（true=在线）。</summary>
    event Action<bool>? ConnectionChanged;

    bool IsConnected { get; }

    /// <summary>
    /// 发一张音乐分享卡片（OneBot 的 music 段）。
    /// platform=163 就是网易云：QQ 客户端会渲染成可点开播放的音乐卡片。
    /// 默认实现返回 false —— 不是每个协议端都支持，上层要能优雅降级。
    /// </summary>
    Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>向群聊/私聊发送纯文本。成功返回 true。replyToMessageId 用于触发 QQ 的"回复"引用。</summary>
    Task<bool> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null);

    /// <summary>
    /// 发一条语音（OneBot 的 record 段）。
    /// audioUrl 指向一个**协议端自己能访问**的音频地址（如 TTS 旁路容器的 /speak?text=…）：
    /// 机器人不下载音频，把 URL 交给协议端去下载、转 silk、上传 —— 这样这边就不用碰 silk 编码。
    /// 默认实现返回 false —— 不是每个协议端都支持，上层要能优雅降级成发文字。
    /// </summary>
    Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>发送一张图片（表情包）。data 为图片原始字节，协议端用 base64:// 形式接收。</summary>
    Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null);

    /// <summary>
    /// 戳一戳某人（群聊走 group_poke，私聊走 friend_poke）。
    /// 默认实现返回 false —— 不是每个协议端都支持，上层要能优雅降级。
    /// </summary>
    Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>获取群名称（用于会话列表）。拿不到时返回 null。</summary>
    Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default);

    /// <summary>
    /// 拉取登录账号在 QQ 里的“收藏表情”图片地址（协议端支持时）。
    /// 默认实现返回空列表 —— 有的协议端没有这个扩展动作，上层要能优雅降级。
    /// </summary>
    Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
        => Task.FromResult(new List<string>());
}