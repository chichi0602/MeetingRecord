using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Migrations.Sql;
using MeetingRecord.AccessDatas.Models;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.0.52 migration 的負責人回填：手打的負責人文字去頭尾空白後，與帳號的姓名或帳號完全相同、
/// 而且剛好只對到一個帳號才填 <c>OwnerUserId</c>；對不到或對到好幾個就留 null（不猜）。
/// </summary>
public sealed class ProjectOwnerBackfillSqlTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;

    public ProjectOwnerBackfillSqlTests()
    {
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Backfill_ShouldMatchByNameOrAccount_OnlyWhenExactlyOneUser()
    {
        var wang = await AddUserAsync("wang", "王小明");
        await AddUserAsync("chen1", "陳大文");
        await AddUserAsync("chen2", "陳大文");

        var byName = await AddProjectAsync(" 王小明 ");
        var byAccount = await AddProjectAsync("wang");
        var ambiguous = await AddProjectAsync("陳大文");
        var unknown = await AddProjectAsync("外包廠商");
        var blank = await AddProjectAsync("");

        await context.Database.ExecuteSqlRawAsync(ProjectOwnerBackfill.Sql);

        Assert.Equal(wang, await OwnerOfAsync(byName));
        Assert.Equal(wang, await OwnerOfAsync(byAccount));
        Assert.Null(await OwnerOfAsync(ambiguous));
        Assert.Null(await OwnerOfAsync(unknown));
        Assert.Null(await OwnerOfAsync(blank));
    }

    private async Task<int> AddUserAsync(string account, string name)
    {
        var user = new MyUser { Account = account, Name = name, Password = "x", Status = true };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return user.Id;
    }

    private async Task<int> AddProjectAsync(string owner)
    {
        var project = new Project { Title = "專案", Status = "進行中", Owner = owner };
        context.Project.Add(project);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return project.Id;
    }

    private Task<int?> OwnerOfAsync(int projectId)
        => context.Project.AsNoTracking().Where(x => x.Id == projectId).Select(x => x.OwnerUserId).SingleAsync();
}
