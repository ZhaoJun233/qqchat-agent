using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQChatAgent.Services;

/// <summary>
/// 面板里填的密钥（目前只有模型 API Key）。
///
/// 为什么不写进 settings.json：行为配置那份文件是要给人看、给面板读的（也方便备份/贴出来排障），
/// 密钥混进去一不小心就跟着泄露。所以单独一个 <c>data/secrets.json</c>，并且：
///   • 文件权限 600（仅本进程可读）—— 容器里也是同一套；
///   • 任何日志、任何接口响应都只给掩码（<c>sk-****</c>），不回显明文；
///   • 环境变量仍然优先于“从未在面板里填过”的默认值：面板填过之后以面板为准。
/// </summary>
public static class SecretsStore
{
    private static string _filePath = string.Empty;
    private static readonly object Gate = new();

    /// <summary>数据根目录（与 data/、stickers/ 同级）。</summary>
    public static void Init(string dataRoot)
    {
        _filePath = Path.Combine(dataRoot, "data", "secrets.json");
    }

    /// <summary>读取面板里填过的 API Key；没有/读失败返回 null（调用方回退环境变量）。</summary>
    public static string? LoadApiKey()
    {
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
                {
                    return null;
                }

                var secrets = JsonSerializer.Deserialize<PersistedSecrets>(File.ReadAllText(_filePath, Encoding.UTF8));
                var key = secrets?.ApiKey?.Trim();
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            catch (Exception ex)
            {
                FileLog.Warn("Secrets", $"密钥文件读取失败（回退环境变量）：{ex.Message}");
                return null;
            }
        }
    }

    /// <summary>写入（空值 = 删除文件里的该项）。返回是否成功。</summary>
    public static bool SaveApiKey(string? apiKey)
    {
        lock (Gate)
        {
            try
            {
                if (string.IsNullOrEmpty(_filePath))
                {
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var payload = new PersistedSecrets
                {
                    ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim()
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });

                File.WriteAllText(_filePath, json, new UTF8Encoding(false));
                RestrictPermissions(_filePath);
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Warn("Secrets", $"密钥文件写入失败：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>只给当前用户读写（Windows 上跳过：同一台机器上的部署目录本来就只有管理员能看）。</summary>
    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Secrets", $"密钥文件权限设置失败（仍可用，但请手工 chmod 600）：{ex.Message}");
        }
    }

    private sealed class PersistedSecrets
    {
        [JsonPropertyName("apiKey")] public string? ApiKey { get; set; }
    }
}
