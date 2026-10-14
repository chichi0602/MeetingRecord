using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Diagnostics;

public interface IDatabaseUsageService
{
    Task<DatabaseUsageReport> GetReportAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 量測 SQLite 的磁碟占用、頁面配置與各資料表用量。
///
/// PRAGMA 無法參數化，而 ExecuteSqlRaw 只回傳受影響列數，因此改走 Database.GetDbConnection() + DbCommand。
///
/// 每表的「資料量估算」是各欄位內容的位元組加總，因為綁定的 SQLite 未編入
/// dbstat 虛擬表（Starter 實測會回 "no such table: dbstat"）—— 而那是取得每表實際頁數的唯一途徑。
///
/// 0.4.120 移植自 NET10-Blazor-Starter（「監控與診斷 → 資料庫用量」）。
/// </summary>
public sealed class DatabaseUsageService : IDatabaseUsageService
{
    private readonly BackendDBContext context;
    private readonly ILogger<DatabaseUsageService> logger;

    public DatabaseUsageService(
        BackendDBContext context,
        ILogger<DatabaseUsageService> logger)
    {
        this.context = context;
        this.logger = logger;
    }

    public async Task<DatabaseUsageReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var report = new DatabaseUsageReport();

        // 走 EF 的 OpenConnectionAsync／CloseConnectionAsync 而非直接對 DbConnection 開關：
        // EF 內部有開啟計數，若連線已被其他查詢開著，這裡的開關只會增減計數而不會關掉別人的。
        // 特別重要的是本頁在 Blazor Server 上，scope 是整個 circuit 而非單一請求，
        // 同一個 DbContext 可能活很久 —— 更該把連線狀態原樣還回去。
        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = context.Database.GetDbConnection();

            CollectFileSizes(connection, report);

            report.PageSize = await ReadScalarInt64Async(connection, "PRAGMA page_size;", cancellationToken);
            report.PageCount = await ReadScalarInt64Async(connection, "PRAGMA page_count;", cancellationToken);
            report.FreelistCount = await ReadScalarInt64Async(connection, "PRAGMA freelist_count;", cancellationToken);

            report.Tables = await CollectTableUsageAsync(connection, report, cancellationToken);

            if (report.Tables.Count == 0)
            {
                report.Message = "資料庫中沒有任何資料表。";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to collect database usage.");
            report.ErrorMessage = $"讀取資料庫用量失敗：{ex.GetType().Name}。";
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        stopwatch.Stop();
        report.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;

        logger.LogInformation(
            "Database usage collected. Elapsed={ElapsedMs}ms, Tables={Tables}, OnDisk={OnDisk}",
            report.ElapsedMilliseconds, report.Tables.Count, FileSizeFormatter.Describe(report.TotalOnDiskBytes));

        return report;
    }

    /// <summary>
    /// 量測主檔與 WAL／SHM 三個檔案。
    ///
    /// 路徑取自「已開啟連線」的 DataSource（即 sqlite3_db_filename 解析出的絕對路徑），
    /// 而非組態值 —— 這樣量到的必定是 EF 實際在用的那個檔案，設定漂移不可能發生。
    /// （SystemSettings:ConnectionStrings:SQLiteDefaultConnection 是死設定，從未被讀取。）
    ///
    /// 在連線開著的時候讀檔案大小，避免中途被 checkpoint 掉而量到不一致的快照。
    /// </summary>
    private static void CollectFileSizes(DbConnection connection, DatabaseUsageReport report)
    {
        var mainPath = connection.DataSource;

        // 記憶體資料庫（單元測試）沒有檔案，但 PRAGMA 與逐表統計仍然可用。
        if (string.IsNullOrWhiteSpace(mainPath) || mainPath.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            report.Message = "目前連線未使用檔案資料庫，無磁碟占用資訊。";
            return;
        }

        report.DatabaseFilePath = mainPath;
        report.DatabaseFileName = Path.GetFileName(mainPath);

        report.MainDbBytes = TryGetFileLength(mainPath, report, "主資料庫檔", out var mainExists);
        if (mainExists == false)
        {
            // 註：連線字串未指定 Mode，預設為 ReadWriteCreate，開啟連線時會自動建出空檔。
            // 實務上應用程式啟動就會存取資料庫，所以走到這裡幾乎只可能是權限問題。
            report.Warnings.Add($"找不到資料庫檔案：{mainPath}");
        }

        report.WalBytes = TryGetFileLength(mainPath + "-wal", report, "預寫日誌", out var walExists);
        report.WalFileExists = walExists;

        report.ShmBytes = TryGetFileLength(mainPath + "-shm", report, "共享記憶體索引", out var shmExists);
        report.ShmFileExists = shmExists;
    }

    private static long TryGetFileLength(string path, DatabaseUsageReport report, string label, out bool exists)
    {
        exists = false;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists == false)
            {
                // WAL／SHM 不存在是正常的（乾淨關閉或剛 checkpoint），0 才是事實，不該顯示為錯誤。
                return 0;
            }

            exists = true;
            return info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Warnings.Add($"無法讀取{label}的檔案大小：{Path.GetFileName(path)}（{ex.GetType().Name}）。");
            return 0;
        }
    }

    private async Task<List<TableUsage>> CollectTableUsageAsync(
        DbConnection connection, DatabaseUsageReport report, CancellationToken cancellationToken)
    {
        var tableNames = await ReadTableNamesAsync(connection, cancellationToken);
        var indexCounts = await ReadIndexCountsAsync(connection, cancellationToken);
        var results = new List<TableUsage>(tableNames.Count);

        foreach (var tableName in tableNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var usage = new TableUsage
            {
                TableName = tableName,
                IndexCount = indexCounts.GetValueOrDefault(tableName),
            };

            // 識別字含 U+0000 時無法安全地放進 SQL（C 字串會在該處截斷，語意會被悄悄改掉）。
            // SQLite 本身建不出這種名稱，但手動破壞的 schema 有可能，所以直接略過而非嘗試清洗。
            if (tableName.Contains('\0'))
            {
                usage.Note = "資料表名稱含不合法字元，已略過";
                report.Warnings.Add($"資料表名稱含不合法字元，已略過：{tableName.Replace("\0", "")}");
                results.Add(usage);
                continue;
            }

            // 逐表 try/catch：一張壞表不該毀掉整頁。
            try
            {
                var quoted = QuoteIdentifier(tableName);
                var columns = await ReadColumnNamesAsync(connection, quoted, cancellationToken);

                var (rowCount, byteSum) = await ReadCountAndByteSumAsync(
                    connection, quoted, columns, cancellationToken);

                usage.RowCount = rowCount;
                usage.EstimatedBytes = byteSum;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to measure table {TableName}.", tableName);
                usage.EstimatedBytes = null;
                usage.Note = $"估算失敗：{ex.GetType().Name}";
                report.Warnings.Add($"資料量估算失敗，已略過：{tableName}（{ex.GetType().Name}）。");

                // 估算失敗時仍試著把筆數拿回來，讓該列不至於整列空白。
                try
                {
                    usage.RowCount = await ReadScalarInt64Async(
                        connection, $"SELECT COUNT(*) FROM {QuoteIdentifier(tableName)};", cancellationToken);
                }
                catch (Exception countEx)
                {
                    logger.LogWarning(countEx, "Failed to count table {TableName}.", tableName);
                    usage.RowCount = null;
                }
            }

            results.Add(usage);
        }

        return results;
    }

    private static async Task<List<string>> ReadTableNamesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        // 不過濾 sqlite_% 或 __EF%：本頁報告的是「實體磁碟現況」，
        // __EFMigrationsHistory、__EFMigrationsLock、sqlite_sequence 同樣占用頁面，該列就要列。
        // type = 'table' 已排除 view／index／trigger。
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// 一次查回所有資料表的索引數，避免每張表各跑一次。
    /// 計入具名索引與 UNIQUE 產生的 sqlite_autoindex_*；
    /// 不計 rowid B-tree 與 INTEGER PRIMARY KEY（前者就是資料表本身，後者是 rowid 別名，皆不額外占空間）。
    /// </summary>
    private static async Task<Dictionary<string, int>> ReadIndexCountsAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tbl_name, COUNT(*) FROM sqlite_master WHERE type = 'index' GROUP BY tbl_name;";

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    private static async Task<List<string>> ReadColumnNamesAsync(
        DbConnection connection, string quotedTable, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({quotedTable});";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nameOrdinal = -1;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (nameOrdinal < 0)
            {
                nameOrdinal = reader.GetOrdinal("name");
            }

            columns.Add(reader.GetString(nameOrdinal));
        }

        return columns;
    }

    /// <summary>
    /// 一次掃描同時取回筆數與位元組加總（分兩次查詢會掃兩遍）。
    ///
    /// CAST(... AS BLOB) 是關鍵：SQLite 的 LENGTH(文字欄) 回傳的是「字元數」不是位元組數，
    /// 中文欄位會少算到三分之一（實測 '中文測試' → LENGTH 為 4、轉 BLOB 後為 12）。
    ///
    /// 每個欄位都要各自 COALESCE：NULL + 任何值仍是 NULL，只要一欄為 NULL 就會讓整列的加總變成
    /// NULL，而 SUM() 會直接跳過 NULL 列 —— 結果是「悄悄少算」而不是報錯。
    ///
    /// 已知不精確：INTEGER／REAL 轉 BLOB 會先經過文字表示，量到的是位數而非實際儲存的 1～8 位元組
    /// varint。這本來就是估算值，畫面上的免責段落已載明。不要改用 CASE typeof(...) 硬套固定長度 ——
    /// 那同樣是猜測，只是把 SQL 產生邏輯弄得更複雜。
    /// </summary>
    private static async Task<(long RowCount, long ByteSum)> ReadCountAndByteSumAsync(
        DbConnection connection, string quotedTable, List<string> columns, CancellationToken cancellationToken)
    {
        if (columns.Count == 0)
        {
            // SQLite 不可能有零欄位的資料表，但空的 SUM() 會是語法錯誤，還是擋一下。
            var onlyCount = await ReadScalarInt64Async(
                connection, $"SELECT COUNT(*) FROM {quotedTable};", cancellationToken);
            return (onlyCount, 0);
        }

        var expression = new StringBuilder();
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                expression.Append(" + ");
            }

            expression.Append($"COALESCE(LENGTH(CAST({QuoteIdentifier(columns[index])} AS BLOB)), 0)");
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*), COALESCE(SUM({expression}), 0) FROM {quotedTable};";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<long> ReadScalarInt64Async(
        DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0L : Convert.ToInt64(value);
    }

    /// <summary>
    /// SQLite 識別字跳脫：以雙引號包覆，內部的雙引號加倍。
    ///
    /// 表名與欄名雖來自 sqlite_master／PRAGMA 而非使用者輸入，仍必須跳脫 ——
    /// 否則保留字（Order、Group）或含空白的名稱會直接語法錯誤。
    /// </summary>
    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
