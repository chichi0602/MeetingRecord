using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Factories;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.DataAccess;

public class ProjectService
{
    public const long MaxUploadFileSize = 1024L * 1024L * 1024L;

    private readonly BackendDBContext context;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly string projectFileRootPath;

    public IMapper Mapper { get; }
    public ILogger<ProjectService> Logger { get; }

    public ProjectService(
        BackendDBContext context,
        IMapper mapper,
        ILogger<ProjectService> logger,
        IOptions<SystemSettings> systemSettings,
        IRecordAccessScopeProvider accessScope)
    {
        this.context = context;
        Mapper = mapper;
        Logger = logger;
        this.accessScope = accessScope;
        projectFileRootPath = systemSettings.Value.ExternalFileSystem.ProjectFilePath;
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
        IQueryable<Project> dataSource = context.Project.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(dataRequest.Search))
        {
            var search = dataRequest.Search.Trim();
            dataSource = dataSource.Where(x =>
                x.Title.Contains(search) ||
                (x.Description ?? string.Empty).Contains(search) ||
                x.Status.Contains(search) ||
                x.Priority.Contains(search) ||
                x.Owner.Contains(search));
        }

        if (dataRequest.CategoryFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Categories, dataRequest.CategoryFilters));
        }

        if (dataRequest.TeamFilters.Count > 0)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildContainsAnyPredicate<Project>(x => x.Teams, dataRequest.TeamFilters));
        }

        var scope = await accessScope.GetAsync();
        if (!scope.IsAdmin)
        {
            dataSource = dataSource.Where(TagStringHelper.BuildTeamAccessPredicate<Project>(x => x.Teams, scope.Teams));
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
            else if (dataRequest.SortField == nameof(ProjectAdapterModel.Priority))
            {
                dataSource = dataRequest.SortDescending == true
                    ? dataSource.OrderByDescending(x => x.Priority).ThenByDescending(x => x.Id)
                    : dataRequest.SortDescending == false
                        ? dataSource.OrderBy(x => x.Priority).ThenBy(x => x.Id)
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

        Project? item = await context.Project
            .AsNoTracking()
            .Include(x => x.Files)
            .FirstOrDefaultAsync(x => x.Id == id);

        if (item is null)
        {
            Logger.LogWarning("Project not found. ProjectId={ProjectId}", id);
            return new ProjectAdapterModel();
        }

        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(item.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Project access denied by team scope. ProjectId={ProjectId}", id);
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

            await context.Project.AddAsync(itemParameter);
            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(itemParameter, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            Logger.LogInformation("Project created successfully. ProjectId={ProjectId}, Title={Title}", itemParameter.Id, itemParameter.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
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

        try
        {
            CleanTrackingHelper.Clean<Project>(context);
            Project? currentItem = await context.Project
                .Include(x => x.Files)
                .FirstOrDefaultAsync(x => x.Id == paraObject.Id);

            if (currentItem == null)
            {
                Logger.LogWarning("Project update rejected because record was not found. ProjectId={ProjectId}", paraObject.Id);
                return VerifyRecordResultFactory.Build(false, "找不到要修改的專案資料。");
            }

            currentItem.Title = paraObject.Title;
            currentItem.Description = paraObject.Description;
            currentItem.StartDate = paraObject.StartDate;
            currentItem.EndDate = paraObject.EndDate;
            currentItem.Status = paraObject.Status;
            currentItem.Priority = paraObject.Priority;
            currentItem.CompletionPercentage = paraObject.CompletionPercentage;
            currentItem.Owner = paraObject.Owner;
            currentItem.Categories = TagStringHelper.ToStored(paraObject.Categories);
            currentItem.Teams = TagStringHelper.ToStored(paraObject.Teams);
            currentItem.UpdatedAt = paraObject.UpdatedAt;

            await context.SaveChangesAsync();

            var saveFilesResult = await SaveNewFilesAsync(currentItem, uploadFiles);
            if (!saveFilesResult.Success)
            {
                return saveFilesResult;
            }

            var removeFilesResult = await RemoveProjectFilesAsync(currentItem, removedFileIds);
            if (!removeFilesResult.Success)
            {
                return removeFilesResult;
            }

            Logger.LogInformation("Project updated successfully. ProjectId={ProjectId}, Title={Title}", currentItem.Id, currentItem.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update project. ProjectId={ProjectId}, Title={Title}", paraObject.Id, paraObject.Title);
            return VerifyRecordResultFactory.Build(false, "修改專案失敗。", ex);
        }
    }

    public async Task<VerifyRecordResult> DeleteAsync(int id)
    {
        Logger.LogInformation("Deleting project. ProjectId={ProjectId}", id);

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

            foreach (var file in item.Files.ToList())
            {
                DeletePhysicalFile(file);
            }

            context.Project.Remove(item);
            await context.SaveChangesAsync();
            CleanTrackingHelper.Clean<Project>(context);

            Logger.LogInformation("Project deleted successfully. ProjectId={ProjectId}, Title={Title}", id, item.Title);
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
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

        if (file is null)
        {
            return null;
        }

        var parent = await context.Project.AsNoTracking().FirstOrDefaultAsync(p => p.Id == file.ProjectId);
        var scope = await accessScope.GetAsync();
        if (!TagStringHelper.IsTeamAccessible(parent?.Teams, scope.Teams, scope.IsAdmin))
        {
            Logger.LogWarning("Project file download denied by team scope. ProjectFileId={ProjectFileId}", projectFileId);
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

        if (ProjectAdapterModel.PriorityOptions.Contains(paraObject.Priority) == false)
        {
            Logger.LogWarning("Project validation failed because priority is invalid. Title={Title}, Priority={Priority}", paraObject.Title, paraObject.Priority);
            return Task.FromResult(VerifyRecordResultFactory.Build(false, "專案優先順序不合法。"));
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
            DeletePhysicalFile(file);
            context.ProjectFile.Remove(file);
        }

        if (filesToRemove.Count > 0)
        {
            await context.SaveChangesAsync();
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

    private string GetFullPath(string relativePath)
    {
        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(projectFileRootPath, normalizedRelativePath);
    }

    public class ProjectFileDownloadResult
    {
        public Stream Content { get; set; } = Stream.Null;

        public string ContentType { get; set; } = "application/octet-stream";

        public string DownloadFileName { get; set; } = string.Empty;
    }
}
