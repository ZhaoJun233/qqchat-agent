using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S26 用的假搜索后端：一个 HttpListener 同时演三种形状 ——
///   ① SearxNG 的 JSON（/searx?q=…）
///   ② MediaWiki 的 JSON（/w/api.php?action=query&list=search…）
///   ③ 一个普通网页（/page）—— 给模型的 read 字段抓正文用
///
/// 为什么要三种：搜索源是"name|url"模板，name 决定解析方式。
/// 只测一种的话，另外两条解析分支（SearxNG / MediaWiki / 通用 HTML）就没覆盖。
/// </summary>
public sealed class MockSearchHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private int _searxHits;
    private int _wikiHits;
    private int _pageHits;

    public MockSearchHost(int port, string bind = "127.0.0.1")
    {
        _port = port;
        _listener.Prefixes.Add($"http://{bind}:{port}/");
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    public int SearxHits => Volatile.Read(ref _searxHits);
    public int WikiHits => Volatile.Read(ref _wikiHits);
    public int PageHits => Volatile.Read(ref _pageHits);

    /// <summary>搜索源模板（SearxNG 形状）。</summary>
    public string SearxSource => $"searx-mock|{BaseUrl}/searx?q={{q}}";

    /// <summary>搜索源模板（MediaWiki 形状）。</summary>
    public string WikiSource => $"wiki-mock|{BaseUrl}/w/api.php?action=query&list=search&srsearch={{q}}&format=json";

    /// <summary>read 字段要读的页面地址。</summary>
    public string PageUrl => $"{BaseUrl}/page";

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            string body;
            string contentType = "application/json; charset=utf-8";

            if (path.StartsWith("/searx", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _searxHits);
                body = new JsonObject
                {
                    ["query"] = ctx.Request.QueryString["q"] ?? string.Empty,
                    ["results"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["title"] = "假 Searx 结果一：白子的声优",
                            ["url"] = "https://example.com/shiroko-voice",
                            ["content"] = "砂狼白子的日语配音是小仓唯，中文配音是安雪璃。"
                        },
                        new JsonObject
                        {
                            ["title"] = "假 Searx 结果二：阿拜多斯对策委员会",
                            ["url"] = "https://example.com/abydos",
                            ["content"] = "对策委员会的五名成员与她们的故事。"
                        }
                    }
                }.ToJsonString();
            }
            else if (path.StartsWith("/w/api.php", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _wikiHits);
                body = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["search"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["title"] = "碧蓝档案",
                                ["snippet"] = "《<span class=\"searchmatch\">碧蓝档案</span>》是一款由 Nexon 开发的手机游戏。"
                            }
                        }
                    }
                }.ToJsonString();
            }
            else if (path.StartsWith("/page", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _pageHits);
                contentType = "text/html; charset=utf-8";
                body = "<html><head><title>测试页面</title></head><body>" +
                       "<script>var x=1;</script><h1>砂狼白子</h1>" +
                       "<p>砂狼白子是《碧蓝档案》中阿拜多斯高中对策委员会的成员，喜欢运动和自行车。</p>" +
                       "<p>她的日语配音是小仓唯。</p></body></html>";
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception)
        {
            try
            {
                ctx.Response.Abort();
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
