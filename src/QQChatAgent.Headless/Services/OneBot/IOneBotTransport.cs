namespace QQChatAgent.Services.OneBot;

/// <summary>
/// OneBot v11 传输层抽象：
/// 正向 WS = 本程序作客户端连接协议端；
/// 反向 WS = 本程序作服务端等协议端连入；
/// HTTP   = 动作走 HTTP POST，事件走 get_latest_events 轮询。
/// </summary>
public interface IOneBotTransport : IDisposable
{
    /// <summary>收到一段完整 JSON（事件或动作响应），在传输线程触发。</summary>
    event Action<string>? OnText;

    /// <summary>连接状态变化（true=已连通协议端）。</summary>
    event Action<bool>? OnStateChanged;

    bool IsConnected { get; }

    /// <summary>启动连接/监听；失败与断线后的重连由实现自行负责。</summary>
    Task StartAsync();

    /// <summary>发送一个动作。WS 实现会包成 {action,params,echo}，HTTP 实现 POST {base}/{action}。</summary>
    Task SendActionAsync(string action, string paramsJson, string echo, CancellationToken ct = default);

    void Stop();
}