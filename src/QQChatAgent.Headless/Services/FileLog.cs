using System.IO;
using System.Text;

namespace QQChatAgent.Services;

/// <summary>
/// 应用日志（headless）：同时写 stdout（容器友好，docker logs 可读）与 logs/qqchat.log。
/// 设 QQCHAT_LOG_FILE=0 可只输出到 stdout。
/// </summary>
public static class FileLog
{
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(AppPaths.LogsDir, "qqchat.log");

    /// <summary>是否同时写文件（托管侧可关闭）。</summary>
    public static bool WriteToFile { get; set; } = true;

    /// <summary>控制台冗余度：Quiet 只输出 Warning/Error 及以上。</summary>
    public static bool Verbose { get; set; } = true;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";

        // 1) stdout/stderr：容器日志（第三方 dalvik 等以 stdout 为准）
        if (Verbose)
        {
            Console.WriteLine(line);
        }

        // 2) 文件：便于事后排查
        if (!WriteToFile)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    public static void Write(string tag, string message) => Write($"[{tag}] {message}");

    /// <summary>警告：无论 Verbose 与否都输出到 stderr。</summary>
    public static void Warn(string tag, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] WARN {message}";
        Console.Error.WriteLine(line);
        if (!WriteToFile)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 忽略
        }
    }
}