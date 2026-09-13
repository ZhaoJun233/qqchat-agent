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
