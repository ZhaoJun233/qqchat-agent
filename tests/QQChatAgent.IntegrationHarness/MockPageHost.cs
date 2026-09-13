using System.Net;
using System.Text;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// S23 用的假网页：给链接预览一个“真网页”去抓。
/// 返回带 og:title / meta description 的 HTML —— 正是真实站点会让机器人看到的东西。
/// </summary>
public sealed class MockPageHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private int _hits;

    public MockPageHost(int port, string title = "测试页面标题xyz", string description = "测试摘要内容abc")
    {
        _port = port;
        Title = title;
        Description = description;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public string Title { get; }

    public string Description { get; }

    /// <summary>被访问次数。</summary>
    public int Hits => Volatile.Read(ref _hits);

    public string PageUrl => $"http://127.0.0.1:{_port}/news/1";

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

            _ = Task.Run(async () =>
            {
                try
                {
                    Interlocked.Increment(ref _hits);
                    var html = $"""
                        <!doctype html><html><head><meta charset="utf-8">
                        <title>{Title} - 假站点</title>
                        <meta property="og:site_name" content="测试站点" />
                        <meta name="description" content="{Description}" />
                        </head><body><h1>{Title}</h1><p>{Description}</p></body></html>
                        """;
                    var body = Encoding.UTF8.GetBytes(html);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
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
            });
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
