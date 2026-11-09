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
/// 清單分頁回歸測試（1.0.57）：分類、團隊、使用者、角色的 <c>GetAsync(DataRequest)</c>
/// 原本把 Take 包在 <c>if (dataRequest.Take != 0)</c> 裡，而清單頁一律傳 0，
/// 等於從來沒有分頁——第 1 頁吐出全部資料。會議與專案的同一組測試在
/// <see cref="MeetingServiceTests"/>、<see cref="ProjectAccessTests"/>（要用它們的 fixture 設可見範圍）。
/// </summary>
public sealed class ListPagingTests : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly BackendDBContext context;
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });
    private readonly IMapper mapper;

    public ListPagingTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), loggerFactory).CreateMapper();
    }

    public static TheoryData<string> Lists => ["分類", "團隊", "使用者", "角色"];

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task GetAsync_WithFirstPage_ShouldNotReturnAllRecords(string list)
    {
        var total = await SeedAsync(list, 10);

        var (pageCount, count) = await GetPageAsync(list, currentPage: 1, pageSize: 4);

        Assert.Equal(4, pageCount);
        Assert.Equal(total, count);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task GetAsync_WithSecondPage_ShouldReturnOnlyPageSizeRecords(string list)
    {
        var total = await SeedAsync(list, 10);

        var (pageCount, count) = await GetPageAsync(list, currentPage: 2, pageSize: 4);

        Assert.Equal(4, pageCount);
        // Count 在 Skip/Take 之前算，是過濾後的總數而不是本頁筆數。
        Assert.Equal(total, count);
    }

    [Theory]
    [MemberData(nameof(Lists))]
    public async Task GetAsync_WithPageBeyondLastPage_ShouldReturnEmptyResultWithFullCount(string list)
    {
        // 頁碼越界回空集合但 Count 仍是總數——畫面靠這個組合把頁碼夾回最後一頁。
        var total = await SeedAsync(list, 5);

        var (pageCount, count) = await GetPageAsync(list, currentPage: 3, pageSize: 4);

        Assert.Equal(0, pageCount);
        Assert.Equal(total, count);
    }

    /// <summary>種 <paramref name="rows"/> 筆資料，回傳該清單的總筆數。</summary>
    private async Task<int> SeedAsync(string list, int rows)
    {
        for (var index = 0; index < rows; index++)
        {
            switch (list)
            {
                case "分類":
                    context.Category.Add(new Category { Name = $"分類{index:00}" });
                    break;
                case "團隊":
                    context.Team.Add(new Team { Name = $"團隊{index:00}" });
                    break;
                case "使用者":
                    context.MyUser.Add(new MyUser { Account = $"user{index:00}", Name = $"使用者{index:00}", Password = "x", Status = true });
                    break;
                case "角色":
                    context.RoleView.Add(new RoleView { Name = $"角色{index:00}" });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(list), list, null);
            }
        }

        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return rows;
    }

    private async Task<(int PageCount, int Count)> GetPageAsync(string list, int currentPage, int pageSize)
    {
        var request = new DataRequest
        {
            Search = string.Empty,
            SortField = string.Empty,
            CurrentPage = currentPage,
            PageSize = pageSize,
            Take = 0,
        };

        switch (list)
        {
            case "分類":
            {
                var result = await new CategoryService(context, mapper, loggerFactory.CreateLogger<CategoryService>(), TestAuditLogs.CreateService(context))
                    .GetAsync(request);
                return (result.Result.Count(), result.Count);
            }
            case "團隊":
            {
                var result = await new TeamService(context, mapper, loggerFactory.CreateLogger<TeamService>(), TestProjectAccess.Admin(context), TestAuditLogs.CreateService(context))
                    .GetAsync(request);
                return (result.Result.Count(), result.Count);
            }
            case "使用者":
            {
                var result = await new MyUserService(context, mapper, loggerFactory.CreateLogger<MyUserService>(), new RbacWriteService(context), TestAuditLogs.CreateService(context), new CurrentUserService())
                    .GetAsync(request);
                return (result.Result.Count(), result.Count);
            }
            case "角色":
            {
                var result = await new RoleViewService(context, mapper, loggerFactory.CreateLogger<RoleViewService>(), new RolePermissionService(), new RbacWriteService(context), TestAuditLogs.CreateService(context), new CurrentUserService())
                    .GetAsync(request);
                return (result.Result.Count(), result.Count);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(list), list, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
        loggerFactory.Dispose();
    }
}
