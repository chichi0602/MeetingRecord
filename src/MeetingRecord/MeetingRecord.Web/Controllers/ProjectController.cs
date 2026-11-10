using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Projects;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Uploads;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 專案項目 API。1.1.24（WASM 遷移 M3）起全部走 <see cref="ProjectService"/>（畫面同一套規則），<c>ProjectRepository</c> 已刪除。
///
/// <para>
/// 既有的五支（GetById／Search／Create／Update／Delete）回應只加欄位，原本的相容行為照舊：新增沒帶主責團隊時用自己 Id 最小的團隊、
/// 只帶負責人姓名時在團隊裡找同名帳號；修改時沒帶（或空的）團隊、負責人、名詞、名冊、分類照原樣保留。
/// 外部看得到的變更只有「同名改成允許」（與畫面一致），見 changelog。
/// 畫面要的整筆覆寫（含附件、日期可留空）另開 <c>form</c> 端點。
/// </para>
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public class ProjectController(
    ILogger<ProjectController> logger,
    ProjectService projectService,
    ProjectAccessService projectAccessService,
    ProjectAttachmentUploads attachmentUploads,
    CategoryService categoryService) : ControllerBase
{
    private const string NotFoundMessage = "找不到 ID 為 {0} 的專案";

    /// <summary>與畫面相同的商業規則（0.4.114）：狀態要在選項內、結束日期不得早於開始日期。</summary>
    private static string? ValidateBusinessRules(ProjectCreateUpdateDto dto)
    {
        if (!ProjectAdapterModel.StatusOptions.Contains(dto.Status))
        {
            return $"狀態必須是：{string.Join("、", ProjectAdapterModel.StatusOptions)}";
        }

        if (dto.StartDate is { } start && dto.EndDate is { } end && end < start)
        {
            return "結束日期 不可早於開始日期";
        }

        return null;
    }

    /// <summary>單筆（含附件與呼叫者的權限旗標）。<paramref name="includeRelatedData"/> 保留給舊呼叫端，不影響結果。</summary>
    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> GetById(int id, [FromQuery] bool includeRelatedData = false)
    {
        var model = await projectService.GetAsync(id);
        if (model.Id == 0)
        {
            return NotFound(ApiResult<ProjectDto>.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var access = await projectAccessService.GetAsync();
        return Ok(ApiResult<ProjectDto>.SuccessResult(ToDto(model, access, includeFiles: true), "取得專案成功"));
    }

    /// <summary>對外的搜尋，行為同 1.1.23 以前（關鍵字、負責人、狀態、日期與完成度區間，預設依更新時間）。</summary>
    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<ProjectDto>>>> Search([FromBody] ProjectSearchRequestDto request)
    {
        var (items, totalCount) = await projectService.SearchFlatAsync(request);
        var access = await projectAccessService.GetAsync();
        return Ok(ApiResult<PagedResult<ProjectDto>>.SuccessResult(new PagedResult<ProjectDto>
        {
            Items = items.Select(x => ToDto(x, access, includeFiles: false)).ToList(),
            TotalCount = totalCount,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        }, "搜尋專案成功"));
    }

    /// <summary>
    /// 新增。相容 1.1.23 以前：沒帶主責團隊用自己 Id 最小的團隊；只帶負責人姓名時在團隊裡找同名帳號（剛好一位才採用）。
    /// 1.1.24 起同名允許（與畫面一致）。
    /// </summary>
    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> Create([FromBody] ProjectCreateUpdateDto projectDto)
    {
        if (ValidateBusinessRules(projectDto) is { } ruleError)
        {
            return BadRequest(ApiResult<ProjectDto>.ValidationError(ruleError));
        }

        var access = await projectAccessService.GetAsync();
        var primary = projectDto.PrimaryTeamId ?? (access.TeamIds.Count > 0 ? access.TeamIds.Min() : null);
        if (primary is null)
        {
            return BadRequest(ApiResult<ProjectDto>.ValidationError("請先加入團隊，或指定主責團隊。"));
        }

        if (projectDto.OwnerUserId is null && string.IsNullOrWhiteSpace(projectDto.Owner))
        {
            return BadRequest(ApiResult<ProjectDto>.ValidationError("負責人 不可為空白"));
        }

        var now = DateTime.Now;
        var model = new ProjectAdapterModel
        {
            Title = projectDto.Title,
            StartDate = projectDto.StartDate,
            EndDate = projectDto.EndDate,
            Status = projectDto.Status,
            CompletionPercentage = projectDto.CompletionPercentage,
            OwnerUserId = projectDto.OwnerUserId
                ?? await projectService.MatchOwnerByNameAsync(projectDto.Owner, primary, projectDto.CollaboratorTeamIds),
            PrimaryTeamId = primary,
            CollaboratorTeamIds = projectDto.CollaboratorTeamIds ?? [],
            GlossaryTerms = projectDto.GlossaryTerms ?? [],
            Participants = projectDto.Participants ?? [],
            Categories = projectDto.Categories ?? [],
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await projectService.AddAsync(model, uploadFiles: null, via: AuditVia.Api, legacyOwnerName: projectDto.Owner);
        if (!result.Success)
        {
            return Failure<ProjectDto>(result);
        }

        logger.LogInformation("Project created through API. ProjectId={ProjectId}", model.Id);
        return Ok(ApiResult<ProjectDto>.SuccessResult(ToDto(await projectService.GetAsync(model.Id), access, includeFiles: true), "新增專案成功"));
    }

    /// <summary>
    /// 修改。相容 1.1.23 以前：沒帶的主責與協作團隊、負責人帳號照原樣保留；名詞、名冊、分類沒帶或空的也保留（API 清不掉名單，要清請用畫面）。
    /// 1.1.24 起同名允許（與畫面一致）。
    /// </summary>
    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] ProjectCreateUpdateDto projectDto)
    {
        if (id != projectDto.Id)
        {
            return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
        }

        if (ValidateBusinessRules(projectDto) is { } ruleError)
        {
            return BadRequest(ApiResult.ValidationError(ruleError));
        }

        var existing = await projectService.GetAsync(id);
        if (existing.Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var model = existing.Clone();
        model.Title = projectDto.Title;
        model.StartDate = projectDto.StartDate;
        model.EndDate = projectDto.EndDate;
        model.Status = projectDto.Status;
        model.CompletionPercentage = projectDto.CompletionPercentage;
        model.OwnerUserId = projectDto.OwnerUserId ?? existing.OwnerUserId;
        model.PrimaryTeamId = projectDto.PrimaryTeamId ?? existing.PrimaryTeamId;
        model.CollaboratorTeamIds = projectDto.CollaboratorTeamIds ?? existing.CollaboratorTeamIds;
        model.GlossaryTerms = projectDto.GlossaryTerms is { Count: > 0 } glossary ? glossary : existing.GlossaryTerms;
        model.Participants = projectDto.Participants is { Count: > 0 } participants ? participants : existing.Participants;
        model.Categories = projectDto.Categories is { Count: > 0 } categories ? categories : existing.Categories;
        model.UpdatedAt = DateTime.Now;

        var result = await projectService.UpdateAsync(model, uploadFiles: null, removedFileIds: null, via: AuditVia.Api);
        if (!result.Success)
        {
            return Failure(result);
        }

        logger.LogInformation("Project updated through API. ProjectId={ProjectId}", id);
        return Ok(ApiResult.SuccessResult("更新專案成功"));
    }

    // 1.0.52：刪除只限專案負責人（服務層把關），負責人是一般使用者也能刪，所以這裡只要求「編輯」，與畫面一致。
    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        if ((await projectService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        // 和畫面同一套（0.4.114）：一併刪掉附件實體檔與 AI 問答對話；不是負責人、已完成（封存）回 400 並說明原因。
        var result = await projectService.DeleteAsync(id, AuditVia.Api);
        return result.Success ? Ok(ApiResult.SuccessResult("刪除專案成功")) : Failure(result);
    }

    // ---------------- 畫面專用（1.1.24） ----------------

    /// <summary>專案選擇器：看得到的專案（依名稱排序，不分頁）。<paramref name="includeArchived"/>＝含已完成。</summary>
    [HttpGet("selectable")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<ProjectDto>>>> Selectable([FromQuery] bool includeArchived = false)
    {
        var access = await projectAccessService.GetAsync();
        var items = await projectService.GetSelectableAsync(includeArchived: includeArchived);
        return Ok(ApiResult<List<ProjectDto>>.SuccessResult(items.Select(x => ToDto(x, access, includeFiles: false)).ToList()));
    }

    /// <summary>上次在專案項目頁選的專案（記在帳號上；可能已刪除或看不到，畫面不在清單裡就退回第一筆）。</summary>
    [HttpGet("last-selected")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<LastSelectedProjectDto>>> LastSelected()
        => Ok(ApiResult<LastSelectedProjectDto>.SuccessResult(new LastSelectedProjectDto { ProjectId = await projectService.GetLastSelectedProjectIdAsync() }));

    /// <summary>記下選的專案（看不到的不記）。</summary>
    [HttpPut("last-selected")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult>> SaveLastSelected([FromBody] LastSelectedProjectDto request)
    {
        if (request.ProjectId is { } projectId)
        {
            await projectService.SaveLastSelectedProjectAsync(projectId);
        }

        return Ok(ApiResult.SuccessResult("已記錄。"));
    }

    /// <summary>啟用中的分類名稱（篩選與表單用，1.1.26）；不借用分類頁或會議頁的權限。</summary>
    [HttpGet("categories/enabled")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<string>>>> EnabledCategories()
        => Ok(ApiResult<List<string>>.SuccessResult(await categoryService.GetAllEnabledNamesAsync()));

    /// <summary>專案選擇器的團隊篩選選項（全部啟用中的團隊，1.1.26，同 Server 頁）；只要檢視權限。</summary>
    [HttpGet("teams/filter")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TeamOptionDto>>>> FilterTeams()
        => Ok(ApiResult<List<TeamOptionDto>>.SuccessResult(ToOptions(await projectService.GetSelectableCollaboratorTeamsAsync())));

    /// <summary>主責團隊選項：管理者看全部啟用中的團隊，一般使用者只看自己所屬的。</summary>
    [HttpGet("teams/primary")]
    [HasPermission(MagicObjectHelper.角色_專案項目, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<List<TeamOptionDto>>>> PrimaryTeams()
        => Ok(ApiResult<List<TeamOptionDto>>.SuccessResult(ToOptions(await projectService.GetSelectablePrimaryTeamsAsync())));

    /// <summary>協作團隊選項：全部啟用中的團隊，<paramref name="include"/>（專案已掛著、可能已停用的）也列進來。</summary>
    [HttpGet("teams/collaborator")]
    [HasPermission(MagicObjectHelper.角色_專案項目, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<List<TeamOptionDto>>>> CollaboratorTeams([FromQuery] List<int>? include)
        => Ok(ApiResult<List<TeamOptionDto>>.SuccessResult(ToOptions(await projectService.GetSelectableCollaboratorTeamsAsync(include))));

    /// <summary>負責人候選人：選的主責＋協作團隊裡啟用中的帳號。</summary>
    [HttpGet("owner-candidates")]
    [HasPermission(MagicObjectHelper.角色_專案項目, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<List<ProjectOwnerCandidateDto>>>> OwnerCandidates([FromQuery] int? primaryTeamId, [FromQuery] List<int>? collaboratorTeamIds)
    {
        var candidates = await projectService.GetOwnerCandidatesAsync(primaryTeamId, collaboratorTeamIds);
        return Ok(ApiResult<List<ProjectOwnerCandidateDto>>.SuccessResult(candidates
            .Select(x => new ProjectOwnerCandidateDto { Id = x.Id, Name = x.Name, Account = x.Account })
            .ToList()));
    }

    /// <summary>畫面的新增（整筆、可帶附件、日期可留空），規則與 Server 頁同一份。</summary>
    [HttpPost("form")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Create)]
    public Task<ActionResult<ApiResult<ProjectSaveResultDto>>> CreateForm([FromBody] ProjectFormDto form)
        => SaveFormAsync(form, isNew: true);

    /// <summary>畫面的修改（整筆覆寫：名單傳空就清空；可加附件、移除附件）。</summary>
    [HttpPut("{id}/form")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult<ProjectSaveResultDto>>> UpdateForm(int id, [FromBody] ProjectFormDto form)
    {
        if (id != form.Id)
        {
            return BadRequest(ApiResult<ProjectSaveResultDto>.FailureResult("路由 ID 與資料 ID 不一致"));
        }

        if ((await projectService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult<ProjectSaveResultDto>.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        return await SaveFormAsync(form, isNew: false);
    }

    /// <summary>下載附件（看得到專案就能下載；伺服器寫稽核）。</summary>
    [HttpGet("files/{fileId:int}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<IActionResult> DownloadFile(int fileId)
    {
        var download = await projectService.GetFileDownloadAsync(fileId, AuditVia.Api);
        return download is null
            ? NotFound(ApiResult.NotFoundResult("找不到這個附件。"))
            : File(download.Content, download.ContentType, download.DownloadFileName);
    }

    private async Task<ActionResult<ApiResult<ProjectSaveResultDto>>> SaveFormAsync(ProjectFormDto form, bool isNew)
    {
        var now = DateTime.Now;
        var model = new ProjectAdapterModel
        {
            Id = isNew ? 0 : form.Id,
            Title = form.Title,
            StartDate = form.StartDate,
            EndDate = form.EndDate,
            Status = form.Status,
            CompletionPercentage = form.CompletionPercentage,
            OwnerUserId = form.OwnerUserId,
            PrimaryTeamId = form.PrimaryTeamId,
            CollaboratorTeamIds = form.CollaboratorTeamIds ?? [],
            Categories = form.Categories ?? [],
            GlossaryTerms = form.GlossaryTerms ?? [],
            Participants = form.Participants ?? [],
            CreatedAt = now,
            UpdatedAt = now,
        };

        // 欄位驗證與 Server 頁的表單同一份（ProjectAdapterModel 的標註與 IValidatableObject）。
        var validation = new List<ValidationResult>();
        if (!Validator.TryValidateObject(model, new ValidationContext(model), validation, validateAllProperties: true))
        {
            return BadRequest(ApiResult<ProjectSaveResultDto>.FailureResult(validation[0].ErrorMessage ?? "資料不正確。"));
        }

        if (UserClaims.GetUserId(User) is not { } userId)
        {
            return Unauthorized(ApiResult<ProjectSaveResultDto>.UnauthorizedResult("無法識別使用者身分。"));
        }

        var uploads = new List<ProjectAttachmentUpload>();
        foreach (var uploadId in (form.UploadIds ?? []).Distinct())
        {
            if (!attachmentUploads.TryGet(uploadId, userId, out var upload) || upload.State != Dtos.Uploads.UploadStates.Succeeded)
            {
                return BadRequest(ApiResult<ProjectSaveResultDto>.FailureResult("找不到已上傳完成的附件（可能已逾時），請重新選擇檔案。"));
            }

            uploads.Add(upload);
        }

        var inputs = new List<ProjectUploadFileInput>();
        VerifyRecordResult result;
        try
        {
            inputs.AddRange(uploads.Select(x => x.OpenInput()));
            var check = isNew
                ? await projectService.BeforeAddCheckAsync(model, inputs)
                : await projectService.BeforeUpdateCheckAsync(model, inputs);
            if (!check.Success)
            {
                return BadRequest(ApiResult<ProjectSaveResultDto>.FailureResult(check.Message));
            }

            result = isNew
                ? await projectService.AddAsync(model, inputs, AuditVia.Api)
                : await projectService.UpdateAsync(model, inputs, form.RemovedFileIds ?? [], AuditVia.Api);
        }
        finally
        {
            // 先關讀檔串流、再刪暫存檔（1.0.65，Windows 上開著刪不掉）。
            foreach (var input in inputs)
            {
                await input.Content.DisposeAsync();
            }
        }

        if (!result.Success)
        {
            // 新增時專案已建立、只是附件存不進去：帶回編號讓畫面切成修改模式重送（暫存檔留著，可以直接重送）。
            var failure = ApiResult<ProjectSaveResultDto>.FailureResult(result.Message);
            if (model.Id > 0)
            {
                failure.Data = new ProjectSaveResultDto { ProjectId = model.Id };
            }

            return BadRequest(failure);
        }

        foreach (var upload in uploads)
        {
            attachmentUploads.Release(upload);
        }

        return Ok(ApiResult<ProjectSaveResultDto>.SuccessResult(new ProjectSaveResultDto { ProjectId = model.Id }, isNew ? "新增成功" : "修改成功"));
    }

    /// <summary>規則擋下回 400 並說明原因；例外才是伺服器錯誤。</summary>
    private ActionResult<ApiResult> Failure(VerifyRecordResult result)
        => result.Exception is null
            ? BadRequest(ApiResult.ValidationError(result.Message))
            : this.ApiServerError(result.Message, result.Exception);

    private ActionResult<ApiResult<T>> Failure<T>(VerifyRecordResult result)
        => result.Exception is null
            ? BadRequest(ApiResult<T>.ValidationError(result.Message))
            : this.ApiServerError<T>(result.Message, result.Exception);

    private static List<TeamOptionDto> ToOptions(IEnumerable<ProjectService.TeamOption> options)
        => options.Select(x => new TeamOptionDto { Id = x.Id, Name = x.Name }).ToList();

    /// <summary>轉成 DTO，並依呼叫者填權限旗標（規則同畫面；只決定按鈕，真正的檢查在服務層）。</summary>
    private static ProjectDto ToDto(ProjectAdapterModel model, ProjectAccess access, bool includeFiles) => new()
    {
        Id = model.Id,
        Title = model.Title,
        StartDate = model.StartDate,
        EndDate = model.EndDate,
        Status = model.Status,
        CompletionPercentage = model.CompletionPercentage,
        Owner = model.Owner,
        OwnerUserId = model.OwnerUserId,
        GlossaryTerms = [.. model.GlossaryTerms],
        Participants = [.. model.Participants],
        Categories = [.. model.Categories],
        PrimaryTeamId = model.PrimaryTeamId,
        CollaboratorTeamIds = [.. model.CollaboratorTeamIds],
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        PrimaryTeamName = model.PrimaryTeamName,
        CollaboratorTeamNames = [.. model.CollaboratorTeamNames],
        IsArchived = model.IsArchived,
        Files = includeFiles
            ? model.Files.Select(x => new ProjectFileDto { Id = x.Id, OriginalFileName = x.OriginalFileName, ContentType = x.ContentType, FileSize = x.FileSize, CreatedAt = x.CreatedAt }).ToList()
            : [],
        IsProjectOwner = access.IsProjectOwner(model.Id),
        CanManageTeams = access.CanManageProject(model.Id),
        CanChangeOwner = ProjectOwnerRules.CanChangeOwner(access, model.Id),
    };
}
