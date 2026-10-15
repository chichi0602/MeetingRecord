using System.Text;
using AutoMapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 業務資料事件的稽核（1.0.34）：專案、待辦、團隊、分類、提示詞、AI 問答。
/// 會議的稽核測試在 <see cref="MeetingServiceTests"/>（那裡已有完整的夾具）。
/// </summary>
public sealed class BusinessAuditEventsTests : IAsyncDisposable
{
    private const int ActorId = 5;
    private const string ActorAccount = "actor5";

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
    private readonly BackendDBContext context;
    private readonly IMapper mapper;
    private readonly IOptions<SystemSettings> systemSettings;

    public BusinessAuditEventsTests()
    {
        connection.Open();
        context = new TestDbContextFactory(connection).CreateDbContext();
        context.Database.EnsureCreated();

        mapper = new MapperConfiguration(c => c.AddProfile<AutoMapping>(), NullLoggerFactory.Instance).CreateMapper();

        var settings = new SystemSettings();
        settings.ExternalFileSystem.ProjectFilePath = Path.Combine(rootPath, "projectfile");
        settings.ExternalFileSystem.AiChatPath = Path.Combine(rootPath, "aichat");
        settings.ExternalFileSystem.MeetingMediaPath = Path.Combine(rootPath, "media");
        settings.ExternalFileSystem.MeetingTranscriptPath = Path.Combine(rootPath, "transcript");
        settings.ExternalFileSystem.AiCallLogPath = Path.Combine(rootPath, "aicalllog");
        systemSettings = Options.Create(settings);
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    // ───────── 專案 ─────────

    [Fact]
    public async Task Project_CreateUpdateDelete_ShouldWriteAuditsWithFilesPerRow()
    {
        var teamId = await AddTeamAsync("研發");
        var service = CreateProjectService();
        var model = NewProject(teamId);

        await service.AddAsync(model, [Upload("規格.pdf", "spec"), Upload("草圖.png", "png")]);
        var fileIds = await context.ProjectFile.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
        var edit = await service.GetAsync(model.Id);
        await service.UpdateAsync(edit, removedFileIds: [fileIds[0]]);
        await service.DeleteAsync(model.Id, AuditVia.Api);

        var audits = await ReadAuditsAsync();
        Assert.Equal(
            [AuditActions.ProjectCreate, AuditActions.ProjectFileUpload, AuditActions.ProjectFileUpload,
             AuditActions.ProjectUpdate, AuditActions.ProjectFileDelete, AuditActions.ProjectDelete],
            audits.Select(x => x.Action));
        Assert.All(audits, x =>
        {
            Assert.Equal(ActorId, x.ActorUserId);
            Assert.Equal(ActorAccount, x.ActorAccount);
            Assert.Equal("Project", x.TargetType);
            Assert.Equal(model.Id.ToString(), x.TargetId);
        });
        Assert.Equal($"title={model.Title}; via=Web", audits[0].Detail);
        Assert.Equal("file=規格.pdf; size=4", audits[1].Detail);
        Assert.Equal("file=規格.pdf; size=4", audits[4].Detail);
        Assert.Contains("via=Api", audits[5].Detail);
    }

    [Fact]
    public async Task Project_FileDownload_ShouldWriteAudit()
    {
        var teamId = await AddTeamAsync("研發");
        var service = CreateProjectService();
        var model = NewProject(teamId);
        await service.AddAsync(model, [Upload("規格.pdf", "spec")]);
        var fileId = await context.ProjectFile.AsNoTracking().Select(x => x.Id).SingleAsync();

        var download = await service.GetFileDownloadAsync(fileId);
        await download!.Content.DisposeAsync();

        var audit = (await ReadAuditsAsync()).Last();
        Assert.Equal(AuditActions.ProjectFileDownload, audit.Action);
        Assert.Contains("file=規格.pdf", audit.Detail);
    }

    /// <summary>資料範圍擋下的寫入要記，對使用者仍當成不存在。</summary>
    [Fact]
    public async Task Project_UpdateByOutsider_ShouldWriteDataScopeDenied()
    {
        var teamId = await AddTeamAsync("研發");
        var model = NewProject(teamId);
        await CreateProjectService().AddAsync(model);
        var outsider = CreateProjectService(isAdmin: false, userId: 99, account: "outsider");

        var result = await outsider.UpdateAsync(new ProjectAdapterModel { Id = model.Id, Title = "偷改", Status = "進行中", PrimaryTeamId = teamId });

        Assert.False(result.Success);
        var denied = (await ReadAuditsAsync()).Single(x => x.Action == AuditActions.PermissionDenied);
        Assert.False(denied.Success);
        Assert.Equal(99, denied.ActorUserId);
        Assert.Equal("Project", denied.TargetType);
        Assert.Equal(model.Id.ToString(), denied.TargetId);
        Assert.Equal("reason=DataScope; op=Update", denied.Detail);
    }

    // ───────── 待辦 ─────────

    [Fact]
    public async Task Todo_Lifecycle_ShouldWriteAudits()
    {
        var projectId = await AddProjectAsync();
        var service = CreateTodoService();

        await service.AddAsync(new TodoAdapterModel { Title = "寫規格", ProjectId = projectId, Priority = "中", Status = "待辦" });
        var todo = await context.Todo.AsNoTracking().SingleAsync();
        await service.SetCompletedAsync(todo.Id, true);
        await service.SetCompletedAsync(todo.Id, false);
        await service.DeleteAsync(todo.Id);

        var audits = await ReadAuditsAsync();
        Assert.Equal(
            [AuditActions.TodoCreate, AuditActions.TodoComplete, AuditActions.TodoReopen, AuditActions.TodoDelete],
            audits.Select(x => x.Action));
        Assert.All(audits, x => Assert.Equal(todo.Id.ToString(), x.TargetId));
        Assert.Equal($"title=寫規格; projectId={projectId}; via=Web", audits[0].Detail);
    }

    [Fact]
    public async Task Todo_AddedByAiExtraction_ShouldBeMarked()
    {
        var projectId = await AddProjectAsync();

        await CreateTodoService().AddAsync(new TodoAdapterModel { Title = "AI 抽出的待辦", ProjectId = projectId, Priority = "中", Status = "待辦" }, AuditVia.AiExtract);

        Assert.EndsWith("via=AiExtract", (await ReadAuditsAsync()).Single().Detail);
    }

    [Fact]
    public async Task Todo_DeleteByOutsider_ShouldWriteDataScopeDenied_ButMissingTodoShouldNot()
    {
        var projectId = await AddProjectAsync();
        var todo = new Todo { Title = "別人的待辦", ProjectId = projectId, Status = "待辦", Priority = "中" };
        context.Todo.Add(todo);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var outsider = CreateTodoService(isAdmin: false, userId: 99);

        await outsider.DeleteAsync(todo.Id);
        await outsider.DeleteAsync(123456);

        var denied = Assert.Single(await ReadAuditsAsync());
        Assert.Equal(AuditActions.PermissionDenied, denied.Action);
        Assert.Equal("Todo", denied.TargetType);
        Assert.Equal(todo.Id.ToString(), denied.TargetId);
        Assert.True(await context.Todo.AnyAsync(x => x.Id == todo.Id));
    }

    // ───────── 團隊、分類、提示詞 ─────────

    [Fact]
    public async Task Team_ShouldAuditCrudToggle_AndMembersOnlyWhenChanged()
    {
        var service = CreateTeamService();
        var userA = await AddUserAsync("a");
        var userB = await AddUserAsync("b");
        var model = new TeamAdapterModel { Name = "設計組", IsEnabled = true };

        await service.AddAsync(model);
        await service.SyncMembersAsync(model.Id, [userA, userB]);
        await service.SyncMembersAsync(model.Id, [userA, userB]);
        await service.SyncMembersAsync(model.Id, [userA]);
        await service.SetEnabledAsync(model.Id, false);
        await service.DeleteAsync(model.Id);

        var audits = await ReadAuditsAsync();
        Assert.Equal(
            [AuditActions.TeamCreate, AuditActions.TeamMembersChange, AuditActions.TeamMembersChange, AuditActions.TeamDisable, AuditActions.TeamDelete],
            audits.Select(x => x.Action));
        Assert.Equal($"added={userA},{userB}; removed=", audits[1].Detail);
        Assert.Equal($"added=; removed={userB}", audits[2].Detail);
    }

    [Fact]
    public async Task Category_ShouldAuditCrud_AndTeamsOnlyWhenChanged()
    {
        var service = CreateCategoryService();
        var teamId = await AddTeamAsync("研發");
        var model = new CategoryAdapterModel { Name = "週報", IsEnabled = true };

        await service.AddAsync(model);
        await service.SyncTeamsAsync(model.Id, [teamId]);
        await service.SyncTeamsAsync(model.Id, [teamId]);
        var edit = await service.GetAsync(model.Id);
        edit.Name = "月報";
        await service.UpdateAsync(edit);
        await service.SetEnabledAsync(model.Id, false);
        await service.DeleteAsync(model.Id);

        Assert.Equal(
            [AuditActions.CategoryCreate, AuditActions.CategoryTeamsChange, AuditActions.CategoryUpdate, AuditActions.CategoryDisable, AuditActions.CategoryDelete],
            (await ReadAuditsAsync()).Select(x => x.Action));
    }

    /// <summary>提示詞只記名稱，不記本文。</summary>
    [Fact]
    public async Task PromptTemplate_ShouldAuditWithoutContent_AndPresetsAsOneRow()
    {
        var service = CreatePromptTemplateService();
        const string secretContent = "請依照以下逐字稿{{transcript}}產生會議紀錄";

        await service.AddAsync(new PromptTemplateAdapterModel { Name = "標準範本", Content = secretContent, IsEnabled = true });
        var id = await context.PromptTemplate.AsNoTracking().Select(x => x.Id).SingleAsync();
        await service.SetEnabledAsync(id, false);
        await service.AddPresetsAsync();
        await service.AddPresetsAsync();

        var audits = await ReadAuditsAsync();
        Assert.Equal(
            [AuditActions.PromptTemplateCreate, AuditActions.PromptTemplateDisable, AuditActions.PromptTemplateAddPresets],
            audits.Select(x => x.Action));
        Assert.All(audits, x => Assert.DoesNotContain("transcript", x.Detail));
        Assert.StartsWith("added=", audits[2].Detail);
    }

    // ───────── AI 問答 ─────────

    [Fact]
    public async Task AiChat_ConversationOperations_ShouldWriteAudits_ExceptAutoCreate()
    {
        var projectId = await AddProjectAsync();
        var service = CreateAiChatService();

        var auto = await service.CreateConversationAsync(AiChatScope.Project, projectId, audit: false);
        var manual = await service.CreateConversationAsync(AiChatScope.Project, projectId);
        await service.RenameConversationAsync(AiChatScope.Project, projectId, manual, "需求討論");
        await service.ClearHistoryAsync(AiChatScope.Project, projectId, auto);

        var audits = await ReadAuditsAsync();
        Assert.Equal(
            [AuditActions.AiChatCreate, AuditActions.AiChatRename, AuditActions.AiChatClearHistory],
            audits.Select(x => x.Action));
        Assert.All(audits, x =>
        {
            Assert.Equal("Project", x.TargetType);
            Assert.Equal(projectId.ToString(), x.TargetId);
        });
        Assert.Equal($"conversationId={manual}", audits[0].Detail);
        Assert.Equal($"conversationId={manual}; title=需求討論", audits[1].Detail);
        Assert.Equal($"conversationId={auto}", audits[2].Detail);
    }

    [Fact]
    public async Task AiChat_RenameByOutsider_ShouldWriteDataScopeDenied()
    {
        var projectId = await AddProjectAsync();
        var conversationId = await CreateAiChatService().CreateConversationAsync(AiChatScope.Project, projectId, audit: false);
        var outsider = CreateAiChatService(isAdmin: false, userId: 99);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => outsider.RenameConversationAsync(AiChatScope.Project, projectId, conversationId, "偷改"));

        var denied = Assert.Single(await ReadAuditsAsync());
        Assert.Equal(AuditActions.PermissionDenied, denied.Action);
        Assert.Equal("reason=DataScope; op=AiChatRename", denied.Detail);
    }

    // ───────── 組裝 ─────────

    private AuditLogService Audit(int userId = ActorId, string account = ActorAccount)
        => TestAuditLogs.CreateService(context, TestAuditActor.Fixed(userId, account));

    private ProjectAccessService Access(bool isAdmin, int userId)
        => isAdmin ? TestProjectAccess.Admin(context, userId) : TestProjectAccess.User(context, userId);

    private ProjectService CreateProjectService(bool isAdmin = true, int userId = ActorId, string account = ActorAccount)
        => new(
            context, mapper, NullLogger<ProjectService>.Instance, systemSettings,
            new AiChatStore(systemSettings, NullLogger<AiChatStore>.Instance),
            Access(isAdmin, userId),
            TestAiServices.CreateCallLogService(context),
            Audit(userId, account));

    private TodoService CreateTodoService(bool isAdmin = true, int userId = ActorId)
        => new(context, mapper, NullLogger<TodoService>.Instance, Access(isAdmin, userId), Audit(userId));

    private TeamService CreateTeamService()
        => new(context, mapper, NullLogger<TeamService>.Instance, Access(true, ActorId), Audit());

    private CategoryService CreateCategoryService()
        => new(context, mapper, NullLogger<CategoryService>.Instance, Audit());

    private PromptTemplateService CreatePromptTemplateService()
        => new(context, mapper, NullLogger<PromptTemplateService>.Instance, Audit());

    private AiChatService CreateAiChatService(bool isAdmin = true, int userId = ActorId)
    {
        var llmSettings = Options.Create(new LlmSettings());
        return new AiChatService(
            context,
            [],
            new MeetingFileStore(systemSettings, NullLogger<MeetingFileStore>.Instance),
            new AiChatStore(systemSettings, NullLogger<AiChatStore>.Instance),
            new AttachmentTextExtractor(systemSettings, NullLogger<AttachmentTextExtractor>.Instance),
            new CurrentUserService(),
            llmSettings,
            TestAiServices.CreateGateway(context, llmSettings),
            NullLogger<AiChatService>.Instance,
            Access(isAdmin, userId),
            Audit(userId));
    }

    private static ProjectAdapterModel NewProject(int teamId) => new()
    {
        Title = "Q3 產品改版專案",
        Status = "進行中",
        Owner = "林怡君",
        PrimaryTeamId = teamId,
    };

    private static ProjectUploadFileInput Upload(string fileName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new ProjectUploadFileInput { FileName = fileName, FileSize = bytes.Length, Content = new MemoryStream(bytes) };
    }

    private async Task<int> AddTeamAsync(string name)
    {
        var team = new Team { Name = name, IsEnabled = true };
        context.Team.Add(team);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return team.Id;
    }

    private async Task<int> AddUserAsync(string account)
    {
        var user = new MyUser { Account = account, Name = account, Password = "x", Status = true };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return user.Id;
    }

    private async Task<int> AddProjectAsync()
    {
        var teamId = await AddTeamAsync($"團隊-{Guid.NewGuid():N}");
        var project = new Project { Title = "既有專案", Status = "進行中", Teams = [new ProjectTeam { TeamId = teamId, IsPrimary = true }] };
        context.Project.Add(project);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return project.Id;
    }

    private async Task<List<AuditLog>> ReadAuditsAsync()
    {
        await using var fresh = new TestDbContextFactory(connection).CreateDbContext();
        return await fresh.AuditLog.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }
}
