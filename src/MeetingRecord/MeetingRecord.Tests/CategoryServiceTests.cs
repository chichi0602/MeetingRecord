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

public sealed class CategoryServiceTests
{
    [Fact]
    public async Task BeforeAddCheckAsync_WithUniqueName_ShouldSucceed()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "技術文件" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateName_ShouldFail()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        await fixture.AddCategoryAsync("技術文件");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "技術文件" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateNameDifferentCase_ShouldFail()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        await fixture.AddCategoryAsync("Report");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(new CategoryAdapterModel { Name = "report" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithSameRecordSameName_ShouldSucceed()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var existing = await fixture.AddCategoryAsync("技術文件");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new CategoryAdapterModel { Id = existing.Id, Name = "技術文件" });

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithNameUsedByOtherRecord_ShouldFail()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        await fixture.AddCategoryAsync("技術文件");
        var other = await fixture.AddCategoryAsync("會議紀錄");
        var service = fixture.CreateService();

        var result = await service.BeforeUpdateCheckAsync(new CategoryAdapterModel { Id = other.Id, Name = "技術文件" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task AddAsync_ShouldPersistCategory()
    {
        await using var fixture = await CategoryServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AddAsync(new CategoryAdapterModel { Name = "技術文件", Description = "說明", IsEnabled = true });

        Assert.True(result.Success);
        var saved = await fixture.Context.Category.AsNoTracking().SingleAsync(x => x.Name == "技術文件");
        Assert.Equal("說明", saved.Description);
        Assert.True(saved.IsEnabled);
    }

    private sealed class CategoryServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private CategoryServiceFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<CategoryServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new CategoryServiceFixture(connection, context);
        }

        public CategoryService CreateService()
        {
            return new CategoryService(
                Context,
                mapper,
                loggerFactory.CreateLogger<CategoryService>());
        }

        public async Task<Category> AddCategoryAsync(string name)
        {
            var category = new Category { Name = name };
            Context.Category.Add(category);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return category;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
