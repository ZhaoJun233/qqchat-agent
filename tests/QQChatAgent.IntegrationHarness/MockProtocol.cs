using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// 假 OneBot v11 协议端。
///   <see cref="ConnectReverseAsync"/>：反向 WS 用 —— 本测试主动连到机器人的 WS 服务端。
///   <see cref="StartForwardServerAsync"/>：正向 WS 用 —— 本测试开 WS 服务端，等机器人连入。
/// 收到的动作会入队 <see cref="ActionsReceived"/>，并自动回带 echo 的成功响应。
/// </summary>
public sealed class MockProtocol : IDisposable
{
    private readonly List<JsonObject> _actions = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _client;
    private HttpListener? _forwardListener;
    private WebSocket? _serverSocket;

    public long SelfId { get; init; } = 10001;

    public string? AccessToken { get; init; }

    /// <summary>get_group_info 返回的群名（可自定义，用于验证同名群不串味）。</summary>
    public string GroupName { get; set; } = "测试群";

    /// <summary>
    /// get_status 报告的账号在线状态。
    /// 用于模拟“WS 连着但账号被顶下线”这个最难排查的故障：
    /// 连接完全正常、get_login_info 照旧回显 UIN，但消息一条都收不到。
    /// </summary>
    public bool AccountOnline { get; set; } = true;

    /// <summary>get_group_msg_history 要返回的历史消息（按**最新在前**排列，与 NapCat 行为一致）。</summary>
    public List<(long MessageId, long UserId, string Sender, string Text)> GroupHistory { get; } = new();

    /// <summary>构建 get_forward_msg 的响应（未登记的 id 返回空 nodes）。</summary>
    private JsonObject BuildForwardRecord(string? id)
    {
        if (id is not null)
        {
            Interlocked.Increment(ref _forwardFetches);
        }

        var messages = id is not null && ForwardRecords.TryGetValue(id, out var nodes) ? nodes : new JsonArray();
        Console.WriteLine($"      [mock] get_forward_msg id={id ?? "(null)"} keys={string.Join(",", ForwardRecords.Keys)} → {messages.Count} 条");
        return new JsonObject { ["messages"] = messages.DeepClone() };
    }

    /// <summary>构建 get_group_msg_history 的响应。</summary>
    private JsonObject BuildHistory()
    {
        var arr = new JsonArray();
        var baseTime = DateTimeOffset.Now.AddMinutes(-10);

        for (var i = 0; i < GroupHistory.Count; i++)
        {
            var item = GroupHistory[i];
            arr.Add(new JsonObject
            {
                ["message_id"] = item.MessageId,
                ["user_id"] = item.UserId,
                // 列表下标 0 = 最新（与 NapCat “最新在前”一致）→ 时间递减
                ["time"] = baseTime.AddSeconds(-i).ToUnixTimeSeconds(),
                ["sender"] = new JsonObject { ["nickname"] = item.Sender, ["card"] = "" },
                ["message"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["data"] = new JsonObject { ["text"] = item.Text }
                    }
                }
            });
        }

        return new JsonObject { ["messages"] = arr };
    }

    /// <summary>收到的动作（action 字段）。</summary>
    public IReadOnlyList<JsonObject> ActionsReceived
    {
        get
        {
            lock (_gate)
            {
                return _actions.ToArray();
            }
        }
    }

    public bool IsConnected => _client?.State == WebSocketState.Open || _serverSocket?.State == WebSocketState.Open;

    // ---------- 反向 WS：测试端作为客户端 ----------

    public async Task ConnectReverseAsync(string url, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        if (AccessToken is not null)
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + AccessToken);
        }

        await socket.ConnectAsync(new Uri(url), ct);
        _client = socket;
        _ = Task.Run(() => ReceiveLoopAsync(socket, ct), ct);
    }

    // ---------- 正向 WS：测试端作为服务端 ----------

    public Task StartForwardServerAsync(int port, CancellationToken ct, string bind = "127.0.0.1")
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{bind}:{port}/");
        listener.Start();
        _forwardListener = listener;
        _ = Task.Run(() => ForwardAcceptLoopAsync(listener, ct), ct);
        return Task.CompletedTask;
    }

    private async Task ForwardAcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            if (AccessToken is not null)
            {
                var auth = context.Request.Headers["Authorization"] ?? string.Empty;
                if (!auth.Replace("Bearer ", string.Empty).Trim().Equals(AccessToken, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    continue;
                }
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            _serverSocket = wsContext.WebSocket;
            _ = Task.Run(() => ReceiveLoopAsync(_serverSocket, ct), ct);
        }
    }

    // ---------- 收发 ----------

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                return;
            }

            JsonObject? root;
            try
            {
                root = JsonNode.Parse(sb.ToString()) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (root is null)
            {
                continue;
            }

            var action = root["action"]?.GetValue<string>();
            var echo = root["echo"]?.GetValue<string>();
            if (action is null)
            {
                continue;
            }

            lock (_gate)
            {
                _actions.Add(root);
            }

            // 回动作响应（机器人依 echo 匹配）
            JsonNode? data = action switch
            {
                "get_login_info" => new JsonObject { ["user_id"] = SelfId, ["nickname"] = "测试机器人" },
                "get_status" => new JsonObject { ["online"] = AccountOnline, ["good"] = AccountOnline },
                "get_group_info" => new JsonObject { ["group_id"] = 99999, ["group_name"] = GroupName },
                "get_group_msg_history" => BuildHistory(),
                "get_forward_msg" => BuildForwardRecord(root["params"]?["id"]?.GetValue<string>()),
                "send_group_msg" or "send_private_msg" => new JsonObject { ["message_id"] = 555 },
                _ => new JsonObject()
            };

            await SendRawAsync(new JsonObject
            {
                ["status"] = "ok",
                ["retcode"] = 0,
                ["echo"] = echo,
                ["data"] = data
            }.ToJsonString(), ct);
        }
    }

    /// <summary>发一个戳一戳事件（notice）；targetId 等于机器人自己 = 戳了机器人。</summary>
    public async Task SendPokeAsync(long groupId, long userId, long targetId, CancellationToken ct = default)
    {
        var evt = new JsonObject
        {
            ["post_type"] = "notice",
            ["notice_type"] = "notify",
            ["sub_type"] = "poke",
            ["self_id"] = SelfId,
            ["user_id"] = userId,
            ["target_id"] = targetId,
            ["group_id"] = groupId,
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds()
        };

        await SendRawAsync(evt.ToJsonString(), ct);
    }

    /// <summary>发一个原始 JSON 帧（例如消息事件）。</summary>
    public async Task SendRawAsync(string json, CancellationToken ct)
    {
        var socket = _client ?? _serverSocket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("尚未连接");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>构造并发送一条 OneBot 消息事件。</summary>
    public async Task SendGroupMessageAsync(
        long groupId,
        long userId,
        string senderName,
        string text,
        long messageId,
        bool mentionBot = false,
        string? imageUrl = null,
        CancellationToken ct = default)
    {
        var segments = new JsonArray();
        if (mentionBot)
        {
            segments.Add(new JsonObject
            {
                ["type"] = "at",
                ["data"] = new JsonObject { ["qq"] = SelfId.ToString() }
            });
        }

        segments.Add(new JsonObject
        {
            ["type"] = "text",
            ["data"] = new JsonObject { ["text"] = text }
        });

        if (imageUrl is not null)
        {
            segments.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = new JsonObject { ["url"] = imageUrl }
            });
        }

        var evt = new JsonObject
        {
            ["post_type"] = "message",
            ["message_type"] = "group",
            ["sub_type"] = "normal",
            ["message_id"] = messageId,
            ["group_id"] = groupId,
            ["user_id"] = userId,
            ["self_id"] = SelfId,
            ["raw_message"] = text,
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            ["message"] = segments,
            ["sender"] = new JsonObject
            {
                ["user_id"] = userId,
                ["nickname"] = senderName,
                ["card"] = senderName,
                ["role"] = "member"
            }
        };

        await SendRawAsync(evt.ToJsonString(), ct);
    }

    /// <summary>发送一条“音乐分享”（OneBot 的 music 段，type=163 就是网易云）。</summary>
    public async Task SendGroupMusicAsync(
        long groupId,
        long userId,
        string senderName,
        long songId,
        long messageId,
        bool mentionBot = false,
        CancellationToken ct = default)
    {
        var segments = new JsonArray();
        if (mentionBot)
        {
            segments.Add(new JsonObject
            {
                ["type"] = "at",
                ["data"] = new JsonObject { ["qq"] = SelfId.ToString() }
            });
        }

        segments.Add(new JsonObject
        {
            ["type"] = "music",
            ["data"] = new JsonObject { ["type"] = "163", ["id"] = songId.ToString() }
        });

        var evt = new JsonObject
        {
            ["post_type"] = "message",
            ["message_type"] = "group",
            ["sub_type"] = "normal",
            ["message_id"] = messageId,
            ["group_id"] = groupId,
            ["user_id"] = userId,
            ["self_id"] = SelfId,
            ["raw_message"] = $"[CQ:music,type=163,id={songId}]",
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            ["message"] = segments,
            ["sender"] = new JsonObject
            {
                ["user_id"] = userId,
                ["nickname"] = senderName,
                ["card"] = senderName,
                ["role"] = "member"
            }
        };

        await SendRawAsync(evt.ToJsonString(), ct);
    }

    /// <summary>合并转发的聊天记录：forward id → 节点数组（get_forward_msg 会返回它）。</summary>
    public Dictionary<string, JsonArray> ForwardRecords { get; } = new();

    /// <summary>收到过几次 get_forward_msg（验证“真的去拉内容了”）。</summary>
    public int ForwardFetchCount => Volatile.Read(ref _forwardFetches);

    private int _forwardFetches;

    /// <summary>发一条自定义段组合的群消息（转发/卡片/文件等都用它）。</summary>
    public async Task SendGroupSegmentsAsync(
        long groupId,
        long userId,
        string senderName,
        long messageId,
        JsonArray segments,
        string rawMessage,
        CancellationToken ct = default)
    {
        var evt = new JsonObject
        {
            ["post_type"] = "message",
            ["message_type"] = "group",
            ["sub_type"] = "normal",
            ["message_id"] = messageId,
            ["group_id"] = groupId,
            ["user_id"] = userId,
            ["self_id"] = SelfId,
            ["raw_message"] = rawMessage,
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            ["message"] = segments,
            ["sender"] = new JsonObject
            {
                ["user_id"] = userId,
                ["nickname"] = senderName,
                ["card"] = senderName,
                ["role"] = "member"
            }
        };

        await SendRawAsync(evt.ToJsonString(), ct);
    }

    /// <summary>发一条“合并转发聊天记录”。</summary>
    public Task SendGroupForwardAsync(long groupId, long userId, string senderName, string forwardId, long messageId,
        string? text = null, CancellationToken ct = default)
    {
        var segments = new JsonArray();
        if (text is not null)
        {
            segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = text } });
        }

        segments.Add(new JsonObject { ["type"] = "forward", ["data"] = new JsonObject { ["id"] = forwardId } });
        return SendGroupSegmentsAsync(groupId, userId, senderName, messageId, segments,
            (text ?? string.Empty) + $"[CQ:forward,id={forwardId}]", ct);
    }

    /// <summary>拼一个转发节点（模拟其他人转过来的聊天记录）。</summary>
    public static JsonObject ForwardNode(long userId, string nickname, params string[] texts)
    {
        var segments = new JsonArray();
        foreach (var t in texts)
        {
            if (t == "[图片]")
            {
                segments.Add(new JsonObject { ["type"] = "image", ["data"] = new JsonObject { ["file"] = "x.jpg" } });
            }
            else
            {
                segments.Add(new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = t } });
            }
        }

        return new JsonObject
        {
            ["user_id"] = userId,
            ["nickname"] = nickname,
            ["sender"] = new JsonObject { ["user_id"] = userId, ["nickname"] = nickname },
            ["message"] = segments,
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds()
        };
    }

    public async Task SendPrivateMessageAsync(long userId, string senderName, string text, long messageId, CancellationToken ct = default)
    {
        var evt = new JsonObject
        {
            ["post_type"] = "message",
            ["message_type"] = "private",
            ["sub_type"] = "friend",
            ["message_id"] = messageId,
            ["group_id"] = 0,
            ["user_id"] = userId,
            ["self_id"] = SelfId,
            ["raw_message"] = text,
            ["time"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
            ["message"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["data"] = new JsonObject { ["text"] = text } }
            },
            ["sender"] = new JsonObject { ["user_id"] = userId, ["nickname"] = senderName }
        };

        await SendRawAsync(evt.ToJsonString(), ct);
    }

    /// <summary>等待某个动作出现（或超时）。返回该动作（未出现返回 null）。</summary>
    public async Task<JsonObject?> WaitForActionAsync(string action, TimeSpan timeout, int skip = 0)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            var matches = ActionsReceived.Where(a => a["action"]?.GetValue<string>() == action).ToList();
            if (matches.Count > skip)
            {
                return matches[skip];
            }

            await Task.Delay(100);
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            _client?.Abort();
            _client?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _serverSocket?.Abort();
            _serverSocket?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _forwardListener?.Stop();
            _forwardListener?.Close();
        }
        catch
        {
            // 忽略
        }
    }
}
