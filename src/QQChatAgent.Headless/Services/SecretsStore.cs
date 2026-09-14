using QQChatAgent.Services.Data;

namespace QQChatAgent.Services;

/// <summary>
/// 面板里填的密钥（模型 API Key、网易云登录态），存在 SQLite 的 <c>secrets</c> 表里。
///
/// 为什么单独一张表而不是塞进 settings：行为配置那份是要给人看、给面板读的（也方便贴出来排障），
/// 密钥混进去一不小心就跟着泄露。表分开之后：面板/日志/接口响应永远只给掩码（<c>sk-****</c>），
/// 要分享配置只需要导出 settings 那一行。库文件权限设成 600（见 <see cref="RestrictDbPermissions"/>）。
/// 环境变量仍然优先于“从未在面板里填过”的默认值：面板填过之后以面板为准。
///
/// 网易云登录态为什么也要存这里：以前它只活在自建 API 容器的进程内存里，
/// **容器一重建（升级镜像 / compose up 重创）就得重新扫码** —— 号主反馈“老是掉登录”就是这个。
/// 现在扫码成功后把 cookie 存进本表，并随手带上每个请求（见 NeteaseMusicClient），
/// 与那个容器活着不活着无关。
/// </summary>
public static class SecretsStore
{
    /// <summary>数据根目录（与 data/、stickers/ 同级）。</summary>
    public static void Init(string dataRoot)
    {
        // 库文件是进程级单例，这里只负责“把权限收紧”（库文件里现在装着密钥）。
        RestrictDbPermissions();
    }

    /// <summary>读取面板里填过的 API Key；没有/读失败返回 null（调用方回退环境变量）。</summary>
    public static string? LoadApiKey() => Load("apiKey");

    /// <summary>写入（空值 = 删掉该条）。返回是否成功。</summary>
    public static bool SaveApiKey(string? apiKey) => Save("apiKey", apiKey);

    /// <summary>网易云登录态（扫码成功后由面板那条链路存下来）；没有就回退环境变量。</summary>
    public static string? LoadNeteaseCookie() => Load("neteaseCookie");

    /// <summary>保存网易云登录态（空值 = 清掉，下次回退环境变量）。</summary>
    public static bool SaveNeteaseCookie(string? cookie) => Save("neteaseCookie", cookie);

    /// <summary>读一条密钥（没有/读失败返回 null）。</summary>
    public static string? Load(string name)
    {
        try
        {
            var value = AppDatabase.Scalar<string>("SELECT value FROM secrets WHERE name = $n", ("$n", name));
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            FileLog.Warn("Secrets", $"密钥读取失败（回退环境变量）：{ex.Message}");
            return null;
        }
    }

    /// <summary>写一条密钥（空值 = 删掉该条）。返回是否成功。</summary>
    public static bool Save(string name, string? value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AppDatabase.Write(conn => AppDatabase.Exec(conn, "DELETE FROM secrets WHERE name = $n", ("$n", name)));
            }
            else
            {
                AppDatabase.Write(conn => AppDatabase.Exec(conn,
                    "INSERT INTO secrets(name, value, updated_unix) VALUES($n, $v, $now) " +
                    "ON CONFLICT(name) DO UPDATE SET value = excluded.value, updated_unix = excluded.updated_unix",
                    ("$n", name), ("$v", value.Trim()), ("$now", DateTimeOffset.Now.ToUnixTimeSeconds())));
            }

            RestrictDbPermissions();
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Secrets", $"密钥写入失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>只给当前用户读写（Windows 上跳过：部署目录本来就只有管理员能看）。</summary>
    private static void RestrictDbPermissions()
    {
        if (OperatingSystem.IsWindows() || !AppDatabase.IsReady)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(AppDatabase.FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Secrets", $"库文件权限设置失败（仍可用，但请手工 chmod 600）：{ex.Message}");
        }
    }
}
