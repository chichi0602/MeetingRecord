using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MeetingRecord.AccessDatas;
using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>
/// 資料庫用量服務測試。
///
/// 最重要的是「位元組而非字元」那一條：SQLite 的 LENGTH(文字欄) 回傳字元數，
/// 若少了 CAST(... AS BLOB)，中文資料會被少算到三分之一 —— 而且是靜默錯誤，
/// 畫面照樣顯示一個看起來合理的數字。
/// </summary>
public sealed class DatabaseUsageServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;

    public DatabaseUsageServiceTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(connection)
            .Options;
        context = new BackendDBContext(options);
    }

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

    private DatabaseUsageService CreateService()
        => new(context, NullLogger<DatabaseUsageService>.Instance);

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static TableUsage Find(DatabaseUsageReport report, string tableName)
        => Assert.Single(report.Tables, table => table.TableName == tableName);

    [Fact]
    public async Task Estimate_ChineseText_ShouldCountBytesNotCharacters()
    {
        // 「中」在 UTF-8 是 3 個位元組、1 個字元。
        // 若實作寫成 LENGTH(Name) 而非 LENGTH(CAST(Name AS BLOB))，這裡會拿到 1。
        Execute("CREATE TABLE T (Name TEXT);");
        Execute("INSERT INTO T (Name) VALUES ('中');");

        var report = await CreateService().GetReportAsync();

        Assert.Equal(3, Find(report, "T").EstimatedBytes);
    }

    [Fact]
    public async Task Estimate_NullColumn_ShouldNotPoisonRowSum()
    {
        // NULL + 任何值仍是 NULL，而 SUM() 會跳過 NULL 列 ——
        // 少了逐欄 COALESCE 就會靜默少算，這裡會拿到 0。
        Execute("CREATE TABLE T (A TEXT, B TEXT);");
        Execute("INSERT INTO T (A, B) VALUES ('abcd', NULL);");

        var report = await CreateService().GetReportAsync();

        Assert.Equal(4, Find(report, "T").EstimatedBytes);
    }

    [Fact]
    public async Task Estimate_EmptyTable_ShouldBeZeroNotNull()
    {
        Execute("CREATE TABLE T (Name TEXT);");

        var report = await CreateService().GetReportAsync();
        var table = Find(report, "T");

        Assert.Equal(0, table.RowCount);
        Assert.Equal(0, table.EstimatedBytes);
    }

    [Fact]
    public async Task RowCount_ShouldMatchInsertedRows()
    {
        Execute("CREATE TABLE T (Name TEXT);");
        Execute("INSERT INTO T (Name) VALUES ('a'), ('b'), ('c');");

        var report = await CreateService().GetReportAsync();

        Assert.Equal(3, Find(report, "T").RowCount);
    }

    [Fact]
    public async Task IndexCount_ShouldCountNamedAndUniqueIndexes()
    {
        // 具名索引 1 個，加上 UNIQUE 隱含產生的 sqlite_autoindex_* 1 個。
        Execute("CREATE TABLE T (Id INTEGER PRIMARY KEY, Code TEXT UNIQUE, Name TEXT);");
        Execute("CREATE INDEX IX_T_Name ON T (Name);");

        var report = await CreateService().GetReportAsync();

        Assert.Equal(2, Find(report, "T").IndexCount);
    }

    [Fact]
    public async Task TableName_WithEmbeddedQuote_ShouldNotBreakQuery()
    {
        // 識別字跳脫若沒把內部雙引號加倍，這裡會語法錯誤。
        Execute("CREATE TABLE \"we\"\"ird\" (\"co\"\"l\" TEXT);");
        Execute("INSERT INTO \"we\"\"ird\" (\"co\"\"l\") VALUES ('abcd');");

        var report = await CreateService().GetReportAsync();
        var table = Find(report, "we\"ird");

        Assert.Equal(1, table.RowCount);
        Assert.Equal(4, table.EstimatedBytes);
        Assert.Equal(string.Empty, table.Note);
    }

    [Fact]
    public async Task Tables_ShouldIncludeSystemTables()
    {
        // AUTOINCREMENT 會讓 SQLite 建出 sqlite_sequence，它同樣占用頁面，應一併列出。
        Execute("CREATE TABLE T (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT);");
        Execute("INSERT INTO T (Name) VALUES ('a');");

        var report = await CreateService().GetReportAsync();

        Assert.Contains(report.Tables, table => table.TableName == "sqlite_sequence");
    }

    [Fact]
    public async Task PageStatistics_ShouldBeReadable()
    {
        Execute("CREATE TABLE T (Name TEXT);");

        var report = await CreateService().GetReportAsync();

        Assert.True(report.PageSize > 0);
        Assert.True(report.PageCount > 0);
        Assert.True(report.FreelistCount >= 0);
        Assert.Equal(report.PageCount * report.PageSize, report.AllocatedBytes);
        Assert.Equal(report.FreelistCount * report.PageSize, report.ReclaimableBytes);
    }

    [Fact]
    public async Task InMemoryDatabase_ShouldDegradeGracefully()
    {
        // 記憶體資料庫沒有檔案，但頁面統計與逐表統計仍應可用。
        Execute("CREATE TABLE T (Name TEXT);");

        var report = await CreateService().GetReportAsync();

        Assert.Equal(0, report.MainDbBytes);
        Assert.Equal(0, report.TotalOnDiskBytes);
        Assert.False(string.IsNullOrWhiteSpace(report.Message));
        Assert.Equal(string.Empty, report.ErrorMessage);
        Assert.NotEmpty(report.Tables);
    }

    [Fact]
    public async Task Report_ShouldRecordElapsedTime()
    {
        Execute("CREATE TABLE T (Name TEXT);");

        var report = await CreateService().GetReportAsync();

        Assert.True(report.ElapsedMilliseconds >= 0);
    }
}
