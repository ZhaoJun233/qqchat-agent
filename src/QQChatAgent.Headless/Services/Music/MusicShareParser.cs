using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QQChatAgent.Services.Music;

/// <summary>
/// 音乐分享识别：把 OneBot 的 music 段 / json 卡片 / 文本里的分享链接统一成 <see cref="MusicShare"/>。
///
/// 为什么要三种都认：QQ 客户端分享歌曲时，协议端给的东西并不统一 ——
/// 老版是 music 段（带平台和 id），新版常常只给一个 json 卡片（网易云/QQ音乐的大小卡片），
/// 而用户手动粘链接时就是纯文本。只认其中一种会漏掉大半分享。
/// </summary>
public static partial class MusicShareParser
{
    [GeneratedRegex(@"(?:music\.163\.com|y\.music\.163\.com)/(?:#/)?(?:m/)?song[\?/]?(?:id=)?(?<id>\d{3,20})", RegexOptions.IgnoreCase)]
    private static partial Regex NeteaseSongRegex();

    [GeneratedRegex(@"https?://163cn\.tv/[\w\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex NeteaseShortRegex();

    [GeneratedRegex(@"https?://[^\s""<>]*music\.163\.com[^\s""<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex NeteaseAnyUrlRegex();

    [GeneratedRegex(@"https?://c6\.y\.qq\.com/[^\s""<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex QqShortRegex();

    /// <summary>把 OneBot 消息段里的音乐信息取出来（无则返回 null）。</summary>
    public static MusicShare? TryParseSegment(JsonNode? seg)
    {
        if (seg is not JsonObject obj || obj["data"] is not JsonObject data)
        {
            return null;
        }

        var type = obj["type"]?.GetValue<string>();
        switch (type)
        {
            case "music":
            {
                var platform = NormalizePlatform(data["type"]?.GetValue<string>());
                var id = ReadString(data["id"]);
                var title = ReadString(data["title"]);
                var artist = ReadString(data["content"]) ?? ReadString(data["singer"]);
                var audio = ReadString(data["audio"]);
                var page = ReadString(data["url"]) ?? ReadString(data["jumpUrl"]);
                return new MusicShare(platform, string.IsNullOrWhiteSpace(id) ? null : id,
                    title, artist, audio, page, "music段");
            }

            case "json":
            {
                // QQ 音乐/网易云的分享卡片：整段 JSON 塞在 data.data 里
                var raw = ReadString(data["data"]) ?? ReadString(data["json"]);
                return string.IsNullOrWhiteSpace(raw) ? null : TryParseCard(raw, "json卡片");
            }

            default:
                return null;
        }
    }

    /// <summary>读 JsonNode 里的字符串/数字（数字也当字符串用，歌曲 id 经常给成 number）。</summary>
    private static string? ReadString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            var text = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解析分享卡片 JSON（QQ 端那套 prompt/meta 结构，或者一个普通的 song 对象）。</summary>
    public static MusicShare? TryParseCard(string rawJson, string sourceLabel = "json卡片")
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            string? title = null, artist = null, jump = null, musicUrl = null, id = null;

            // 结构一：{"app":"…","prompt":"[分享]歌名","meta":{"music":{…}}} —— QQ 音乐小卡片
            if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in meta.EnumerateObject())
                {
                    var v = item.Value;
                    if (v.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    title ??= ReadString(v, "title");
                    artist ??= ReadString(v, "desc") ?? ReadString(v, "singer");
                    jump ??= ReadString(v, "jumpUrl") ?? ReadString(v, "url");
                    musicUrl ??= ReadString(v, "musicUrl") ?? ReadString(v, "audio");
                    id ??= ReadString(v, "id") ?? ReadString(v, "songid");
                }
            }

            // 结构二：网易云大卡片 {"app":"com.tencent.structmsg","meta":{"news":{…}}} 或直接就是歌曲对象
            title ??= ReadString(root, "title") ?? ReadString(root, "name");
            artist ??= ReadString(root, "desc") ?? ReadString(root, "artist") ?? ReadString(root, "singer");
            jump ??= ReadString(root, "jumpUrl") ?? ReadString(root, "url");
            musicUrl ??= ReadString(root, "musicUrl") ?? ReadString(root, "audio");
            id ??= ReadString(root, "id") ?? ReadString(root, "songid");

            if (root.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String && title is null)
            {
                title = prompt.GetString()?.Replace("[分享]", string.Empty).Trim();
            }

            // 从链接里抠出平台与歌曲 id
            var platform = "unknown";
            var pageUrl = jump;
            if (!string.IsNullOrWhiteSpace(jump))
            {
                var m = NeteaseSongRegex().Match(jump);
                if (m.Success)
                {
                    platform = "netease";
                    id ??= m.Groups["id"].Value;
                }
                else if (jump.Contains("y.qq.com", StringComparison.OrdinalIgnoreCase) ||
                         jump.Contains("i.y.qq.com", StringComparison.OrdinalIgnoreCase))
                {
                    platform = "qq";
                }
            }

            if (platform == "unknown" && !string.IsNullOrWhiteSpace(id) && NeteaseSongRegex().IsMatch(rawJson))
            {
                platform = "netease";
            }

            if (title is null && artist is null && id is null && pageUrl is null && musicUrl is null)
            {
                return null;
            }

            return new MusicShare(platform, id, title, artist, musicUrl, pageUrl, sourceLabel);
        }
        catch (JsonException)
        {
            // 卡片就是个坏 JSON —— 退化成“纯文本里找链接”
            return TryParseText(rawJson, sourceLabel);
        }
    }

    /// <summary>从纯文本里找分享链接（网易云长/短链、QQ 音乐短链）。</summary>
    public static MusicShare? TryParseText(string text, string sourceLabel = "文本链接")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = NeteaseSongRegex().Match(text);
        if (m.Success)
        {
            return new MusicShare("netease", m.Groups["id"].Value, PageUrl: NeteaseAnyUrlRegex().Match(text) is { Success: true } u ? u.Value : null, SourceLabel: sourceLabel);
        }

        var shortLink = NeteaseShortRegex().Match(text);
        if (shortLink.Success)
        {
            // 短链要跟一次跳转才知道是哪首歌 → 交给上层解析
            return new MusicShare("netease", null, PageUrl: shortLink.Value, SourceLabel: sourceLabel);
        }

        var qq = QqShortRegex().Match(text);
        if (qq.Success)
        {
            return new MusicShare("qq", null, PageUrl: qq.Value, SourceLabel: sourceLabel);
        }

        return null;
    }

    private static string NormalizePlatform(string? raw) => raw switch
    {
        "163" or "netease" or "cloudmusic" => "netease",
        "qq" or "qqmusic" => "qq",
        "kugou" => "kugou",
        "kuwo" => "kuwo",
        "migu" => "migu",
        null or "" => "unknown",
        _ => raw.ToLowerInvariant(),
    };

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }
}
