using System.Text.RegularExpressions;

namespace QQChatAgent.Services.Links;

/// <summary>
/// 从文本里抠出链接。
///
/// 为什么要单独做：群友发链接时，模型只看到一串字符 —— 不知道那是个新闻、视频还是广告；
/// 而链接往往又是“这条消息到底在说什么”的关键。抠出来之后可以去取标题/摘要（见 <see cref="LinkPreviewer"/>），
/// 也能在提示词里把链接标清楚。
/// </summary>
public static partial class LinkExtractor
{
    [GeneratedRegex(@"https?://[^\s<>""'\u3000]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>句末标点与括号：链接后面紧跟中文标点时，它们不属于链接。</summary>
    private static readonly char[] Trailing = ['。', '，', '、', '；', '：', '！', '？', '）', '】', '》', '」', '”', '’', ')', ']', '>', ',', '.', ';', ':', '!', '?', '\'', '"'];

    /// <summary>提取文本里的链接（去重、去尾部标点、最多 max 个）。</summary>
    public static IReadOnlyList<string> Extract(string? text, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(text) || max <= 0)
        {
            return [];
        }

        var found = new List<string>();
        foreach (Match m in UrlRegex().Matches(text))
        {
            var url = m.Value.TrimEnd(Trailing);
            if (url.Length < 12 || !url.Contains("//"))
            {
                continue; // 形如 http://a 的碎片不要
            }

            if (!found.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(url);
            }

            if (found.Count >= max)
            {
                break;
            }
        }

        return found;
    }
}
