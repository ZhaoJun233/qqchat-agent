using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace QQChatAgent.Services.OneBot;

/// <summary>
/// 反向 WebSocket：本程序作服务端（TcpListener），等协议端（NapCat 等的「反向 WS」）主动连入。
/// 按 RFC6455 手写握手与帧收发，不依赖第三方包。
/// </summary>
public sealed class ReverseWsTransport : IOneBotTransport
{
    private readonly IPAddress _ip;
    private readonly int _port;
    private readonly string? _token;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private WsConnection? _connection;
    private Task? _loopTask;

    public event Action<string>? OnText;
    public event Action<bool>? OnStateChanged;

    public bool IsConnected => _connection?.IsOpen == true;

    public ReverseWsTransport(string address, string? token)
    {
        var uri = new Uri(address);
        _ip = uri.Host is "localhost" or "0.0.0.0" or "*" ? IPAddress.Any : IPAddress.Parse(uri.Host);
        _port = uri.Port;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public Task StartAsync()
    {
        _listener = new TcpListener(_ip, _port);
        _listener.Start();

        if (_loopTask is null || _loopTask.IsCompleted)
        {
            _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        _cts.Cancel();
        try
        {
            _connection?.Close();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }

        OnStateChanged?.Invoke(false);
    }

    public void Dispose() => Stop();

    public async Task SendActionAsync(string action, string paramsJson, string echo, CancellationToken ct = default)
    {
        var connection = _connection ?? throw new InvalidOperationException("协议端尚未连接");
        var json = "{\"action\":" + Json(action) + ",\"params\":" + (paramsJson.Length == 0 ? "{}" : paramsJson) +
                   ",\"echo\":" + Json(echo) + "}";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        await connection.SendTextAsync(json, linked.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WsConnection? connection = null;
            try
            {
                var client = await _listener!.AcceptTcpClientAsync().WaitAsync(ct);
                connection = await WsConnection.AcceptAsync(client, _token, ct);
                connection.TextReceived += OnText;
                _connection = connection;
                OnStateChanged?.Invoke(true);
                await connection.RunAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 握手失败或连接中断：继续等待下一条连入
            }
            finally
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                    OnStateChanged?.Invoke(false);
                }
            }
        }
    }

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>最小 RFC6455 服务端连接：HTTP 升级握手 + 帧收发。</summary>
    private sealed class WsConnection
    {
        private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        /// <summary>收到的完整文本（由 AcceptLoop 桥接到外层事件）。</summary>
        public event Action<string>? TextReceived;

        public bool IsOpen { get; private set; } = true;

        private WsConnection(NetworkStream stream) => _stream = stream;

        public static async Task<WsConnection> AcceptAsync(TcpClient client, string? token, CancellationToken ct)
        {
            var stream = client.GetStream();
            stream.ReadTimeout = 10000;

            // 读取请求头直到空行
            var headerBytes = new List<byte>();
            var buffer = new byte[4096];
            while (true)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
                if (n <= 0)
                {
                    throw new EndOfStreamException("客户端关闭连接");
                }

                headerBytes.Add(buffer[0]);
                if (headerBytes.Count >= 4 &&
                    headerBytes[^4] == '\r' && headerBytes[^3] == '\n' &&
                    headerBytes[^2] == '\r' && headerBytes[^1] == '\n')
                {
                    break;
                }

                if (headerBytes.Count > 16 * 1024)
                {
                    throw new InvalidOperationException("请求头过大");
                }
            }

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            if (!string.Equals(headers.GetValueOrDefault("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("非 WebSocket 升级请求");
            }

            if (token is not null &&
                !string.Equals((headers.GetValueOrDefault("Authorization") ?? string.Empty).Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase), token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AccessToken 校验失败");
            }

            var key = headers.GetValueOrDefault("Sec-WebSocket-Key") ?? string.Empty;
            var acceptKey = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Magic)));

            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           "Sec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";
            var responseBytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBytes, ct);
            await stream.FlushAsync(ct);

            return new WsConnection(stream);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                while (IsOpen && !ct.IsCancellationRequested)
                {
                    var (closed, text) = await ReadFrameAsync(ct);
                    if (closed)
                    {
                        break;
                    }

                    if (text is not null)
                    {
                        TextReceived?.Invoke(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
            catch
            {
                // 连接异常断开
            }
            finally
            {
                IsOpen = false;
            }
        }

        public async Task SendTextAsync(string text, CancellationToken ct)
        {
            var payload = Encoding.UTF8.GetBytes(text);
            var header = new byte[10];
            int offset = 0;
            header[offset++] = 0x81; // FIN + text

            if (payload.Length <= 125)
            {
                header[offset++] = (byte)payload.Length;
            }
            else if (payload.Length <= 0xFFFF)
            {
                header[offset++] = 126;
                header[offset++] = (byte)(payload.Length >> 8);
                header[offset++] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                header[offset++] = 127;
                var len = (ulong)payload.Length;
                for (int i = 7; i >= 0; i--)
                {
                    header[offset++] = (byte)(len >> (i * 8));
                }
            }

            await _writeLock.WaitAsync(ct);
            try
            {
                await _stream.WriteAsync(header.AsMemory(0, offset), ct);
                await _stream.WriteAsync(payload, ct);
                await _stream.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>读取一帧。返回 (是否关闭连接, 聚合文本)。ping 自动回 pong。</summary>
        private async Task<(bool Closed, string? Text)> ReadFrameAsync(CancellationToken ct)
        {
            var header = new byte[2];
            await ReadExactAsync(header, ct);

            var opcode = header[0] & 0x0F;
            bool masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;

            if (length == 126)
            {
                var lenBytes = new byte[2];
                await ReadExactAsync(lenBytes, ct);
                length = (lenBytes[0] << 8) | lenBytes[1];
            }
            else if (length == 127)
            {
                var lenBytes = new byte[8];
                await ReadExactAsync(lenBytes, ct);
                length = 0;
                for (int i = 0; i < 8; i++)
                {
                    length = (length << 8) | lenBytes[i];
                }
            }

            byte[] mask = new byte[4];
            if (masked)
            {
                await ReadExactAsync(mask, ct);
            }

            var payload = new byte[length];
            await ReadExactAsync(payload, ct);
            if (masked)
            {
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] ^= mask[i & 3];
                }
            }

            switch (opcode)
            {
                case 0x8: // close
                    return (true, null);
                case 0x9: // ping -> pong（服务端帧不掩码）
                    await SendRawAsync(new byte[] { 0x8A, (byte)payload.Length }, payload, ct);
                    return (false, null);
                case 0xA: // pong
                    return (false, null);
                case 0x1: // text
                    return (false, Encoding.UTF8.GetString(payload));
                default:
                    return (false, null); // 忽略 continuation / binary
            }
        }

        private async Task SendRawAsync(byte[] header, byte[] payload, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await _stream.WriteAsync(header, ct);
                await _stream.WriteAsync(payload, ct);
                await _stream.FlushAsync(ct);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await _stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n <= 0)
                {
                    throw new EndOfStreamException("连接已关闭");
                }

                read += n;
            }
        }

        public void Close()
        {
            IsOpen = false;
            try
            {
                _stream.Dispose();
            }
            catch
            {
                // 忽略
            }
        }
    }
}