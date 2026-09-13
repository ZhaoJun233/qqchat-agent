using System.IO;
using Microsoft.Data.Sqlite;

namespace QQChatAgent.IntegrationHarness;

/// <summary>
/// 直接读机器人那个 SQLite 库的探针（只读）。
///
/// 为什么测试要直连库：以前“落盘了吗”可以直接看 JSON 文件，
/// 现在数据在库里 —— 断言得能看到库内容，否则“持久化”这件事就没被验到。
/// 只读打开（Mode=ReadOnly）+ busy_timeout：机器人进程同时在写也不会互相干扰。
/// </summary>
public static class DbProbe
{
    public static string DbPath(string dataDir) => Path.Combine(dataDir, "data", "qqchat.db");

    public static bool Exists(string dataDir) => File.Exists(DbPath(dataDir));

    /// <summary>查一列文本（取第一行第一列）。查不到返回 null。</summary>
    public static string? Text(string dataDir, string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open(dataDir);
        if (conn is null)
        {
            return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result);
    }

    /// <summary>查一个整数（查不到返回 0）。</summary>
    public static long Count(string dataDir, string sql, params (string Name, object? Value)[] args)
        => long.TryParse(Text(dataDir, sql, args), out var n) ? n : 0;

    /// <summary>把整张结果集拼成一段文本（断言里做 Contains 用）。</summary>
    public static string Dump(string dataDir, string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open(dataDir);
        if (conn is null)
        {
            return string.Empty;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var sb = new System.Text.StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0)
                {
                    sb.Append('|');
                }

                sb.Append(reader.IsDBNull(i) ? "(null)" : reader.GetValue(i).ToString());
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>表里有多少行。</summary>
    public static long TableCount(string dataDir, string table)
        => Count(dataDir, $"SELECT COUNT(1) FROM {table}");

    private static SqliteConnection? Open(string dataDir)
    {
        var path = DbPath(dataDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());

            conn.Open();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
            return conn;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
