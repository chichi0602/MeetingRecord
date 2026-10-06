using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.DataAccess;

public class ProjectService
{
    public const long MaxUploadFileSize = 1024L * 1024L * 1024L;

    private readonly BackendDBContext context;
    private readonly string projectFileRootPath;
    private readonly AiChatStore chatStore;
    private readonly ProjectAccessService projectAccess;

    public IMapper Mapper { get; }
    public ILogger<ProjectService> Logger { get; }

    public ProjectService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<ProjectService> logger,
        IOptions<SystemSettings> systemSettings,
        AiChatStore chatStore,
        ProjectAccessService projectAccess)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        projectFileRootPath = systemSettings.Value.ExternalFileSystem.ProjectFilePath;
        this.chatStore = chatStore;
        this.projectAccess = projectAccess;
    }

    public async Task<DataRequestResult<ProjectAdapterModel>> GetAsync(DataRequest dataRequest)
    {
        Logger.LogDebug(
            "Loading projects. Search={Search}, SortField={SortField}, SortDescending={SortDescending}, CurrentPage={CurrentPage}, PageSize={PageSize}, Take={Take}",
            dataRequest.Search,
            dataRequest.SortField,
            dataRequest.SortDescending,
            dataRequest.CurrentPage,
            dataRequest.PageSize,
            dataRequest.Take);

        DataRequestResult<ProjectAdapterModel> result = new();
        var access = await projectAccess.GetAsync();
        IQueryable<Project> dataSource = access.Filter(context.Project.AsNoTracking())
            .Include(x => x.Teams).ThenInclude(x => x.Team);

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                x.Status.Contains(search) ||
                x.Owner.Contains(search));
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (!string.IsNullOrWhiteSpace(dataRequest.SortField))
        {
            if (dataRequest.SortField == nameof(ProjectAdapterModel.Title))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Title).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Title).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.StartDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.StartDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.StartDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.EndDate))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.EndDate).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.EndDate).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Status))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Status).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Status).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CompletionPercentage))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CompletionPercentage).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CompletionPercentage).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Owner))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Owner).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Owner).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.CreatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.UpdatedAt))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id)
                        : dataSource;
            }
        }

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource.Skip((dataRequest.CurrentPage - 1) * dataRequest.PageSize);
        if (dataRequest.Take != 0)
        {
            dataSource = dataSource.Take(dataRequest.PageSize);
        }

        var records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<ProjectAdapterModel>>(records);
        Logger.LogDebug("Loaded projects successfully. Count={Count}", result.Count);
        return result;
    }

    public async Task<ProjectAdapterModel> GetAsync(int id)
    {
        Logger.LogDebug("Loading project by id. ProjectId={ProjectId}", id);

        if (!(await projectAccess.GetAsync()).CanViewProject(id))
        {
            Logger.LogWarning("Project read denied by data group. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        Project? item = await context.Project
            .AsNoTracking()
            .Include(x => x.Files)
            .Include(x => x.Teams).ThenInclude(x => x.Team)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Project not found. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        return Mapper.Map<ProjectAdapterModel>(item);
    }

    public async Task<VerifyRecordResult> AddAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        Logger.LogInformation("Creating project. Title={Title}, Owner={Owner}", paraObject.Title, paraObject.Owner);

        try
        {
            CleanTrackingHelper.Clean<Project>(context);
            Project itemParameter = Mapper.Map<Project>(paraObject);
            itemParameter.Files = [];

            // 團隊（0.4.102）：主責必填，非管理者的主責只能是自己所屬的團隊；協作可選任何團隊。
            var access = await projectAccess.GetAsync();
            var teams = await ProjectTeamWriter.ValidateAsync(context, access.ResolveProjectTeams(null, paraObject.PrimaryTeamId, paraObject.CollaboratorTeamIds));
            if (teams.Error is not null)
            {
                return VerifyRecordResultFactory.Build(false, teams.Error);
            }

            itemParameter.Teams = [];
            ProjectTeamWriter.Apply(itemParameter, teams);

            await context.Project.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            // 回填 Id（0.4.114）：畫面用它選取剛建立的專案；附件失敗時也靠它切成編輯模式，重送才不會再建一筆。
            paraObject.Id = itemParameter.Id;

            var saveFilesResult = await SaveNewFilesAsync(itemParameter, uploadFiles);
            if (!saveFilesResult.Success)
            {
                context.ChangeTracker.Clear();
                return VerifyRecordResultFactory.Build(false, "專案已建立，但附件儲存失敗，請在編輯中重新上傳附件。");
            }

            Logger.LogInformation("Project created successfully. ProjectId={ProjectId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            // DbContext 是整條 Blazor 連線共用的：失敗留下的追蹤實體會在下一次任何人 SaveChanges 時被寫進去。
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to create project. Title={Title}", paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "新增專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> UpdateAsync(
        ProjectAdapterModel paraObject,
        IEnumerable<ProjectUploadFileInput>? uploadFiles = null,
        IEnumerable<int>? removedFileIds = null)
    {
        Logger.LogInformation("Updating project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        // 能不能改由角色決定（畫面與 API 各自檢查動作權限）；這裡只擋「看不到的專案」——看不到就當成不存在。
        var access = await projectAccess.GetAsync();
        if (!access.CanViewProject(paraObject.Id))
        {
            Logger.LogWarning("Project update denied by data group. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "找不到要修改的專案資料。");
        }

        try
        {
            CleanTrackingHelper.Clean<Project>(context);
            Project? currentItem = await context.Project
                .Include(x => x.Files)
                .Include(x => x.Teams)
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Project update rejected because record was not found. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的專案資料。");
            }

            // 主責沒變時照原樣保留（就算不是自己的團隊，協作團隊的人也要能存檔）；改主責才檢查是不是自己的團隊。
            // ⚠️ 一定要在改欄位之前驗證（0.4.114）：currentItem 是被追蹤的，先改欄位再 return 的話，
            //    被拒絕的修改會留在共用的 DbContext 裡，下一次任何人 SaveChanges 就被寫進資料庫。
            var existingPrimary = currentItem.Teams.FirstOrDefault(x => x.IsPrimary)?.TeamId;
            var teams = await ProjectTeamWriter.ValidateAsync(context, access.ResolveProjectTeams(existingPrimary, paraObject.PrimaryTeamId, paraObject.CollaboratorTeamIds));
            if (teams.Error is not null)
            {
                context.ChangeTracker.Clear();
                return VerifyRecordResultFactory.Build(false, teams.Error);
            }

            currentItem.Title = paraObject.Title;
            currentItem.StartDate = paraObject.StartDate;
            currentItem.EndDate = paraObject.EndDate;
            currentItem.Status = paraObject.Status;
            currentItem.CompletionPercentage = paraObject.CompletionPercentage;
            currentItem.Owner = paraObject.Owner;

            ProjectTeamWriter.Apply(currentItem, teams);

            // ⚠️ 這個方法刻意手抄欄位、不走 Mapper（AddAsync 才走）。
            // 新增欄位時只改 AutoMapping 會變成「新增存得進去、修改存不進去」，而且不會報錯。
            currentItem.GlossaryTerms = TagStringHelper.ToStored(paraObject.GlossaryTerms);
            currentItem.Participants = TagStringHelper.ToStored(paraObject.Participants);
            currentItem.Categories = TagStringHelper.ToStored(paraObject.Categories);

            currentItem.UpdatedAt = paraObject.UpdatedAt;

            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(currentItem, uploadFiles);
            if (!saveFilesResult.Success)
            {
                context.ChangeTracker.Clear();
                return VerifyRecordResultFactory.Build(false, "專案資料已儲存，但附件儲存失敗，請重新上傳附件。");
            }

            var removeFilesResult = await RemoveProjectFilesAsync(currentItem, removedFileIds);
            if (!removeFilesResult.Success)
            {
                context.ChangeTracker.Clear();
                return removeFilesResult;
            }

            Logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", currentItem.Id, currentItem.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to update project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting project. ProjectId={ProjectId}", id);

        // 能不能刪由角色的刪除權限決定；這裡只擋看不到的專案。
        if (!(await projectAccess.GetAsync()).CanViewProject(id))
        {
            Logger.LogWarning("Project deletion denied by data group. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "找不到要刪除的專案資料。");
        }

        try
        {
            CleanTrackingHelper.Clean<Project>(context);
            Project? item = await context.Project
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (item == null)
            {
                Logger.LogWarning("Project deletion rejected because record was not found. ProjectId={ProjectId}", id);
                return VerifyRecordResultFactory.Build(false, "找不到要刪除的專案資料。");
            }

            // 先寫資料庫、成功後才刪實體檔（0.4.114）：反過來的話資料庫失敗時檔案已經不見，資料列卻還在。
            var physicalFiles = item.Files.ToList();
            context.Project.Remove(item);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Project>(context);

            foreach (var file in physicalFiles)
            {
                DeletePhysicalFile(file);
            }

            // 0.4.60 起對話存在檔案系統，資料表已移除，Cascade 不會再幫我們清掉它。
            chatStore.TryDelete(AiChatScope.Project, id);

            Logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            context.ChangeTracker.Clear();
            Logger.LogError(ex, "Failed to delete project. ProjectId={ProjectId}", id);
            return VerifyRecordResultFactory.Build(false, "刪除專案失敗。", ex);
        }
    }

    public Task<VerifyRecordResult> BeforeAddCheckAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-create validation for project. Title={Title}", paraObject.Title);
        return ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public async Task<VerifyRecordResult> BeforeUpdateCheckAsync(
        ProjectAdapterModel paraObject,
        IEnumerable<ProjectUploadFileInput>? uploadFiles = null)
    {
        Logger.LogDebug("Running pre-update validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);

        CleanTrackingHelper.Clean<Project>(context);
        Project? searchItem = await context.Project
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

        if (searchItem == null)
        {
            Logger.LogWarning("Pre-update validation failed because project was not found. ProjectId={ProjectId}", paraObject.Id);
            return VerifyRecordResultFactory.Build(false, "要修改的專案資料不存在。");
        }

        return await ValidateBusinessRulesAsync(paraObject, uploadFiles);
    }

    public Task<VerifyRecordResult> BeforeDeleteCheckAsync(ProjectAdapterModel paraObject)
    {
        Logger.LogDebug("Running pre-delete validation for project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    public async Task<ProjectFileDownloadResult?> GetFileDownloadAsync(int projectFileId)
    {
        var file = await context.ProjectFile
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == projectFileId);

        if (file is null || !(await projectAccess.GetAsync()).CanViewProject(file.ProjectId))
        {
            return null;
        }

        var fullPath = GetFullPath(file.RelativePath);
        if (!File.Exists(fullPath))
        {
            Logger.LogWarning("Project file metadata exists but physical file was not found. ProjectFileId={ProjectFileId}, FullPath={FullPath}", projectFileId, fullPath);
            return null;
        }

        return new ProjectFileDownloadResult
        {
            Content = File.OpenRead(fullPath),
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            DownloadFileName = file.OriginalFileName
        };
    }

    private Task<VerifyRecordResult> ValidateBusinessRulesAsync(ProjectAdapterModel paraObject, IEnumerable<ProjectUploadFileInput>? uploadFiles)
    {
        if (paraObject.StartDate.HasValue && paraObject.EndDate.HasValue && paraObject.EndDate.Value < paraObject.StartDate.Value)
        {
            Logger.LogWarning("Project validation failed because end date is earlier than start date. Title={Title}", paraObject.Title);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "結束日期不可早於開始日期。"));
        }

        if (ProjectAdapterModel.StatusOptions.Contains(paraObject.Status) == false)
        {
            Logger.LogWarning("Project validation failed because status is invalid. Title={Title}, Status={Status}", paraObject.Title, paraObject.Status);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "專案狀態不合法。"));
        }

        if (paraObject.CompletionPercentage < 0 || paraObject.CompletionPercentage > 100)
        {
            Logger.LogWarning("Project validation failed because completion percentage is out of range. Title={Title}, CompletionPercentage={CompletionPercentage}", paraObject.Title, paraObject.CompletionPercentage);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "完成百分比必須介於 0 到 100。"));
        }

        if (uploadFiles is not null)
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.FileSize > MaxUploadFileSize)
                {
                    Logger.LogWarning("Project upload validation failed because file exceeded the size limit. FileName={FileName}, FileSize={FileSize}", uploadFile.FileName, uploadFile.FileSize);
                    return Task.FromResult(VerifyRecordResultFactory.Build(false, $"檔案 {uploadFile.FileName} 超過 1GB 限制"));
                }
            }
        }

        if (string.IsNullOrWhiteSpace(projectFileRootPath))
        {
            Logger.LogWarning("Project upload validation failed because ProjectFilePath is not configured.");
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "尚未設定專案附件儲存目錄"));
        }

        return Task.FromResult(VerifyRecordResultFactory.Build(true));
    }

    private async Task<VerifyRecordResult> SaveNewFilesAsync(Project project, IEnumerable<ProjectUploadFileInput>? uploadFiles)
    {
        if (uploadFiles is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        List<ProjectFile> newFiles = [];
        List<string> createdFullPaths = [];

        try
        {
            foreach (var uploadFile in uploadFiles)
            {
                if (uploadFile.Content == Stream.Null)
                {
                    continue;
                }

                var fileMetadata = await SavePhysicalFileAsync(project, uploadFile);
                newFiles.Add(fileMetadata.File);
                createdFullPaths.Add(fileMetadata.FullPath);
            }

            if (newFiles.Count > 0)
            {
                await context.ProjectFile.AddRangeAsync(newFiles);
                await context.SaveChangesAsync();
            }

            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            foreach (var fullPath in createdFullPaths)
            {
                TryDeleteFile(fullPath);
            }

            Logger.LogError(ex, "Failed to save project files. ProjectId={ProjectId}", project.Id);
            return VerifyRecordResultFactory.Build(false, "專案附件儲存失敗", ex);
        }
    }

    private async Task<VerifyRecordResult> RemoveProjectFilesAsync(Project project, IEnumerable<int>? removedFileIds)
    {
        if (removedFileIds is null)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        var removedFileIdSet = removedFileIds.Distinct().ToHashSet();
        if (removedFileIdSet.Count == 0)
        {
            return VerifyRecordResultFactory.Build(true);
        }

        var filesToRemove = project.Files
            .Where(x => removedFileIdSet.Contains(x.Id))
            .ToList();

        foreach (var file in filesToRemove)
        {
            context.ProjectFile.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            await context.SaveChangesAsync();
        }

        // 資料庫寫入成功後才刪實體檔（0.4.114）。
        foreach (var file in filesToRemove)
        {
            DeletePhysicalFile(file);
        }

        return VerifyRecordResultFactory.Build(true);
    }

    private async Task<(ProjectFile File, string FullPath)> SavePhysicalFileAsync(Project project, ProjectUploadFileInput uploadFile)
    {
        var originalFileName = Path.GetFileName(uploadFile.FileName);
        var extension = Path.GetExtension(originalFileName);
        var year = project.CreatedAt.Year.ToString("0000");
        var month = project.CreatedAt.Month.ToString("00");
        var relativePath = Path.Combine(year, month, $"{Guid.NewGuid():N}{extension}");
        var fullPath = GetFullPath(relativePath);
        var directoryPath = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (uploadFile.Content.CanSeek)
        {
            uploadFile.Content.Position = 0;
        }

        await using (var targetStream = File.Create(fullPath))
        {
            await uploadFile.Content.CopyToAsync(targetStream);
        }

        var contentType = string.IsNullOrWhiteSpace(uploadFile.ContentType)
            ? "application/octet-stream"
            : uploadFile.ContentType;

        return (
            new ProjectFile
            {
                ProjectId = project.Id,
                OriginalFileName = originalFileName,
                StoredFileName = Path.GetFileName(fullPath),
                RelativePath = relativePath.Replace('\\', '/'),
                ContentType = contentType,
                FileSize = uploadFile.FileSize,
                CreatedAt = DateTime.Now
            },
            fullPath);
    }

    private void DeletePhysicalFile(ProjectFile file)
    {
        var fullPath = GetFullPath(file.RelativePath);
        TryDeleteFile(fullPath);
    }

    private void TryDeleteFile(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        File.Delete(fullPath);
    }

    /// <summary>
    /// 取得目前使用者可存取的全部專案（依標題排序），供專案項目頁的專案選擇器使用。
    /// 這個畫面以下拉選單取代分頁表格，因此不分頁；專案數量成長到數百筆時要重新檢討。
    /// </summary>
    public async Task<List<ProjectAdapterModel>> GetSelectableAsync(CancellationToken cancellationToken = default)
    {
        var access = await projectAccess.GetAsync();
        IQueryable<Project> dataSource = access.Filter(context.Project.AsNoTracking())
            .Include(x => x.Teams).ThenInclude(x => x.Team);

        var items = await dataSource
            .OrderBy(x => x.Title)
            .ToListAsync(cancellationToken);

        return Mapper.Map<List<ProjectAdapterModel>>(items);
    }

    /// <summary>
    /// 專案表單「主責團隊」的選項（0.4.102）：管理者看全部啟用中的團隊，一般使用者只看自己所屬的。
    /// </summary>
    public async Task<List<TeamOption>> GetSelectablePrimaryTeamsAsync()
    {
        var access = await projectAccess.GetAsync();
        var query = context.Team.AsNoTracking().Where(x => x.IsEnabled);
        if (!access.IsAdmin)
        {
            var mine = access.TeamIds.ToList();
            query = query.Where(x => mine.Contains(x.Id));
        }

        return await query
            .OrderBy(x => x.Name)
            .Select(x => new TeamOption(x.Id, x.Name))
            .ToListAsync();
    }

    /// <summary>
    /// 專案表單「協作團隊」的選項：全部啟用中的團隊，任何人都可以拉別的部門進來協作。
    /// <paramref name="alsoInclude"/>：編輯時專案已掛著的團隊（可能已停用）也列進來（0.4.114），
    /// 否則多選框裡沒有對應選項，使用者一動這個欄位，那些團隊就被默默拿掉，可見範圍跟著改變。
    /// </summary>
    public async Task<List<TeamOption>> GetSelectableCollaboratorTeamsAsync(IEnumerable<int>? alsoInclude = null)
    {
        var keep = alsoInclude?.ToList() ?? [];
        return await context.Team.AsNoTracking()
            .Where(x => x.IsEnabled || keep.Contains(x.Id))
            .OrderBy(x => x.Name)
            .Select(x => new TeamOption(x.Id, x.Name))
            .ToListAsync();
    }

    private string GetFullPath(string relativePath)
    {
        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(projectFileRootPath, normalizedRelativePath);
    }

    /// <summary>團隊下拉的一個選項。</summary>
    public sealed record TeamOption(int Id, string Name);

    public class ProjectFileDownloadResult
    {
        public Stream Content { get; set; } = Stream.Null;

        public string ContentType { get; set; } = "application/octet-stream";

        public string DownloadFileName { get; set; } = string.Empty;
    }
}
