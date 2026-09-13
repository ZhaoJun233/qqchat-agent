using System.Text.Json;
using QQChatAgent.Services.Data;

namespace QQChatAgent.Services;

/// <summary>
/// 配置读写：行为配置存在 SQLite 的 <c>settings</c> 表（整份 JSON 一行）。
///
/// 为什么整份存 JSON 而不是拆成几十个列：这份配置永远是“整份读、整份写”
/// （面板一次提交整张表单），列化只会带来一版一版的 ALTER 与字段映射漂移；
/// 它仍然在库里 —— 备份、查询（<c>json_extract(json,'$.AiDesire')</c>）、事务都成立。
/// 老版本的 <c>settings.json</c> 由 LegacyJsonImporter 导入后归档。
/// </summary>
public static class SettingsStore
{
    public static string FilePath => AppDatabase.FilePath;

    /// <summary>库里到底存过配置没有（用来判断“首次部署”：没存过才用环境变量当种子）。</summary>
    public static bool HasStoredSettings()
    {
        try
        {
            return AppDatabase.Scalar<long>("SELECT COUNT(1) FROM settings") > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            var json = AppDatabase.Scalar<string>("SELECT json FROM settings WHERE id = 1");
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config] 读取配置失败，使用默认值: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            AppDatabase.Write(conn => AppDatabase.Exec(conn,
                "INSERT INTO settings(id, json, updated_unix) VALUES(1, $json, $now) " +
                "ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_unix = excluded.updated_unix",
                ("$json", json), ("$now", DateTimeOffset.Now.ToUnixTimeSeconds())));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Config] 写入配置失败: {ex.Message}");
        }
    }
}
