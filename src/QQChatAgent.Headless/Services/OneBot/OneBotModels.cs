using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQChatAgent.Services.OneBot;

/// <summary>OneBot v11 消息事件（post_type=message）。</summary>
public sealed class OneBotEventMessage
{
    [JsonPropertyName("post_type")] public string? PostType { get; set; }
    [JsonPropertyName("message_type")] public string? MessageType { get; set; }
    [JsonPropertyName("sub_type")] public string? SubType { get; set; }
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("message")] public JsonElement Message { get; set; }
    [JsonPropertyName("raw_message")] public string? RawMessage { get; set; }
    [JsonPropertyName("self_id")] public long SelfId { get; set; }
    [JsonPropertyName("sender")] public OneBotSender? Sender { get; set; }
    [JsonPropertyName("time")] public long Time { get; set; }
}

public sealed class OneBotSender
{
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("card")] public string? Card { get; set; }
    [JsonPropertyName("role")] public string? Role { get; set; }
}

public sealed class GroupInfo
{
    [JsonPropertyName("group_id")] public long GroupId { get; set; }
    [JsonPropertyName("group_name")] public string? GroupName { get; set; }
}

/// <summary>统一后的 QQ 入站消息（与传输层无关）。</summary>
/// <param name="MentionedSelf">群消息中是否 @ 了机器人自己。</param>
public sealed record QqChatMessage(
    long MessageId,
    bool IsGroup,
    long UserId,
    long GroupId,
    string SenderName,
    string Text,
    DateTimeOffset Time,
    bool MentionedSelf,
    IReadOnlyList<string>? ImageUrls = null,
    IReadOnlyList<QQChatAgent.Services.Music.MusicShare>? MusicShares = null);

/// <summary>
/// 戳一戳事件（OneBot v11：post_type=notice）。
/// NapCat/go-cqhttp 都有两种写法（notice_type=notice+sub_type=poke 或 notice_type=poke），解析时两种都收。
/// </summary>
/// <param name="UserId">发起戳的人。</param>
/// <param name="TargetId">被戳的人；<paramref name="IsSelfPoked"/> 为 true 时就是机器人自己。</param>
public sealed record QqPokeEvent(
    bool IsGroup,
    long GroupId,
    long UserId,
    long TargetId,
    bool IsSelfPoked,
    DateTimeOffset Time);

public static class OneBotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}