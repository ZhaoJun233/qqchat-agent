using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QQChatAgent.Services.Net;

/// <summary>HTML → 文本的小工具（链接预览与网页正文提取共用）。</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<script[\s\S]*?</script>|<style[\s\S]*?</style>|<!--[\s\S]*?-->", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"charset\s*=\s*[""']?\s*(?<c>[\w\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();

    /// <summary>
    /// 按页面自己声明的编码解码（声明了但系统没有该 codepage 时退回 UTF-8 —— 现代站点基本都是 UTF-8）。
    /// </summary>
    public static string Decode(byte[] body)
    {
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 2048));
        var charset = CharsetRegex().Match(head).Groups["c"].Value;
        var encoding = Encoding.UTF8;
        if (charset.Length > 0 &&
            !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) &&
            !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (Exception)
            {
                encoding = Encoding.UTF8;
            }
        }

        return encoding.GetString(body);
    }

    /// <summary>把 HTML 压成纯文本（去脚本/样式/注释/标签，压缩空白）。</summary>
    public static string ToPlainText(string html)
    {
        var text = NoiseRegex().Replace(html, " ");
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>压空白 + 截断（超长加省略号）。</summary>
    public static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
