using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace QQChatAgent.Services;

/// <summary>
/// 配置加载：settings.json（可挂卷复用桌面版配置）+ 运行时保存。
/// 环境变量覆盖在 <see cref="Configuration.BotConfig"/> 中完成。
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string BaseDir => AppPaths.DataDir;

    public static string FilePath => AppPaths.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config] 读取 {FilePath} 失败，使用默认值: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, WriteOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config] 写入 {FilePath} 失败: {ex.Message}");
        }
    }
}
