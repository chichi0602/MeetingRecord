using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;

namespace MeetingRecord.Tests;

/// <summary>
/// 守住「模型改了就要產生 migration」。
///
/// <para>
/// ⚠️ 這個 repo 原本抓不到這件事：**全部的測試 fixture 都用
/// <c>Database.EnsureCreatedAsync()</c>**（照目前的模型直接建表，完全繞過 migration），
/// 而正式啟動走的是 <c>Database.Migrate()</c>（<c>Program.cs:295</c>）。
/// 所以忘了產 migration 時，整套測試會全綠，直到有人跑 App 才在啟動時被
/// EF 10 的 <c>PendingModelChangesWarning</c> 擋下來——而那時多半已經 commit 出去了。
/// </para>
///
/// <para>
/// 這一筆就是那個缺口的補丁。加欄位、改型別、改索引而沒有 <c>dotnet ef migrations add</c>，
/// 它會直接變紅並印出缺哪些操作。
/// </para>
/// </summary>
public sealed class MigrationSnapshotTests
{
    [Fact]
    public void ModelSnapshot_ShouldMatchCurrentModel()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<BackendDBContext>()
            .UseSqlite(connection)
            .Options;

        using var context = new BackendDBContext(options);

        var snapshotModel = context.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;
        Assert.NotNull(snapshotModel);

        // 快照裡的模型是「設計階段」的形狀，必須先跑過 runtime initializer 才能與目前的模型比較。
        if (snapshotModel is IMutableModel mutable)
        {
            snapshotModel = context.GetService<IModelRuntimeInitializer>()
                .Initialize(mutable.FinalizeModel(), designTime: true, validationLogger: null);
        }

        var differ = context.GetService<IMigrationsModelDiffer>();
        var operations = differ.GetDifferences(
            snapshotModel.GetRelationalModel(),
            context.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(
            operations.Count == 0,
            $"""
            模型與 migration 快照不一致，缺少 {operations.Count} 個操作：
            {string.Join(Environment.NewLine, operations.Select(x => "  - " + x.GetType().Name))}

            請執行：
              dotnet ef migrations add <名稱> --project MeetingRecord.AccessDatas --startup-project MeetingRecord.Web
            """);
    }
}
