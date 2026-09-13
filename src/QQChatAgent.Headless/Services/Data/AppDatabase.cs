using System.IO;
using Microsoft.Data.Sqlite;

namespace QQChatAgent.Services.Data;

/// <summary>
/// 全局唯一的 SQLite 数据库：<c>{RuntimeRoot}/data/qqchat.db</c>。
///
/// 为什么从“一堆 JSON 文件”换成数据库（2026-09-13 号主要求）：
///   • **一致性**：会话/消息/档案分散在 conversations.json + member_profiles/*.json + archive/*.jsonl 里，
///     一次崩溃可能只写了一半（会话写了、档案没写），跨文件没法用事务；
///   • **规模**：消息是追加型数据，JSON 每次全量重写（几千条就明显卡）；
///     档案按人一个文件，人一多就是几百个小文件；
///   • **查询**：面板要“某会话说过的最近 N 条 / 某人在某群的画像 / 归档里翻旧账”，
///     在 JSON 上只能全量读进内存再过滤；数据库一句 SQL 就能干，还能加索引。
///
/// 用法约定：
///   • 每个操作自己开关连接（Microsoft.Data.Sqlite 有连接池，开销很小）；
///   • 写操作走 <see cref="Write"/> / <see cref="WriteAsync"/>，自带事务；
///   • WAL + busy_timeout：读不阻塞写，偶发并发也只等几秒而不是直接抛。
/// 数据迁移：老版本留下的 JSON 由 <see cref="LegacyJsonImporter"/> 一次性导入（导入后旧文件改名留档）。
/// </summary>
public static class AppDatabase
{
    private static readonly object InitGate = new();
    private static bool _ready;
    private static string _filePath = string.Empty;
    private static string _connectionString = string.Empty;

    /// <summary>库文件路径（未初始化时按 AppPaths 推导）。</summary>
    public static string FilePath
    {
        get
        {
            if (!string.IsNullOrEmpty(_filePath))
            {
                return _filePath;
            }

            _filePath = Path.Combine(AppPaths.DataDir, "qqchat.db");
            return _filePath;
        }
    }

    /// <summary>是否已经初始化过（面板/日志用来提示“数据在库里”）。</summary>
    public static bool IsReady => _ready;

    /// <summary>
    /// 建库建表（幂等）。进程启动时调用一次；重复调用直接返回。
    /// </summary>
    public static void Initialize()
    {
        lock (InitGate)
        {
            if (_ready)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = FilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true
            }.ToString();

            using (var conn = Open())
            {
                Exec(conn, "PRAGMA journal_mode=WAL;");      // 读写不互相阻塞（容器里只有一个进程，但线程很多）
                Exec(conn, "PRAGMA synchronous=NORMAL;");    // WAL 下够安全，写入快很多
                Migrate(conn);
            }

            _ready = true;
        }
    }

    /// <summary>开一个连接（用 from 调用方 using 释放；池化，开销极小）。</summary>
    public static SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout=5000;");   // 并发写：等一会儿而不是立刻抛 SQLITE_BUSY
        Exec(conn, "PRAGMA foreign_keys=ON;");
        return conn;
    }

    /// <summary>执行一条无返回的 SQL。</summary>
    public static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    /// <summary>写事务（同步）：回调里做若干条写操作，异常整体回滚。</summary>
    public static void Write(Action<SqliteConnection> action)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        try
        {
            action(conn);
            tx.Commit();
        }
        catch
        {
            try
            {
                tx.Rollback();
            }
            catch
            {
                // 忽略
            }

            throw;
        }
    }

    /// <summary>写事务（异步）：给 async 调用链用（SQLite 本身是同步 IO，这里只是不阻塞调用方太久）。</summary>
    public static Task WriteAsync(Action<SqliteConnection> action) => Task.Run(() => Write(action));

    /// <summary>查询并映射每一行。</summary>
    public static List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var list = new List<T>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }

    /// <summary>查询单个值（无行时返回 default）。</summary>
    public static T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull)
        {
            return default;
        }

        // 可空类型要转到它底下的真实类型：Convert.ChangeType 不认 Nullable<T>
        //（踩过：Scalar<long?> 直接抛 Invalid cast from 'System.Int64' to 'System.Nullable`1[System.Int64]'）
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, target);
    }

    /// <summary>元数据读写（迁移标记、架构版本说明等都放这）。</summary>
    public static string? GetMeta(string key)
        => Scalar<string>("SELECT value FROM meta WHERE key = $k", ("$k", key));

    public static void SetMeta(string key, string value)
        => Write(conn => Exec(conn,
            "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", value)));

    public static bool HasMeta(string key)
        => Scalar<long>("SELECT COUNT(1) FROM meta WHERE key = $k", ("$k", key)) > 0;

    /// <summary>读取可空字符串/长整形的工具（各处读表都会用到）。</summary>
    public static string? Str(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static long? LongOrNull(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static long Long(SqliteDataReader r, string name, long fallback = 0)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? fallback : r.GetInt64(i);
    }

    public static int Int(SqliteDataReader r, string name, int fallback = 0)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? fallback : (int)r.GetInt64(i);
    }

    public static bool Bool(SqliteDataReader r, string name, bool fallback = false)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? fallback : r.GetInt64(i) != 0;
    }

    public static double Double(SqliteDataReader r, string name, double fallback = 0)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? fallback : r.GetDouble(i);
    }

    /// <summary>
    /// 建表 / 升级。用 <c>PRAGMA user_version</c> 记版本：
    /// 老版本只认自己认识的那几列，加列用 ALTER 逐版补（这里一次性建全，后续版本追加）。
    /// </summary>
    private static void Migrate(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS meta(
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );

            -- 行为配置：整份 JSON 存一行。
            -- 为什么不做成几十个列：它永远是“整份读、整份写”（面板一次提交整张表单），
            -- 列化只会带来一版一版的 ALTER 与字段映射漂移；数据仍然在库里（要查可以 json_extract）。
            CREATE TABLE IF NOT EXISTS settings(
              id           INTEGER PRIMARY KEY CHECK (id = 1),
              json         TEXT NOT NULL,
              updated_unix INTEGER NOT NULL
            );

            -- 密钥（面板里填过的模型 API Key 等）。单独一张表：它不进 settings 那份给人看的 JSON。
            CREATE TABLE IF NOT EXISTS secrets(
              name         TEXT PRIMARY KEY,
              value        TEXT NOT NULL,
              updated_unix INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS conversations(
              id             TEXT PRIMARY KEY,
              source_key     TEXT UNIQUE,
              kind           TEXT NOT NULL,
              name           TEXT NOT NULL,
              avatar_text    TEXT,
              avatar_url     TEXT,
              avatar_index   INTEGER NOT NULL DEFAULT 0,
              last_time_unix INTEGER NOT NULL DEFAULT 0,
              unread_count   INTEGER NOT NULL DEFAULT 0,
              history_loaded INTEGER NOT NULL DEFAULT 0,
              max_messages   INTEGER NOT NULL DEFAULT 500,
              -- 下一个待分配的会话内序号。
              -- 为什么必须存它：序号是“档案/画像判据”（through_seq 比大小），
              -- 若重启后从 0 重新开始，新消息的序号会小于画像已折叠的边界 → 记忆静默冻结；
              -- 而且 (source_key, seq) 是消息主键，重号会把老消息覆盖掉（踩过）。
              next_seq       INTEGER NOT NULL DEFAULT 0,
              updated_unix   INTEGER NOT NULL DEFAULT 0
            );

            -- 消息：主键 (source_key, seq)。
            -- seq 是会话内单调序号（群历史补录会是负数），撤回/归档都只改标记不删行。
            CREATE TABLE IF NOT EXISTS messages(
              source_key    TEXT NOT NULL,
              seq           INTEGER NOT NULL,
              role          TEXT NOT NULL,
              text          TEXT NOT NULL,
              time_unix     INTEGER NOT NULL,
              sender_name   TEXT,
              sender_id     INTEGER,
              qq_message_id INTEGER,
              recalled      INTEGER NOT NULL DEFAULT 0,
              images        TEXT,
              archived      INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (source_key, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_messages_live ON messages(source_key, archived, seq);
            CREATE INDEX IF NOT EXISTS ix_messages_mid ON messages(qq_message_id);

            CREATE TABLE IF NOT EXISTS members(
              uid          TEXT PRIMARY KEY,
              name         TEXT NOT NULL DEFAULT '',
              updated_unix INTEGER NOT NULL DEFAULT 0
            );

            -- 某个人的发言（按 uid + 群号 + seq）。
            -- ⚠ 主键必须带 group_id：seq 是**会话内**序号，A 群和 B 群的序号会重叠（都是 1、2、3…），
            -- 不带群号就会把另一个群的发言当成冲突丢掉（踩过：S8 里 B 群的档案全是空的）。
            CREATE TABLE IF NOT EXISTS member_messages(
              uid        TEXT NOT NULL,
              group_id   INTEGER NOT NULL DEFAULT 0,
              seq        INTEGER NOT NULL,
              group_name TEXT,
              text       TEXT NOT NULL,
              time_unix  INTEGER NOT NULL,
              PRIMARY KEY (uid, group_id, seq)
            );
            CREATE INDEX IF NOT EXISTS ix_member_messages_time ON member_messages(uid, time_unix);

            -- 长期画像：一个人在一个会话范围（群/私聊）里一份
            CREATE TABLE IF NOT EXISTS member_summaries(
              uid          TEXT NOT NULL,
              scope        TEXT NOT NULL,
              text         TEXT NOT NULL,
              through_seq  INTEGER NOT NULL DEFAULT 0,
              updated_unix INTEGER NOT NULL DEFAULT 0,
              folded_count INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (uid, scope)
            );

            CREATE TABLE IF NOT EXISTS mood(
              id           INTEGER PRIMARY KEY CHECK (id = 1),
              text         TEXT,
              updated_unix INTEGER
            );

            -- 心情的客观来源：最近被戳的时刻（窗口外的会被清掉）
            CREATE TABLE IF NOT EXISTS mood_pokes(
              at_unix INTEGER PRIMARY KEY
            );

            CREATE TABLE IF NOT EXISTS heard_songs(
              key              TEXT PRIMARY KEY,
              platform         TEXT NOT NULL DEFAULT '',
              song_id          TEXT NOT NULL DEFAULT '',
              title            TEXT NOT NULL DEFAULT '',
              artist           TEXT NOT NULL DEFAULT '',
              album            TEXT NOT NULL DEFAULT '',
              duration_seconds REAL NOT NULL DEFAULT 0,
              features         TEXT NOT NULL DEFAULT '',
              lyric_excerpt    TEXT NOT NULL DEFAULT '',
              first_heard_unix INTEGER NOT NULL DEFAULT 0,
              last_heard_unix  INTEGER NOT NULL DEFAULT 0,
              heard_count      INTEGER NOT NULL DEFAULT 0
            );

            -- 表情包索引（图片本体仍放 stickers/ 目录，库里只存元数据）
            CREATE TABLE IF NOT EXISTS stickers(
              id                TEXT PRIMARY KEY,
              hash              TEXT NOT NULL DEFAULT '',
              file              TEXT NOT NULL DEFAULT '',
              ext               TEXT NOT NULL DEFAULT 'png',
              bytes             INTEGER NOT NULL DEFAULT 0,
              added_unix        INTEGER NOT NULL DEFAULT 0,
              last_used_unix    INTEGER NOT NULL DEFAULT 0,
              uses              INTEGER NOT NULL DEFAULT 0,
              from_uid          TEXT,
              from_group        INTEGER NOT NULL DEFAULT 0,
              description       TEXT,
              tags              TEXT,
              is_sticker        INTEGER,
              described         INTEGER NOT NULL DEFAULT 0,
              describe_attempts INTEGER NOT NULL DEFAULT 0
            );
            """);

        var version = Scalar<long>("PRAGMA user_version");
        if (version < 1)
        {
            Write(conn2 => Exec(conn2, "PRAGMA user_version = 1;"));
        }

        // v2：会话行补 next_seq（老库升级用；新库建表时已经有了）
        if (version < 2)
        {
            var hasColumn = Scalar<long>(
                "SELECT COUNT(1) FROM pragma_table_info('conversations') WHERE name = 'next_seq'") > 0;
            if (!hasColumn)
            {
                Write(conn2 => Exec(conn2, "ALTER TABLE conversations ADD COLUMN next_seq INTEGER NOT NULL DEFAULT 0;"));
            }

            Write(conn2 => Exec(conn2, "PRAGMA user_version = 2;"));
        }

        // v3：member_messages 的主键补上 group_id（旧版 A/B 群序号重叠会互相覆盖）
        if (version < 3)
        {
            var pkHasGroup = Scalar<long>("""
                SELECT COUNT(1) FROM pragma_table_info('member_messages') WHERE name = 'group_id' AND pk > 0
                """) > 0;
            if (!pkHasGroup)
            {
                // 这张表是“发言原文缓存”（长期画像在 member_summaries 里，不受影响），
                // 重建一次比写一个数据搬迁脚本简单得多；旧的 JSON 导入会在新库上重跑。
                Write(conn2 =>
                {
                    Exec(conn2, "DROP TABLE IF EXISTS member_messages;");
                    Exec(conn2, """
                        CREATE TABLE member_messages(
                          uid        TEXT NOT NULL,
                          group_id   INTEGER NOT NULL DEFAULT 0,
                          seq        INTEGER NOT NULL,
                          group_name TEXT,
                          text       TEXT NOT NULL,
                          time_unix  INTEGER NOT NULL,
                          PRIMARY KEY (uid, group_id, seq)
                        );
                        CREATE INDEX IF NOT EXISTS ix_member_messages_time ON member_messages(uid, time_unix);
                        """);
                });
            }

            Write(conn2 => Exec(conn2, "PRAGMA user_version = 3;"));
        }
    }
}
