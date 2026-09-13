using System.IO;

namespace QQChatAgent.Services;

/// <summary>
/// 应用路径中心（headless / 跨平台）。
/// 优先级：QQCHAT_DATA_DIR 环境变量 → 工作区 ./runtime → /data（容器默认挂载点）→ 用户数据目录。
/// </summary>
public static class AppPaths
{
    /// <summary>运行时根：数据/日志统一放这里（容器内建议挂载为卷）。</summary>
    public static string RuntimeRoot { get; }

    /// <summary>日志目录：{RuntimeRoot}/logs</summary>
    public static string LogsDir => Path.Combine(RuntimeRoot, "logs");

    /// <summary>数据目录（设置/会话/人物档案）：{RuntimeRoot}/data</summary>
    public static string DataDir => Path.Combine(RuntimeRoot, "data");

    /// <summary>设置文件：{DataDir}/settings.json</summary>
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    static AppPaths()
    {
        RuntimeRoot = ResolveRuntimeRoot();
        EnsureDirectories();
    }

    private static string ResolveRuntimeRoot()
    {
        // 1) 显式指定（容器 / systemd 推荐）
        var configured = Environment.GetEnvironmentVariable("QQCHAT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        // 2) 工作区：从程序所在目录向上找 runtime/ 或 .git（开发环境）
        var workspace = FindWorkspaceRoot();
        if (workspace is not null)
        {
            return Path.Combine(workspace, "runtime");
        }

        // 3) 容器约定：/data 存在且可写
        if (Directory.Exists("/data") && IsWritable("/data"))
        {
            return "/data";
        }

        // 4) 兜底：当前工作目录下 runtime
        return Path.Combine(Directory.GetCurrentDirectory(), "runtime");
    }

    /// <summary>从程序所在目录向上查找工作区根（含 .git）。</summary>
    private static string? FindWorkspaceRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch
        {
            // 忽略
        }

        return null;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(DataDir);
        }
        catch
        {
            // 忽略：由上层在使用时报告
        }
    }
}
