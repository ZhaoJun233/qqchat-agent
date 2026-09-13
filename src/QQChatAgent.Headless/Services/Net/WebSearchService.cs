using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QQChatAgent.Services.Net;

/// <summary>一条搜索结果。</summary>
public sealed record WebSearchHit(string Title, string Url, string Snippet);

/// <summary>一次联网搜索的结果：答案（如果有）+ 若干来源。</summary>
public sealed record WebSearchResult(
    string Query,
    string? Answer,
    IReadOnlyList<WebSearchHit> Hits,
    string Provider,
    string? Error = null)
{
    public bool HasContent => !string.IsNullOrWhiteSpace(Answer) || Hits.Count > 0;

    /// <summary>拼成给模型看的一段事实（搜不到时如实说明，别让它瞎编）。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("搜索「").Append(Query).Append("」").Append("（").Append(Provider).Append("）：\n");

        if (!string.IsNullOrWhiteSpace(Answer))
        {
            sb.Append(Answer!.Trim()).Append('\n');
        }

        if (Hits.Count > 0)
        {
            sb.Append("来源：\n");
            foreach (var hit in Hits.Take(6))
            {
                sb.Append("- ").Append(hit.Title);
                if (!string.IsNullOrWhiteSpace(hit.Snippet))
                {
                    sb.Append("：").Append(HtmlText.Clip(hit.Snippet, 160));
                }

                if (!string.IsNullOrWhiteSpace(hit.Url))
                {
                    sb.Append("  ").Append(hit.Url);
                }

                sb.Append('\n');
            }
        }

        if (!HasContent)
        {
            sb.Append("（没有搜到可用内容。").Append(Error ?? "上游没有返回结果").Append("）");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 联网搜索服务。两条路，按顺序试：
///
/// **① 让模型自己搜（首选）** —— 走 OpenAI 兼容网关背后的 Gemini 原生端点，
/// 带上 <c>tools:[{google_search:{}}]</c>：Google 真去搜，答案带 <c>groundingMetadata</c> 来源。
/// 为什么首选它：很多部署环境的出口是机房 IP，Google/Bing/DuckDuckGo/百度 对爬虫一律回
/// 看板页或验证码（实测：八家公开 SearxNG 实例也全部关了 JSON），而模型订阅本来就能搜 ——
/// 不额外要密钥、不爬虫、质量还更好。
///
/// **② 音源式的可插拔模板（兜底）** —— <c>name|url</c> 模板列表，<c>{q}</c> 是查询词。
/// 支持 SearxNG(JSON) / MediaWiki(JSON) / 通用 HTML 三种形状。
/// 机房 IP 被搜索引擎拦掉时，至少可以指向自建 SearxNG 或公司内部检索。
///
/// 另外还有 <see cref="ReadPageAsync"/>：把某个 URL 的正文抓成纯文本（模型可以主动要求“读一下这个页面”）。
/// </summary>
public sealed partial class WebSearchService
{
    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string> _log;

    [GeneratedRegex(@"<a\s+[^>]*href\s*=\s*""(?<u>[^""]+)""[^>]*>(?<t>[\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    public WebSearchService(HttpClient http, Func<AppSettings> settings, Action<string> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    /// <summary>搜索：先试模型自带搜索，再用模板源兜底。</summary>
    public async Task<WebSearchResult> SearchAsync(string query, CancellationToken ct)
    {
        var settings = _settings();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.WebSearchTimeoutSeconds, 5, 60));

        string? groundingError = null;
        if (settings.WebSearchUseModelSearch)
        {
            var (result, error) = await SearchViaModelAsync(query, timeout, ct);
            if (result is not null)
            {
                return result;
            }

            groundingError = error;
            _log($"[Search] 模型自带搜索不可用（{error}），改用配置的搜索源");
        }

        var (fromSources, sourceError) = await SearchViaSourcesAsync(query, timeout, ct);
        if (fromSources is not null)
        {
            return fromSources;
        }

        return new WebSearchResult(query, null, Array.Empty<WebSearchHit>(), "无",
            groundingError is null ? sourceError : $"模型搜索：{groundingError}；搜索源：{sourceError}");
    }

    /// <summary>① 走模型端的原生 Gemini 端点 + google_search 工具（真·Google 搜索，带来源）。</summary>
    private async Task<(WebSearchResult? Result, string? Error)> SearchViaModelAsync(
        string query, TimeSpan timeout, CancellationToken ct)
    {
        var settings = _settings();
        var uri = BuildGroundingUri(settings);
        if (uri is null)
        {
            return (null, "模型地址/模型名没配好，无法推导原生搜索端点");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var payload = new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["text"] = "你在为另一个聊天机器人做联网检索。请用中文简明回答下面的问题，" +
                                           "只讲事实、不要寒暄、不要编造；不确定就说不确定。问题：" + query
                            }
                        }
                    }
                },
                ["tools"] = new JsonArray { new JsonObject { ["google_search"] = new JsonObject() } },
                ["generationConfig"] = new JsonObject
                {
                    ["temperature"] = 0.2,
                    ["maxOutputTokens"] = 900
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ApiKey.Trim()}");

            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)resp.StatusCode}：{HtmlText.Clip(body, 160)}");
            }

            var root = JsonNode.Parse(body);
            var candidate = root?["candidates"]?[0];
            var answer = ReadGroundedText(candidate);
            var hits = ReadGroundingChunks(candidate).ToList();
            var queries = candidate?["groundingMetadata"]?["webSearchQueries"]?.AsArray()
                .Select(q => q?.GetValue<string>())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .ToList() ?? new List<string?>();

            if (string.IsNullOrWhiteSpace(answer) && hits.Count == 0)
            {
                return (null, "模型没有返回可用内容（可能这个上游不支持 google_search 工具）");
            }

            _log($"[Search] 模型搜索完成：「{query}」→ {hits.Count} 个来源" +
                 (queries.Count > 0 ? $"，实际检索词 {string.Join("/", queries)}" : string.Empty));

            return (new WebSearchResult(query, answer, hits, "模型联网搜索"), null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>拼接 candidates[0].content.parts 里所有 text（Gemini 3 会附带 thoughtSignature，跳过非文本）。</summary>
    private static string? ReadGroundedText(JsonNode? candidate)
    {
        var parts = candidate?["content"]?["parts"]?.AsArray();
        if (parts is null)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            var text = part?["text"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(text.Trim()).Append('\n');
            }
        }

        var joined = sb.ToString().Trim();
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>groundingChunks[].web 里的来源（title + uri）。uri 多是 Google 的中转长链，
    /// 只留标题给模型（省 token，也不让它去引用一条自己访问不了的链），完整 URI 记到运维日志。</summary>
    private IEnumerable<WebSearchHit> ReadGroundingChunks(JsonNode? candidate)
    {
        var chunks = candidate?["groundingMetadata"]?["groundingChunks"]?.AsArray();
        if (chunks is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            var web = chunk?["web"];
            var title = web?["title"]?.GetValue<string>();
            var uri = web?["uri"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) || !seen.Add(title!))
            {
                continue;
            }

            if (uri.Length > 0)
            {
                _log($"[Search] 来源：{title} → {HtmlText.Clip(uri, 120)}");
            }

            yield return new WebSearchHit(title!, string.Empty, string.Empty);
        }
    }

    /// <summary>从 OpenAI 兼容的 Base URL 推导 Gemini 原生 generateContent 地址。</summary>
    private static Uri? BuildGroundingUri(AppSettings settings)
    {
        var baseUrl = settings.ModelBaseUrl?.Trim();
        var model = settings.Model?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return null;
        }

        var trimmed = baseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }

        return Uri.TryCreate($"{trimmed}/v1beta/models/{Uri.EscapeDataString(model)}:generateContent",
            UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>② 模板源兜底（SearxNG JSON / MediaWiki JSON / 通用 HTML）。</summary>
    private async Task<(WebSearchResult? Result, string? Error)> SearchViaSourcesAsync(
        string query, TimeSpan timeout, CancellationToken ct)
    {
        var sources = _settings().WebSearchSources
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .ToList();

        if (sources.Count == 0)
        {
            return (null, "没有配置搜索源");
        }

        var lastError = "没有可用的搜索源";
        foreach (var line in sources)
        {
            var sep = line.IndexOf('|');
            if (sep <= 0 || sep == line.Length - 1)
            {
                continue;
            }

            var name = line[..sep].Trim();
            var template = line[(sep + 1)..].Trim();
            var url = template.Replace("{q}", Uri.EscapeDataString(query), StringComparison.OrdinalIgnoreCase);

            if (!SafeUrl.TryValidate(url, AllowPrivateForTests(), out var uri, out var why))
            {
                lastError = $"{name}：{why}";
                _log($"[Search] 跳过搜索源 {name}（{why}）");
                continue;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/122 Safari/537.36");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

                using var resp = await _http.SendAsync(req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    lastError = $"{name} 返回 HTTP {(int)resp.StatusCode}";
                    _log($"[Search] {lastError}");
                    continue;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                var text = HtmlText.Decode(bytes);
                var hits = ParseSource(name, text, uri, _settings().WebSearchMaxResults, out var parseError);
                if (hits.Count == 0)
                {
                    // 带上原因与响应片段：搜索源被反爬拦时会回一个“看板页”，
                    // 不把这东西记下来，排查时只能靠猜（踩过）。
                    lastError = $"{name} 没有解析出结果（{parseError}）";
                    _log($"[Search] {lastError}：响应前 200 字：{HtmlText.Clip(Regex.Replace(text, @"\s+", " "), 200)}");
                    continue;
                }

                _log($"[Search] {name} 搜到 {hits.Count} 条：「{query}」");
                return (new WebSearchResult(query, null, hits, name), null);
            }
            catch (Exception ex)
            {
                lastError = $"{name}：{ex.GetType().Name} {ex.Message}";
                _log($"[Search] {lastError}");
            }
        }

        return (null, lastError);
    }

    /// <summary>
    /// 按源名分派解析：searx*（SearxNG JSON）/ wiki*（MediaWiki JSON）/ 其它当通用 HTML。
    ///
    /// 名字不认时才看形状 —— 注意 **SearxNG 的响应里也有一个 query 字段**，
    /// 光用 “有没有 query” 判 MediaWiki 会把 Searx 误判成 wiki（踩过：结果永远为空）。
    /// </summary>
    internal static List<WebSearchHit> ParseSource(string name, string body, Uri uri, int max)
        => ParseSource(name, body, uri, max, out _);

    /// <summary>同上，但把“为什么没解析出来”带出来（排障用：JSON 坏了还是形状不对）。</summary>
    internal static List<WebSearchHit> ParseSource(string name, string body, Uri uri, int max, out string? parseError)
    {
        parseError = null;
        var limit = Math.Clamp(max, 1, 10);
        var looksJson = body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[');
        if (name.StartsWith("searx", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("wiki", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("json", StringComparison.OrdinalIgnoreCase) ||
            looksJson)
        {
            try
            {
                var root = JsonNode.Parse(body);
                // ⚠ 形状探测必须用 is 判定：JsonNode 对**非对象**节点取下标会抛
                // InvalidOperationException（SearxNG 的 "query" 是个字符串，踩过）。
                var isWiki = name.StartsWith("wiki", StringComparison.OrdinalIgnoreCase) ||
                             (root is JsonObject obj && obj["query"] is JsonObject q && q["search"] is JsonArray);
                return isWiki ? ParseMediaWiki(root, uri, limit) : ParseSearx(root, limit);
            }
            catch (Exception ex)
            {
                // 不是 JSON（被拦了会回 HTML，也可能是形状变了）→ 走通用 HTML，并把原因记下来
                parseError = $"JSON 解析失败（{ex.GetType().Name}: {ex.Message}）";
            }
        }

        var hits = ParseGenericHtml(body, uri, limit);
        if (hits.Count == 0 && parseError is null)
        {
            parseError = looksJson ? "JSON 里没有找到结果字段（形状与预期不符）" : "HTML 里没抽到链接（多半是被反爬拦了）";
        }

        return hits;
    }

    private static List<WebSearchHit> ParseSearx(JsonNode? root, int limit)
    {
        var hits = new List<WebSearchHit>();
        if (root is not JsonObject obj || obj["results"] is not JsonArray results)
        {
            return hits;
        }

        foreach (var item in results)
        {
            if (item is not JsonObject hit)
            {
                continue;
            }

            var url = ReadString(hit, "url");
            var title = ReadString(hit, "title");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            hits.Add(new WebSearchHit(HtmlText.Clip(title, 120), url,
                HtmlText.Clip(ReadString(hit, "content") ?? string.Empty, 200)));
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    private static List<WebSearchHit> ParseMediaWiki(JsonNode? root, Uri uri, int limit)
    {
        var hits = new List<WebSearchHit>();
        if (root is not JsonObject obj || obj["query"] is not JsonObject query || query["search"] is not JsonArray results)
        {
            return hits;
        }

        foreach (var item in results)
        {
            if (item is not JsonObject hit)
            {
                continue;
            }

            var title = ReadString(hit, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var snippet = HtmlText.ToPlainText(ReadString(hit, "snippet") ?? string.Empty);
            var url = $"{uri.Scheme}://{uri.Authority}/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";
            hits.Add(new WebSearchHit(title, url, HtmlText.Clip(snippet, 200)));
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    /// <summary>安全取字符串字段（不是字符串就当没有 —— 不同源的字段类型不一致）。</summary>
    private static string? ReadString(JsonObject obj, string name)
        => obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<WebSearchHit> ParseGenericHtml(string body, Uri uri, int limit)
    {
        var hits = new List<WebSearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in LinkRegex().Matches(body))
        {
            var href = WebUtility.HtmlDecode(m.Groups["u"].Value);
            var title = HtmlText.ToPlainText(m.Groups["t"].Value);
            if (title.Length < 4 || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(uri, href, out var abs))
            {
                continue;
            }

            if (!seen.Add(abs.ToString()))
            {
                continue;
            }

            hits.Add(new WebSearchHit(HtmlText.Clip(title, 120), abs.ToString(), string.Empty));
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    /// <summary>
    /// 读一个网页的正文（模型填 <c>read</c> 时用）。返回压好的纯文本；失败返回 (null, 原因)。
    /// </summary>
    public async Task<(string? Text, string? Error)> ReadPageAsync(string url, CancellationToken ct)
    {
        if (!SafeUrl.TryValidate(url, AllowPrivateForTests(), out var uri, out var why))
        {
            return (null, why);
        }

        var settings = _settings();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.WebSearchTimeoutSeconds, 5, 60));
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/122 Safari/537.36");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)resp.StatusCode}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[32 * 1024];
            while (buffer.Length < 512 * 1024)
            {
                var read = await stream.ReadAsync(chunk, cts.Token);
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            var text = HtmlText.ToPlainText(HtmlText.Decode(buffer.ToArray()));
            if (text.Length < 40)
            {
                return (null, "页面正文太短（可能是纯 JS 站点）");
            }

            var max = Math.Clamp(settings.WebSearchReadMaxChars, 300, 6000);
            _log($"[Search] 读了页面 {HtmlText.Clip(uri.ToString(), 80)}（{text.Length} 字 → 截到 {max}）");
            return (HtmlText.Clip(text, max), null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>是否允许抓内网/回环地址（自建 SearxNG 时需要；与图片、链接预览共用同一个开关）。</summary>
    private static bool AllowPrivateForTests()
        => Environment.GetEnvironmentVariable("QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS") == "1";
}
