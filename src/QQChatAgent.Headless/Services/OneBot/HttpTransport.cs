using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace QQChatAgent.Services.OneBot;

/// <summary>
/// HTTP 模式：动作走 POST {base}/{action}；
/// 事件通过 get_latest_events 轮询获取（NapCat / LLOneBot 等均支持该动作）。
/// </summary>
public sealed class HttpTransport : IOneBotTransport
{
    private readonly string _baseUrl;
    private readonly string? _token;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private int _failCount;

    public event Action<string>? OnText;
    public event Action<bool>? OnStateChanged;

    public bool IsConnected { get; private set; }

    public HttpTransport(string address, string? token)
    {
        _baseUrl = address.TrimEnd('/');
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public Task StartAsync()
    {
        if (_loopTask is null || _loopTask.IsCompleted)
        {
            _loopTask = Task.Run(PollLoopAsync);
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        _cts.Cancel();
        SetConnected(false);
    }

    public void Dispose() => Stop();

    public async Task SendActionAsync(string action, string paramsJson, string echo, CancellationToken ct = default)
    {
        if (paramsJson.Length == 0)
        {
            paramsJson = "{}";
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        await PostAsync(action, paramsJson, linked.Token);
    }

    private async Task PollLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var response = await PostAsync("get_latest_events", "{}", _cts.Token);
                var json = await response.Content.ReadAsStringAsync(_cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    _failCount = 0;
                    SetConnected(true);
                    foreach (var item in data.EnumerateArray())
                    {
                        OnText?.Invoke(item.GetRawText());
                    }
                }
                else
                {
                    _failCount++;
                    SetConnected(_failCount < 3);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                _failCount++;
                SetConnected(_failCount < 3);
            }

            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string action, string paramsJson, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(8);
        if (_token is not null)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/" + action)
        {
            Content = new StringContent(paramsJson, Encoding.UTF8, "application/json")
        };
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        OnStateChanged?.Invoke(connected);
    }
}