using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using QQChatAgent.Models;

namespace QQChatAgent.Services;

/// <summary>会话与消息的磁盘持久化（QQ 会话 ↔ 本地会话映射的来源）。</summary>
public sealed class ConversationStore
{
    // 不转义非 ASCII：容器里 cat conversations.json 应该能直接看懂内容
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _lastWrite;
    private bool _dirty;

    public ConversationStore(string? baseDir = null)
    {
        baseDir ??= AppPaths.DataDir;
        _filePath = Path.Combine(baseDir, "conversations.json");
    }

    /// <summary>加载全部持久化会话（不还原画刷，由 ViewModel 统一赋色）。</summary>
    public async Task<List<ConversationRecord>> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<ConversationRecord>();
            }

            var json = await File.ReadAllTextAsync(_filePath);
            var list = JsonSerializer.Deserialize<List<ConversationRecord>>(json);
            return list ?? new List<ConversationRecord>();
        }
        catch
        {
            return new List<ConversationRecord>();
        }
    }

    /// <summary>请求保存（节流：1 秒内多次变更合并为一次写盘）。</summary>
    public void RequestSave(IEnumerable<ConversationRecord> records)
    {
        _ = SaveCoreAsync(records.ToList());
    }

    private async Task SaveCoreAsync(List<ConversationRecord> records)
    {
        await _lock.WaitAsync();
        try
        {
            _dirty = true;
            var now = DateTime.UtcNow;
            if ((now - _lastWrite).TotalSeconds < 1)
            {
                await Task.Delay(1000);
            }

            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            _lastWrite = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(records, JsonOptions);
            var temp = _filePath + ".tmp";
            await File.WriteAllTextAsync(temp, json);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // 持久化失败不应影响主流程
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>磁盘上的会话快照（与 UI 模型解耦）。</summary>
public sealed class ConversationRecord
{
    public string? Id { get; set; }

    /// <summary>QQ 映射键："private:{QQ号}" 或 "group:{群号}"。null = 本地会话。</summary>
    public string? SourceKey { get; set; }

    public string Kind { get; set; } = "LocalTest";

    public string Name { get; set; } = string.Empty;

    public string AvatarText { get; set; } = "?";

    public string? AvatarUrl { get; set; }

    public int AvatarIndex { get; set; }

    public List<MessageRecord> Messages { get; set; } = new();

    public long LastTimeUnix { get; set; }

    public int UnreadCount { get; set; }
}

public sealed class MessageRecord
{
    public string Role { get; set; } = "Peer";

    public string Text { get; set; } = string.Empty;

    public long TimeUnix { get; set; }

    public string? SenderName { get; set; }

    /// <summary>发送者 QQ 号（headless 新增：重启后仍能建人物档案）。</summary>
    public long? SenderId { get; set; }

    /// <summary>消息图片 URL（headless 新增：重启后仍可识图）。</summary>
    public List<string>? ImageUrls { get; set; }

    /// <summary>QQ 原始消息 ID（headless 新增：重启后历史去重仍生效）。</summary>
    public long? QqMessageId { get; set; }

    /// <summary>
    /// 会话内单调序号（同时被人物档案/画像用作“这条是否已在上下文里”的判据）。
    /// **必须持久化**：不存的话重启后序号从头计数，会小于画像里已记录的 ThroughSeq，
    /// 导致该成员的记忆静默停止更新（既不注入也不再摘要）。
    /// 可为负数 —— 拉取到的群历史会插到现有序号之前。
    /// </summary>
    public long Seq { get; set; }
}