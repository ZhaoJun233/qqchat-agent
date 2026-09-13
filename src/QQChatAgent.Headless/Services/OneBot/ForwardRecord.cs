using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.Services.OneBot;

/// <summary>
/// 合并转发（聊天记录）的渲染。
///
/// 群友经常把一段对话合并转发过来，协议端只给一个 forward id —— 内容要另发一次
/// <c>get_forward_msg</c> 去取。取回来是一堆节点（谁说的 + 说了什么），
/// 这里把它压成一段紧凑的文字塞进消息正文，模型才有东西可读。
///
/// 上限是有意的：一条转发记录可能有几百条，全塞进去既费 token 又淹没当前话题 ——
/// 只展开前 N 行，剩下的用“还有 K 条”交代清楚。
/// </summary>
public static class ForwardRecord
{
    /// <summary>把 get_forward_msg 的 data 渲染成消息正文（取不到内容时返回 null）。</summary>
    public static string? Render(JsonNode? data, int maxLines, int maxChars)
    {
        var messages = data?["messages"] as JsonArray;
        if (messages is null || messages.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("[合并转发的聊天记录 ").Append(messages.Count).Append(" 条]\n");

        var lines = 0;
        var rendered = 0;
        foreach (var node in messages)
        {
            if (node is null || lines >= maxLines || sb.Length >= maxChars)
            {
                break;
            }

            var who = Nickname(node);
            var what = NodeText(node);
            if (what.Length == 0)
            {
                continue;
            }

            var line = $"{who}: {what}";
            if (line.Length > 200)
            {
                line = line[..200] + "…";
            }

            sb.Append(line).Append('\n');
            lines++;
            rendered++;
        }

        var omitted = messages.Count - rendered;
        if (omitted > 0)
        {
            sb.Append("（还有 ").Append(omitted).Append(" 条未展开）");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>取节点里的说话人名字。</summary>
    private static string Nickname(JsonNode node)
    {
        var name = node["sender"]?["nickname"]?.GetValue<string>()
            ?? node["nickname"]?.GetValue<string>()
            ?? node["sender"]?["card"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name!.Trim();
        }

        var id = node["user_id"]?.GetValue<long>() ?? 0;
        return id > 0 ? $"成员{id}" : "某人";
    }

    /// <summary>把节点正文（可能是段数组、也可能是已经渲染好的字符串）转成纯文本。</summary>
    private static string NodeText(JsonNode node)
    {
        var message = node["message"];
        if (message is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return Collapse(text);
        }

        if (message is JsonArray segments)
        {
            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                sb.Append(SegmentText(seg));
            }

            return Collapse(sb.ToString());
        }

        // 少数实现直接给 content
        return Collapse(node["content"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>消息段 → 可读文本（与网关里那套同口径，但只做最必要的几种）。</summary>
    public static string SegmentText(JsonNode? seg)
    {
        if (seg is null)
        {
            return string.Empty;
        }

        var data = seg["data"];
        return seg["type"]?.GetValue<string>() switch
        {
            "text" => data?["text"]?.GetValue<string>() ?? string.Empty,
            "at" => data?["qq"]?.GetValue<string>() is string qq ? (qq == "all" ? "@全体成员 " : $"@{qq} ") : "@某人 ",
            "image" => "[图片]",
            "face" => "[表情]",
            "mface" => "[动画表情]",
            "sface" or "bface" => "[超级表情]",
            "record" => "[语音]",
            "video" => "[视频]",
            "file" => data?["name"]?.GetValue<string>() is string f ? $"[文件:{f}]" : "[文件]",
            "music" => "[音乐分享]",
            "forward" => "[合并转发]",
            "json" => "[分享卡片]",
            "dice" => "[骰子]",
            "rps" => "[猜拳]",
            "poke" => "[戳一戳]",
            "reply" => string.Empty,
            _ => string.Empty,
        };
    }

    private static string Collapse(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
}
