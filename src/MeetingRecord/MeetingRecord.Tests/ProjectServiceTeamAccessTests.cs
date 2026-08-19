using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

public sealed class ProjectServiceTeamAccessTests
{
    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.GetAsync(NewRequest());
        var titles = result.Result.Select(x => x.Title).OrderBy(x => x).ToList();

        // 公開（無團隊）與 團隊A 可見；團隊B 不可見
        Assert.Equal(["公開專案", "團隊A專案"], titles);
    }

    [Fact]
    public async Task GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(["公開專案"], result.Result.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task GetAsync_WithTeamFilter_ShouldFilterByTeam()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var request = NewRequest();
        request.TeamFilters = ["團隊B"];
        var result = await service.GetAsync(request);

        Assert.Equal(["團隊B專案"], result.Result.Select(x => x.Title).ToList());
    }

    [Fact]
    public async Task GetById_NonAdmin_ShouldDenyRecordOutsideTeamScope()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultProjectsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var denied = await service.GetAsync(ids["團隊B專案"]);
        var allowed = await service.GetAsync(ids["團隊A專案"]);

        Assert.Equal(0, denied.Id); // 守門回空模型
        Assert.Equal("團隊A專案", allowed.Title);
    }

    private static DataRequest NewRequest() => new()
    {
        Search = string.Empty,
        SortField = string.Empty,
        CurrentPage = 1,
        PageSize = 50,
        Take = 0,
    };

    private sealed class FakeScopeProvider(bool isAdmin, IReadOnlyList<string> teams) : IRecordAccessScopeProvider
    {
        public Task<RecordAccessScope> GetAsync() => Task.FromResult(new RecordAccessScope(isAdmin, teams));
    }

    private sealed class ProjectServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private ProjectServiceFixture(SqliteConnection connection, BackendDBContext context)
        {
            this.connection = connection;
            Context = context;

            loggerFactory = LoggerFactory.Create(_ => { });
            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();
        }

        public BackendDBContext Context { get; }

        public static async Task<ProjectServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new ProjectServiceFixture(connection, context);
        }

        public ProjectService CreateService(bool isAdmin, params string[] teams)
        {
            return new ProjectService(
                Context,
                mapper,
                loggerFactory.CreateLogger<ProjectService>(),
                Options.Create(new SystemSettings()),
                new FakeScopeProvider(isAdmin, teams));
        }

        public async Task<Dictionary<string, int>> SeedDefaultProjectsAsync()
        {
            var pub = NewProject("公開專案", null);
            var teamA = NewProject("團隊A專案", ["團隊A"]);
            var teamB = NewProject("團隊B專案", ["團隊B"]);

            Context.Project.AddRange(pub, teamA, teamB);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();

            return new Dictionary<string, int>
            {
                ["公開專案"] = pub.Id,
                ["團隊A專案"] = teamA.Id,
                ["團隊B專案"] = teamB.Id,
            };
        }

        private static Project NewProject(string title, IEnumerable<string>? teams) => new()
        {
            Title = title,
            Status = "未開始",
            Priority = "中",
            Owner = "tester",
            Teams = TagStringHelper.ToStored(teams),
        };

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
