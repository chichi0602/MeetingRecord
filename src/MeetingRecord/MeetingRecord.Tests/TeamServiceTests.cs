using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

public sealed class TeamServiceTests
{
    [Fact]
    public async Task BeforeAddCheckAsync_WithUniqueNameAndCode_ShouldSucceed()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "研發部", Code = "RD" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateName_ShouldFail()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "研發部", Code = "RD2" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateCode_ShouldFail()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "業務部", Code = "RD" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithEmptyCode_ShouldSucceedEvenIfAnotherEmptyCodeExists()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", null);
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new TeamAdapterModel { Name = "業務部", Code = null });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithSameRecord_ShouldSucceed()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var existing = await fixture.AddTeamAsync("研發部", "RD");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new TeamAdapterModel { Id = existing.Id, Name = "研發部", Code = "RD" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithCodeUsedByOtherRecord_ShouldFail()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        await fixture.AddTeamAsync("研發部", "RD");
        var other = await fixture.AddTeamAsync("業務部", "SALES");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new TeamAdapterModel { Id = other.Id, Name = "業務部", Code = "RD" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AddAsync_ShouldPersistTeam()
    {
        await using var fixture = await TeamServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AddAsync(new TeamAdapterModel { Name = "研發部", Code = "RD", IsEnabled = true });

        Assert.True(result.Success);
        var saved = await fixture.Context.Team.AsNoTracking().SingleAsync(x => x.Name == "研發部");
        Assert.Equal("RD", saved.Code);
        Assert.True(saved.IsEnabled);
    }

    private sealed class TeamServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private TeamServiceFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<TeamServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new TeamServiceFixture(connection, context);
        }

        public TeamService CreateService()
        {
            return new TeamService(
                Context,
                mapper,
                loggerFactory.CreateLogger<TeamService>());
        }

        public async Task<Team> AddTeamAsync(string name, string? code)
        {
            var team = new Team { Name = name, Code = code };
            Context.Team.Add(team);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return team;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
