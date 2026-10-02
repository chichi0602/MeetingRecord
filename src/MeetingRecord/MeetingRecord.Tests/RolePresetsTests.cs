using System.Text.Json;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 角色管理「建立預設角色」（0.4.108）：上線時系統是空的，一鍵建出四個角色並設好權限。
/// </summary>
public sealed class RolePresetsTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
    private readonly IMapper mapper;

    public RolePresetsTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
    }

    private RoleViewService CreateService()
        => new(
            context,
            mapper,
            loggerFactory.CreateLogger<RoleViewService>(),
            new RolePermissionService(),
            new RbacWriteService(context),
            new AuditLogService(context, loggerFactory.CreateLogger<AuditLogService>()),
            new CurrentUserService());

    private async Task<List<string>> PermissionKeysOfAsync(string roleName)
        => await context.RolePermissionMap.AsNoTracking()
            .Where(m => m.RoleView!.Name == roleName)
            .Join(context.Permission, m => m.PermissionId, p => p.Id, (m, p) => p.Key)
            .ToListAsync();

    [Fact]
    public async Task AddPresets_OnEmptyDatabase_ShouldCreateFourRolesWithPermissions()
    {
        var result = await CreateService().AddPresetsAsync();

        Assert.True(result.Success);
        Assert.Equal(
            RolePresets.All.Select(x => x.Name).Order().ToList(),
            (await context.RoleView.AsNoTracking().Select(x => x.Name).ToListAsync()).Order().ToList());

        // 一般使用者：業務頁可以檢視、新增、修改、匯出，不能刪除。
        var general = await PermissionKeysOfAsync(MagicObjectHelper.預設角色);
        Assert.Contains(PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit), general);
        Assert.DoesNotContain(PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Delete), general);
        Assert.DoesNotContain(MagicObjectHelper.角色_使用者管理, general);

        // 檢視者只有檢視。
        var viewer = await PermissionKeysOfAsync("檢視者");
        Assert.Contains(PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.View), viewer);
        Assert.DoesNotContain(PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.Create), viewer);

        // 主管多了分類清單與團隊清單。
        var manager = await PermissionKeysOfAsync("主管");
        Assert.Contains(MagicObjectHelper.角色_團隊清單, manager);
        Assert.Contains(MagicObjectHelper.角色_分類清單, manager);

        // 管理者角色有全部頁面。
        var admin = await PermissionKeysOfAsync("管理者");
        Assert.Equal(new RolePermissionService().GetRolePermissionAllName().Distinct().Count(), admin.Distinct().Count());

        // 角色編輯畫面靠 TabViewJson 回填勾選。
        var stored = await context.RoleView.AsNoTracking().SingleAsync(x => x.Name == "檢視者");
        Assert.Equal(RolePresets.All.Single(x => x.Name == "檢視者").PermissionKeys, JsonSerializer.Deserialize<List<string>>(stored.TabViewJson));
    }

    [Fact]
    public async Task AddPresets_ExistingSameName_ShouldSkipAndNotOverwrite()
    {
        var custom = new RoleView { Name = "主管", TabViewJson = "[]" };
        context.RoleView.Add(custom);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        await new RbacWriteService(context).SyncRolePermissionsAsync(custom.Id, [MagicObjectHelper.角色_使用說明]);

        var first = await CreateService().AddPresetsAsync();
        var second = await CreateService().AddPresetsAsync();

        Assert.Contains("略過 1 個", first.Message);
        Assert.Contains("已新增 0 個", second.Message);
        Assert.Equal(4, await context.RoleView.CountAsync());
        Assert.Equal([MagicObjectHelper.角色_使用說明], await PermissionKeysOfAsync("主管"));
    }

    [Fact]
    public void GeneralUserInitialPermissions_ShouldComeFromPreset()
    {
        Assert.Equal(
            RolePresets.All.Single(x => x.Name == MagicObjectHelper.預設角色).PermissionKeys,
            new RolePermissionService().GetGeneralUserPermissionNames());
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
        loggerFactory.Dispose();
    }
}
