using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

public sealed class PromptTemplateServiceTests
{
    #region 名稱唯一性

    [Fact]
    public async Task BeforeAddCheckAsync_WithUniqueName_ShouldSucceed()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.AddPromptAsync("會議摘要");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(NewModel("決議事項"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateName_ShouldFail()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.AddPromptAsync("會議摘要");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(NewModel("會議摘要"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeAddCheckAsync_WithDuplicateNameDifferentCase_ShouldFail()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.AddPromptAsync("Meeting Summary");
        var service = fixture.CreateService();

        var result = await service.BeforeAddCheckAsync(NewModel("meeting summary"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithSameRecordSameName_ShouldSucceed()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var existing = await fixture.AddPromptAsync("會議摘要");
        var service = fixture.CreateService();

        var model = NewModel("會議摘要");
        model.Id = existing.Id;
        var result = await service.BeforeUpdateCheckAsync(model);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task BeforeUpdateCheckAsync_WithNameUsedByOtherRecord_ShouldFail()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var first = await fixture.AddPromptAsync("會議摘要");
        await fixture.AddPromptAsync("決議事項");
        var service = fixture.CreateService();

        var model = NewModel("決議事項");
        model.Id = first.Id;
        var result = await service.BeforeUpdateCheckAsync(model);

        Assert.False(result.Success);
    }

    #endregion

    #region 資料往返與標籤字串轉換

    [Fact]
    public async Task AddAsync_ShouldPersistContentAndTagStrings()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var model = NewModel("會議摘要");
        model.Content = "請依 {{transcript}} 產生會議紀錄。";
        model.Description = "標準摘要範本";
        model.Categories = ["會議"];
        model.Teams = ["團隊A"];

        var result = await service.AddAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Name == "會議摘要");
        Assert.Equal("請依 {{transcript}} 產生會議紀錄。", saved.Content);
        Assert.Equal("標準摘要範本", saved.Description);
        Assert.True(saved.IsEnabled);
        // 標籤欄位必須經 TagStringHelper 轉為「以換行包夾」的儲存字串，
        // 否則團隊列級權控的 Contains 比對會全面失效。
        Assert.Equal(TagStringHelper.ToStored(["會議"]), saved.Categories);
        Assert.Equal(TagStringHelper.ToStored(["團隊A"]), saved.Teams);
    }

    [Fact]
    public async Task GetAsync_ById_ShouldRoundTripTagsToList()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var existing = await fixture.AddPromptAsync("會議摘要", categories: ["會議", "週報"], teams: ["團隊A"]);
        var service = fixture.CreateService();

        var model = await service.GetAsync(existing.Id);

        Assert.Equal(["會議", "週報"], model.Categories);
        Assert.Equal("團隊A", model.TeamsText);
    }

    [Fact]
    public async Task UpdateAsync_ShouldReplaceTagsAndKeepCreatedAt()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var existing = await fixture.AddPromptAsync("會議摘要", teams: ["團隊A"]);
        var originalCreatedAt = existing.CreatedAt;
        var service = fixture.CreateService();

        var model = await service.GetAsync(existing.Id);
        model.Teams = ["團隊B"];
        model.Content = "更新後的提示詞內容";
        var result = await service.UpdateAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(TagStringHelper.ToStored(["團隊B"]), saved.Teams);
        Assert.Equal("更新後的提示詞內容", saved.Content);
        Assert.Equal(originalCreatedAt, saved.CreatedAt);
        Assert.True(saved.UpdatedAt >= originalCreatedAt);
    }

    [Fact]
    public async Task AddAsync_WithMultiKilobyteContent_ShouldPersistIntact()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var longContent = new string('提', 8192);
        var model = NewModel("長提示詞");
        model.Content = longContent;

        var result = await service.AddAsync(model);

        Assert.True(result.Success);
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Name == "長提示詞");
        Assert.Equal(8192, saved.Content.Length);
        Assert.Equal(longContent, saved.Content);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveRecord()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var existing = await fixture.AddPromptAsync("會議摘要");
        var service = fixture.CreateService();

        var result = await service.DeleteAsync(existing.Id);

        Assert.True(result.Success);
        Assert.False(await fixture.Context.PromptTemplate.AsNoTracking().AnyAsync(x => x.Id == existing.Id));
    }

    [Fact]
    public async Task GetAllEnabledNamesAsync_ShouldReturnOnlyEnabledOrderedByName()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.AddPromptAsync("乙範本");
        await fixture.AddPromptAsync("甲範本");
        await fixture.AddPromptAsync("停用範本", isEnabled: false);
        var service = fixture.CreateService();

        var names = await service.GetAllEnabledNamesAsync();

        Assert.Equal(["乙範本", "甲範本"], names);
    }

    #endregion

    #region 團隊列級權控

    [Fact]
    public async Task GetAsync_Admin_ShouldSeeAllRecords()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.SeedDefaultPromptsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetAsync_NonAdmin_ShouldSeeOnlyPublicOrIntersectingTeamRecords()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.SeedDefaultPromptsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.GetAsync(NewRequest());
        var names = result.Result.Select(x => x.Name).OrderBy(x => x).ToList();

        // 公開（無團隊）與 團隊A 可見；團隊B 不可見
        Assert.Equal(["公開提示詞", "團隊A提示詞"], names);
    }

    [Fact]
    public async Task GetAsync_NonAdminWithoutTeams_ShouldSeeOnlyPublicRecords()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.SeedDefaultPromptsAsync();
        var service = fixture.CreateService(isAdmin: false);

        var result = await service.GetAsync(NewRequest());

        Assert.Equal(["公開提示詞"], result.Result.Select(x => x.Name).ToList());
    }

    [Fact]
    public async Task GetById_NonAdmin_ShouldDenyRecordOutsideTeamScope()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var ids = await fixture.SeedDefaultPromptsAsync();
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var denied = await service.GetAsync(ids["團隊B提示詞"]);
        var allowed = await service.GetAsync(ids["團隊A提示詞"]);

        Assert.Equal(0, denied.Id); // 守門回空模型
        Assert.Equal("團隊A提示詞", allowed.Name);
    }

    [Fact]
    public async Task GetAsync_WithTeamFilter_ShouldFilterByTeam()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.SeedDefaultPromptsAsync();
        var service = fixture.CreateService(isAdmin: true);

        var request = NewRequest();
        request.TeamFilters = ["團隊B"];
        var result = await service.GetAsync(request);

        Assert.Equal(["團隊B提示詞"], result.Result.Select(x => x.Name).ToList());
    }

    #endregion

    #region 搜尋

    [Fact]
    public async Task GetAsync_WithKeyword_ShouldMatchContent()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        await fixture.AddPromptAsync("甲範本", content: "請整理決議事項");
        await fixture.AddPromptAsync("乙範本", content: "請整理待辦清單");
        var service = fixture.CreateService();

        var request = NewRequest();
        request.Search = "決議";
        var result = await service.GetAsync(request);

        Assert.Equal(["甲範本"], result.Result.Select(x => x.Name).ToList());
    }

    #endregion

    #region 啟用狀態切換

    [Fact]
    public async Task SetEnabledAsync_WithEnabledRecord_ShouldDisableAndTouchUpdatedAt()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();
        var prompt = await fixture.AddPromptAsync("標準會議紀錄");
        var originalUpdatedAt = prompt.UpdatedAt;

        var result = await service.SetEnabledAsync(prompt.Id, false);

        Assert.True(result.Success);
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Id == prompt.Id);
        Assert.False(saved.IsEnabled);
        // UpdatedAt 要被動到：清單預設按它遞減排序，不動的話剛切換過的那筆不會浮上來。
        Assert.True(saved.UpdatedAt >= originalUpdatedAt);
    }

    [Fact]
    public async Task SetEnabledAsync_WithDisabledRecord_ShouldEnable()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();
        var prompt = await fixture.AddPromptAsync("停用中的範本", isEnabled: false);

        var result = await service.SetEnabledAsync(prompt.Id, true);

        Assert.True(result.Success);
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Id == prompt.Id);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_WithMissingId_ShouldFail()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.SetEnabledAsync(9999, false);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task SetEnabledAsync_NonAdminOutsideTeamScope_ShouldDenyAndKeepOriginalValue()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var prompt = await fixture.AddPromptAsync("團隊B提示詞", teams: ["團隊B"]);
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.SetEnabledAsync(prompt.Id, false);

        Assert.False(result.Success);
        // 一定要斷言資料庫的值沒變，不能只看 Success：若不小心加了 AsNoTracking，
        // 這裡會變成「回傳成功但沒寫入」，只驗 Success 的測試抓不到。
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Id == prompt.Id);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_NonAdminWithPublicRecord_ShouldSucceed()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var prompt = await fixture.AddPromptAsync("公開提示詞");
        var service = fixture.CreateService(isAdmin: false, "團隊A");

        var result = await service.SetEnabledAsync(prompt.Id, false);

        Assert.True(result.Success);
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Id == prompt.Id);
        Assert.False(saved.IsEnabled);
    }

    #endregion

    #region 內建範本

    [Fact]
    public async Task AddPresetsAsync_OnEmptyDatabase_ShouldCreateAllPresets()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AddPresetsAsync();

        Assert.True(result.Success);
        var saved = await fixture.Context.PromptTemplate.AsNoTracking().Select(x => x.Name).ToListAsync();
        Assert.Equal(
            PromptTemplatePresets.All.Select(x => x.Name).Order(StringComparer.Ordinal),
            saved.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AddPresetsAsync_ShouldCreateEnabledPublicRecords()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddPresetsAsync();

        var saved = await fixture.Context.PromptTemplate.AsNoTracking().ToListAsync();
        Assert.All(saved, item =>
        {
            Assert.True(item.IsEnabled);
            // 不掛團隊等於公開：一鍵建立出來的範本必須所有人都看得到，
            // 否則新使用者按了按鈕卻還是空清單。
            Assert.Null(item.Teams);
            Assert.Null(item.Categories);
            Assert.Contains("{{transcript}}", item.Content);
        });
    }

    [Fact]
    public async Task AddPresetsAsync_RunTwice_ShouldNotCreateDuplicates()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddPresetsAsync();
        var second = await service.AddPresetsAsync();

        Assert.True(second.Success);
        Assert.Equal(PromptTemplatePresets.All.Count, await fixture.Context.PromptTemplate.CountAsync());
    }

    [Fact]
    public async Task AddPresetsAsync_WithExistingPresetName_ShouldSkipOnlyThatPreset()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        var existingName = PromptTemplatePresets.All[0].Name;
        await fixture.AddPromptAsync(existingName, content: "使用者自己改過的內容");
        var service = fixture.CreateService();

        await service.AddPresetsAsync();

        Assert.Equal(PromptTemplatePresets.All.Count, await fixture.Context.PromptTemplate.CountAsync());
        // 已存在那筆不能被覆寫——使用者可能已經改過內容。
        var kept = await fixture.Context.PromptTemplate.AsNoTracking().SingleAsync(x => x.Name == existingName);
        Assert.Equal("使用者自己改過的內容", kept.Content);
    }

    [Fact]
    public async Task AddPresetsAsync_WithExistingNameDifferentCase_ShouldSkipThatPreset()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        // 名稱唯一性不分大小寫，所以冪等判斷也必須不分大小寫，否則會建出一筆撞名的資料，
        // 讓後續的名稱檢查行為變得不一致。這裡用英文名稱才驗得到大小寫。
        var presetName = PromptTemplatePresets.All[0].Name;
        await fixture.AddPromptAsync(presetName.ToUpperInvariant());
        var service = fixture.CreateService();

        await service.AddPresetsAsync();

        Assert.Equal(PromptTemplatePresets.All.Count, await fixture.Context.PromptTemplate.CountAsync());
    }

    #endregion

    #region 分頁

    [Fact]
    public async Task GetAsync_WithFirstPage_ShouldNotReturnAllRecords()
    {
        // 回歸測試：Take 原本綁 dataRequest.Take != 0，而呼叫端一律傳 0，
        // 等於從來沒有分頁——第 1 頁會把全部資料一次吐出來。
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        for (var index = 0; index < 10; index++)
        {
            await fixture.AddPromptAsync($"範本{index:00}");
        }

        var service = fixture.CreateService();
        var request = NewRequest();
        request.PageSize = 4;

        var result = await service.GetAsync(request);

        Assert.Equal(4, result.Result.Count());
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public async Task GetAsync_WithSecondPage_ShouldReturnOnlyPageSizeRecords()
    {
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        for (var index = 0; index < 10; index++)
        {
            await fixture.AddPromptAsync($"範本{index:00}");
        }

        var service = fixture.CreateService();
        var request = NewRequest();
        request.PageSize = 4;
        request.CurrentPage = 2;

        var result = await service.GetAsync(request);

        Assert.Equal(4, result.Result.Count());
        // Count 是在 Skip/Take 之前算的，所以它一律是過濾後的總數而不是本頁筆數。
        Assert.Equal(10, result.Count);
    }

    [Fact]
    public async Task GetAsync_WithPageBeyondLastPage_ShouldReturnEmptyResultWithFullCount()
    {
        // 頁碼越界會回空集合但 Count 仍是總數——畫面端要靠這個組合把頁碼夾回最後一頁，
        // 否則刪掉最後一頁唯一一筆之後會停在空白表格。
        await using var fixture = await PromptTemplateServiceFixture.CreateAsync();
        for (var index = 0; index < 5; index++)
        {
            await fixture.AddPromptAsync($"範本{index:00}");
        }

        var service = fixture.CreateService();
        var request = NewRequest();
        request.PageSize = 4;
        request.CurrentPage = 3;

        var result = await service.GetAsync(request);

        Assert.Empty(result.Result);
        Assert.Equal(5, result.Count);
    }

    #endregion

    private static PromptTemplateAdapterModel NewModel(string name) => new()
    {
        Name = name,
        Content = "請依會議逐字稿產生會議紀錄。",
    };

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

    private sealed class PromptTemplateServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly IMapper mapper;
        private readonly ILoggerFactory loggerFactory;

        private PromptTemplateServiceFixture(SqliteConnection connection, BackendDBContext context)
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

        public static async Task<PromptTemplateServiceFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<BackendDBContext>()
                .UseSqlite(connection)
                .Options;

            var context = new BackendDBContext(options);
            await context.Database.EnsureCreatedAsync();

            return new PromptTemplateServiceFixture(connection, context);
        }

        public PromptTemplateService CreateService(bool isAdmin = true, params string[] teams)
        {
            return new PromptTemplateService(
                Context,
                mapper,
                loggerFactory.CreateLogger<PromptTemplateService>(),
                new FakeScopeProvider(isAdmin, teams));
        }

        public async Task<PromptTemplate> AddPromptAsync(
            string name,
            string content = "請依會議逐字稿產生會議紀錄。",
            bool isEnabled = true,
            IEnumerable<string>? categories = null,
            IEnumerable<string>? teams = null)
        {
            var promptTemplate = new PromptTemplate
            {
                Name = name,
                Content = content,
                IsEnabled = isEnabled,
                Categories = TagStringHelper.ToStored(categories),
                Teams = TagStringHelper.ToStored(teams),
            };

            Context.PromptTemplate.Add(promptTemplate);
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return promptTemplate;
        }

        public async Task<Dictionary<string, int>> SeedDefaultPromptsAsync()
        {
            var pub = await AddPromptAsync("公開提示詞");
            var teamA = await AddPromptAsync("團隊A提示詞", teams: ["團隊A"]);
            var teamB = await AddPromptAsync("團隊B提示詞", teams: ["團隊B"]);

            return new Dictionary<string, int>
            {
                ["公開提示詞"] = pub.Id,
                ["團隊A提示詞"] = teamA.Id,
                ["團隊B提示詞"] = teamB.Id,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();
        }
    }
}
