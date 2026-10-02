using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 清單上點狀態膠囊直接切換啟用／停用（0.4.105）：使用者、團隊、分類。提示詞範本的版本在 PromptTemplateServiceTests。
/// </summary>
public sealed class StatusToggleTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
    private readonly IMapper mapper;

    public StatusToggleTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
    }

    [Fact]
    public async Task MyUser_SetStatus_ShouldPersist_AndRejectMissing()
    {
        var user = new MyUser { Account = "alice", Name = "alice", Password = "x", Status = true };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = new MyUserService(
            context,
            mapper,
            loggerFactory.CreateLogger<MyUserService>(),
            new RbacWriteService(context),
            new AuditLogService(context, loggerFactory.CreateLogger<AuditLogService>()),
            new CurrentUserService());

        Assert.True((await service.SetStatusAsync(user.Id, false)).Success);
        Assert.False((await context.MyUser.AsNoTracking().SingleAsync()).Status);
        Assert.False((await service.SetStatusAsync(999, true)).Success);
    }

    [Fact]
    public async Task Team_SetEnabled_ShouldPersist_AndRejectMissing()
    {
        var team = new Team { Name = "研發部", IsEnabled = true };
        context.Team.Add(team);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = new TeamService(context, mapper, loggerFactory.CreateLogger<TeamService>(), TestProjectAccess.Admin(context));

        Assert.True((await service.SetEnabledAsync(team.Id, false)).Success);
        Assert.False((await context.Team.AsNoTracking().SingleAsync()).IsEnabled);
        Assert.False((await service.SetEnabledAsync(999, true)).Success);
    }

    [Fact]
    public async Task Category_SetEnabled_ShouldPersist_AndRejectMissing()
    {
        var category = new Category { Name = ".NET", IsEnabled = false };
        context.Category.Add(category);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = new CategoryService(context, mapper, loggerFactory.CreateLogger<CategoryService>());

        Assert.True((await service.SetEnabledAsync(category.Id, true)).Success);
        Assert.True((await context.Category.AsNoTracking().SingleAsync()).IsEnabled);
        Assert.False((await service.SetEnabledAsync(999, true)).Success);
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
        loggerFactory.Dispose();
    }
}
