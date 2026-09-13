namespace QQChatAgent.Models;

/// <summary>一条会话消息（headless 版：去掉所有 UI 属性，仅保留数据）。</summary>
public sealed class ChatMessage
{
    public required MessageRole Role { get; init; }

    public required string Text { get; init; }

    /// <summary>会话内自增序号（持久化不保留，仅在进程内用于前端增量同步）。</summary>
    public long Seq { get; internal set; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string? SenderName { get; init; }

    /// <summary>发送者 QQ 号（群成员/好友），用于建立人物档案。</summary>
    public long? SenderId { get; init; }

    /// <summary>消息中的图片 URL（供模型识图）。</summary>
    public IReadOnlyList<string>? ImageUrls { get; init; }

    /// <summary>QQ 原始消息 ID（用于历史去重）。</summary>
    public long? QqMessageId { get; init; }

    /// <summary>
    /// 这条消息后来被撤回了（OneBot 的 group_recall / friend_recall 事件）。
    /// 为什么要记：撤回后群友就看不到内容了，但机器人手里还有 —— 不标记的话它下一轮会
    /// 引用一句“已经不存在的消息”，或者把撤回前看到的内容当成公共信息继续用。
    /// 标记之后：送给模型的正文变成 <c>[已撤回]</c>（它知道“这里有过一条、被撤了”），
    /// 同时不能再被选作回复引用目标。
    /// 原始正文仍留在记录里（面板/日志是给运维看的），但**永远不会**再发给模型。
    /// </summary>
    public bool Recalled { get; set; }

    /// <summary>时间短格式：当天 HH:mm，跨天 MM-dd HH:mm。</summary>
    public string TimeText
    {
        get
        {
            var local = Timestamp.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("MM-dd HH:mm");
        }
    }
}
