using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 專案服務的測試。
///
/// ⚠️ 這個檔案是 0.4.71 才建的，先前<b>整個專案沒有任何 Project 的服務測試</b>——
/// 而 <c>UpdateAsync</c> 是手抄欄位、不走 Mapper 的，新增欄位時只改 AutoMapping
/// 會變成「新增存得進去、修改存不進去」而且不報錯。這裡主要就是守這件事。
/// </summary>
public sealed class ProjectServiceTests
{
    #region 常用名詞與與會人員名冊

    [Fact]
    public async Task AddAsync_ShouldPersistGlossaryAndParticipants()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        var result = await service.AddAsync(NewProject(
            glossaryTerms: ["甲專案", "乙系統"],
            participants: ["王小明", "陳大文"]));

        Assert.True(result.Success);

        var saved = await fixture.Context.Project.AsNoTracking().FirstAsync();
        Assert.Equal("\n甲專案\n乙系統\n", saved.GlossaryTerms);
        Assert.Equal("\n王小明\n陳大文\n", saved.Participants);
    }

    [Fact]
    public async Task UpdateAsync_ShouldPersistGlossaryAndParticipants()
    {
        // ⚠️ 這是核心回歸測試。UpdateAsync 手抄欄位、不走 Mapper——
        // 只加 ForMember 的話這裡會失敗（而畫面上看起來像存成功了）。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(NewProject());
        var saved = await fixture.Context.Project.AsNoTracking().FirstAsync();

        var edit = await service.GetAsync(saved.Id);
        edit.GlossaryTerms = ["甲專案"];
        edit.Participants = ["王小明"];

        var result = await service.UpdateAsync(edit);

        Assert.True(result.Success);

        var updated = await fixture.Context.Project.AsNoTracking().FirstAsync(x => x.Id == saved.Id);
        Assert.Equal("\n甲專案\n", updated.GlossaryTerms);
        Assert.Equal("\n王小明\n", updated.Participants);
    }

    [Fact]
    public async Task UpdateAsync_ShouldClearLists_WhenEmptied()
    {
        // 清空名單也要存得進去，不能因為「空清單就跳過」而留著舊值。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(NewProject(glossaryTerms: ["甲專案"], participants: ["王小明"]));
        var saved = await fixture.Context.Project.AsNoTracking().FirstAsync();

        var edit = await service.GetAsync(saved.Id);
        edit.GlossaryTerms = [];
        edit.Participants = [];

        await service.UpdateAsync(edit);

        var updated = await fixture.Context.Project.AsNoTracking().FirstAsync(x => x.Id == saved.Id);
        Assert.Null(updated.GlossaryTerms);
        Assert.Null(updated.Participants);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnListsAsItems()
    {
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(NewProject(glossaryTerms: ["甲專案", "乙系統"]));
        var saved = await fixture.Context.Project.AsNoTracking().FirstAsync();

        var loaded = await service.GetAsync(saved.Id);

        Assert.Equal(["甲專案", "乙系統"], loaded.GlossaryTerms);
        Assert.Empty(loaded.Participants);
    }

    [Fact]
    public async Task GetSelectableAsync_ShouldIncludeParticipants()
    {
        // AI 面板的與會者選擇器讀的是 SelectedProject（來自 GetSelectableAsync），
        // 名冊撈不到的話那個選擇器會永遠是空的。
        await using var fixture = await ProjectServiceFixture.CreateAsync();
        var service = fixture.CreateService();

        await service.AddAsync(NewProject(participants: ["王小明"]));

        var selectable = await service.GetSelectableAsync();

        Assert.Equal(["王小明"], Assert.Single(selectable).Participants);
    }

    #endregion

    #region Clone

    [Fact]
    public void Clone_ShouldDeepCopyLists()
    {
        // 編輯對話框先 Clone 再改。淺複製的話，按「取消」也已經改到清單資料列上了。
        var original = NewProject(glossaryTerms: ["甲專案"], participants: ["王小明"]);

        var cloned = original.Clone();
        cloned.GlossaryTerms.Add("乙系統");
        cloned.Participants.Add("陳大文");

        Assert.Equal(["甲專案"], original.GlossaryTerms);
        Assert.Equal(["王小明"], original.Participants);
    }

    #endregion

    private static ProjectAdapterModel NewProject(
        IEnumerable<string>? glossaryTerms = null,
        IEnumerable<string>? participants = null) => new()
        {
            Title = "Q3 產品改版專案",
            Status = "進行中",
            Owner = "林怡君",
            GlossaryTerms = [.. glossaryTerms ?? []],
            Participants = [.. participants ?? []],
        };

    private sealed class ProjectServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ILoggerFactory loggerFactory;
        private readonly IMapper mapper;
        private readonly IOptions<SystemSettings> systemSettings;
        private readonly string rootPath;

        private ProjectServiceFixture(
            SqliteConnection connection,
            BackendDBContext context,
            string rootPath)
        {
            this.connection = connection;
            Context = context;
            this.rootPath = rootPath;

            loggerFactory = LoggerFactory.Create(_ => { });

            var mapperConfiguration = new MapperConfiguration(
                configuration => configuration.AddProfile<AutoMapping>(),
                loggerFactory);
            mapper = mapperConfiguration.CreateMapper();

            var settings = new SystemSettings();
            settings.ExternalFileSystem.ProjectFilePath = Path.Combine(rootPath, "projectfile");
            settings.ExternalFileSystem.AiChatPath = Path.Combine(rootPath, "aichat");
            systemSettings = Options.Create(settings);
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

            var rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);

            return new ProjectServiceFixture(connection, context, rootPath);
        }

        public ProjectService CreateService()
        {
            var chatStore = new AiChatStore(systemSettings, loggerFactory.CreateLogger<AiChatStore>());

            return new ProjectService(
                Context,
                mapper,
                loggerFactory.CreateLogger<ProjectService>(),
                systemSettings,
                chatStore);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
            loggerFactory.Dispose();

            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // 測試用的暫存目錄清不掉不該讓測試失敗。
            }
        }
    }
}
