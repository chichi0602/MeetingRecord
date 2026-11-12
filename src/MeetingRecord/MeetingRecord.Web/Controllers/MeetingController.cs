using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Auth;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 會議紀錄 API。1.1.20 起改走畫面用的同一套 <see cref="MeetingService"/>：資料範圍、只限上傳者、
/// 封存唯讀、背景工作進行中不能動，全部只有一份規則。
/// 既有五支（GetById／Search／Create／Update／Delete）的路由與回應形狀不變、只多帶欄位；畫面專用的另開端點
/// （清單、重新轉錄、產生與修改會議紀錄、歸屬專案、逐字稿、下載與匯出、選項）。
/// 影音檔上傳走 <c>api/v1/uploads</c>（1.1.16）。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public class MeetingController(
    ILogger<MeetingController> logger,
    MeetingService meetingService,
    ProjectService projectService,
    PromptTemplateService promptTemplateService,
    CategoryService categoryService,
    ProjectAccessService projectAccessService,
    IPermissionChecker permissionChecker,
    IAuditLogService auditLogService,
    IPdfRenderer pdfRenderer,
    IMapper mapper) : ControllerBase
{
    private const string NotFoundMessage = "找不到 ID 為 {0} 的會議紀錄";
    private const string PdfContentType = "application/pdf";
    private const string TranscriptContentType = "text/plain;charset=utf-8";

    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<MeetingDto>>> GetById(int id)
    {
        var model = await meetingService.GetAsync(id);
        if (model.Id == 0)
        {
            return NotFound(ApiResult<MeetingDto>.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var dto = (await ToDtosAsync([model]))[0];
        dto.DraftContent = model.DraftContent;
        return Ok(ApiResult<MeetingDto>.SuccessResult(dto, "取得會議紀錄成功"));
    }

    /// <summary>對外的搜尋，行為同 1.1.19 以前（關鍵字、轉錄狀態、預設依更新時間）。</summary>
    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<MeetingDto>>>> Search([FromBody] MeetingSearchRequestDto request)
    {
        var (items, totalCount) = await meetingService.SearchFlatAsync(request);
        return Ok(ApiResult<PagedResult<MeetingDto>>.SuccessResult(new PagedResult<MeetingDto>
        {
            Items = await ToDtosAsync(items),
            TotalCount = totalCount,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
        }, "搜尋會議紀錄成功"));
    }

    /// <summary>新增：一律由資料庫配號、一律未歸屬，上傳者就是呼叫者（否則建完自己就看不到了）。</summary>
    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<MeetingDto>>> Create([FromBody] MeetingCreateUpdateDto meetingDto)
    {
        var model = ToModel(meetingDto);
        model.Id = 0;
        model.ProjectId = null;
        if (await meetingService.BeforeAddCheckAsync(model) is { Success: false } check)
        {
            return BadRequest(ApiResult<MeetingDto>.ValidationError(check.Message));
        }

        var result = await meetingService.AddAsync(model, AuditVia.Api);
        if (!result.Success)
        {
            return BadRequest(ApiResult<MeetingDto>.ValidationError(result.Message));
        }

        logger.LogInformation("Meeting created through API. MeetingId={MeetingId}", model.Id);
        return Ok(ApiResult<MeetingDto>.SuccessResult((await ToDtosAsync([await meetingService.GetAsync(model.Id)]))[0], "新增會議紀錄成功"));
    }

    /// <summary>修改中繼資料（標題、日期、描述、分類）。影音檔、轉錄、會議紀錄、歸屬一律沿用既有值。</summary>
    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] MeetingCreateUpdateDto meetingDto)
    {
        if (id != meetingDto.Id)
        {
            return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
        }

        if ((await meetingService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var model = ToModel(meetingDto);
        if (await meetingService.BeforeUpdateCheckAsync(model) is { Success: false } check)
        {
            return BadRequest(ApiResult.ValidationError(check.Message));
        }

        var result = await meetingService.UpdateAsync(model, AuditVia.Api);
        return result.Success
            ? Ok(ApiResult.SuccessResult("更新會議紀錄成功"))
            : BadRequest(ApiResult.ValidationError(result.Message));
    }

    // 1.0.47：刪除只限上傳者與管理者（1.0.52 起加專案負責人，服務層把關），所以這裡只要求「編輯」，與畫面一致。
    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
    {
        if ((await meetingService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        // 和畫面同一套，一併清掉影音檔、逐字稿與 AI 問答對話，背景工作進行中也會擋下（0.4.115）。
        var result = await meetingService.DeleteAsync(id, AuditVia.Api);
        return result.Success
            ? Ok(ApiResult.SuccessResult("刪除會議紀錄成功"))
            : BadRequest(ApiResult.ValidationError(result.Message));
    }

    // ---------------- 畫面專用（1.1.20） ----------------

    /// <summary>清單頁：分類篩選（任一）、畫面欄位排序。</summary>
    [HttpPost("list")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<MeetingDto>>>> List([FromBody] MeetingListRequestDto request)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var pageIndex = Math.Max(1, request.PageIndex);
        var result = await meetingService.GetAsync(new DataRequest
        {
            Search = request.Keyword ?? string.Empty,
            CategoryFilters = request.CategoryFilters ?? [],
            SortField = request.SortBy ?? string.Empty,
            SortDescending = request.SortDescending,
            CurrentPage = pageIndex,
            PageSize = pageSize,
        });

        return Ok(ApiResult<PagedResult<MeetingDto>>.SuccessResult(new PagedResult<MeetingDto>
        {
            Items = await ToDtosAsync(result.Result.ToList()),
            TotalCount = result.Count,
            PageIndex = pageIndex,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)pageSize),
        }));
    }

    /// <summary>從這場會議抽出的待辦數（刪除前的確認框要講清楚它們會留在專案裡）。</summary>
    [HttpGet("{id}/linked-todos")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult<int>>> LinkedTodos(int id)
    {
        if ((await meetingService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult<int>.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        return Ok(ApiResult<int>.SuccessResult(await meetingService.CountLinkedTodosAsync(id)));
    }

    /// <summary>重新轉錄（只限上傳者與管理者；已排入或轉錄中的會擋下，避免跑兩趟重複計費）。</summary>
    [HttpPost("{id}/retranscribe")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public Task<ActionResult<ApiResult>> Retranscribe(int id)
        => RunAsync(id, () => meetingService.RequeueTranscriptionAsync(id), "已重新排入轉錄。");

    /// <summary>產生（或重新產生）會議紀錄：會花 AI 費用，額度、封存、提示詞與專案的檢查都在服務層。</summary>
    [HttpPost("{id}/draft")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public Task<ActionResult<ApiResult>> RequestDraft(int id, [FromBody] MeetingDraftRequestDto request)
        => RunAsync(
            id,
            () => meetingService.RequestDraftAsync(id, request.ProjectId is > 0 ? request.ProjectId : null, request.PromptTemplateId, request.Attendees),
            "已排入產生會議紀錄。");

    /// <summary>修改會議紀錄全文。</summary>
    [HttpPut("{id}/draft")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public Task<ActionResult<ApiResult>> UpdateDraft(int id, [FromBody] MeetingDraftContentDto request)
        => RunAsync(id, () => meetingService.UpdateDraftAsync(id, request.Content), "會議紀錄已儲存。");

    /// <summary>把會議歸屬到專案（之後跟著專案的可見範圍走）。</summary>
    [HttpPut("{id}/project")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public Task<ActionResult<ApiResult>> AttachToProject(int id, [FromBody] MeetingAttachRequestDto request)
        => RunAsync(id, () => meetingService.AttachToProjectAsync(id, request.ProjectId), "已歸屬到專案。");

    /// <summary>逐字稿全文（看得到會議就看得到）。</summary>
    [HttpGet("{id}/transcript")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<MeetingTranscriptDto>>> Transcript(int id)
    {
        var model = await meetingService.GetAsync(id);
        if (model.Id == 0)
        {
            return NotFound(ApiResult<MeetingTranscriptDto>.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var text = model.HasTranscript ? await meetingService.ReadTranscriptAsync(id) : null;
        return Ok(ApiResult<MeetingTranscriptDto>.SuccessResult(new MeetingTranscriptDto { Text = text ?? string.Empty }));
    }

    /// <summary>下載逐字稿 .txt（UTF-8 含 BOM，記事本直接開也不亂碼）；下載算匯出，寫稽核。</summary>
    [HttpGet("{id}/transcript/file")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Export)]
    public async Task<IActionResult> TranscriptFile(int id)
    {
        var model = await meetingService.GetAsync(id);
        var text = model.Id == 0 || !model.HasTranscript ? null : await meetingService.ReadTranscriptAsync(id);
        if (string.IsNullOrEmpty(text))
        {
            return NotFound(ApiResult.NotFoundResult("這場會議沒有逐字稿。"));
        }

        var fileName = MeetingDocumentExporter.BuildTranscriptFileName(model);
        var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(text))
            .ToArray();
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.MeetingTranscriptDownload, targetType: "Meeting", targetId: id.ToString(),
            detail: $"file={fileName}; bytes={bytes.Length}; page=Meeting; via={AuditVia.Api}");
        return File(bytes, TranscriptContentType, fileName);
    }

    /// <summary>瀏覽器已把逐字稿複製到剪貼簿：寫稽核（複製算匯出）。</summary>
    [HttpPost("{id}/transcript/copied")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Export)]
    public async Task<ActionResult<ApiResult>> TranscriptCopied(int id)
    {
        var model = await meetingService.GetAsync(id);
        if (model.Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var length = model.HasTranscript ? (await meetingService.ReadTranscriptAsync(id))?.Length ?? 0 : 0;
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.MeetingTranscriptCopy, targetType: "Meeting", targetId: id.ToString(),
            detail: $"chars={length}; page=Meeting; via={AuditVia.Api}");
        return Ok(ApiResult.SuccessResult("已記錄。"));
    }

    /// <summary>
    /// 匯出會議紀錄 PDF。帶 <c>content</c> 就用它（編修中、還沒存的內容；複本只服務這一次匯出），沒帶用資料庫裡的。
    /// </summary>
    [HttpPost("{id}/draft/pdf")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Export)]
    public async Task<IActionResult> DraftPdf(int id, [FromBody] MeetingPdfRequestDto request)
    {
        var model = await meetingService.GetAsync(id);
        if (model.Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var snapshot = model.Clone();
        if (request.Content is not null)
        {
            snapshot.DraftContent = request.Content;
        }

        if (string.IsNullOrWhiteSpace(snapshot.DraftContent))
        {
            return BadRequest(ApiResult.FailureResult("這場會議還沒有會議紀錄。"));
        }

        var fileName = MeetingDocumentExporter.BuildFileName(snapshot);
        var pdf = await pdfRenderer.RenderAsync(MeetingDocumentExporter.BuildHtml(snapshot));
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.MeetingExportPdf, targetType: "Meeting", targetId: id.ToString(),
            detail: $"file={fileName}; bytes={pdf.Length}; page=Meeting; via={AuditVia.Api}");
        return File(pdf, PdfContentType, fileName);
    }

    /// <summary>會議頁可選的專案（產生會議紀錄、歸屬專案用），附與會人員名冊；不借用專案頁的權限。</summary>
    [HttpGet("projects/selectable")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<MeetingProjectOptionDto>>>> SelectableProjects()
    {
        var projects = await projectService.GetSelectableAsync();
        return Ok(ApiResult<List<MeetingProjectOptionDto>>.SuccessResult(projects
            .Select(x => new MeetingProjectOptionDto { Id = x.Id, Title = x.Title, Participants = [.. x.Participants] })
            .ToList()));
    }

    /// <summary>會議頁可選的提示詞（啟用中的）；不借用提示詞頁的權限。</summary>
    [HttpGet("prompt-templates/selectable")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<MeetingPromptOptionDto>>>> SelectablePromptTemplates()
    {
        var templates = await promptTemplateService.GetEnabledSelectableAsync();
        return Ok(ApiResult<List<MeetingPromptOptionDto>>.SuccessResult(templates
            .Select(x => new MeetingPromptOptionDto { Id = x.Id, Name = x.Name })
            .ToList()));
    }

    /// <summary>啟用中的分類名稱（表單與篩選用）；不借用分類頁的權限。</summary>
    [HttpGet("categories/enabled")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<string>>>> EnabledCategories()
        => Ok(ApiResult<List<string>>.SuccessResult(await categoryService.GetAllEnabledNamesAsync()));

    /// <summary>看不到的會議回 404；服務層的規則擋下回 400 與原文（放在 <c>errorMessage</c>）。</summary>
    private async Task<ActionResult<ApiResult>> RunAsync(int id, Func<Task<VerifyRecordResult>> action, string successMessage)
    {
        if ((await meetingService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(string.Format(NotFoundMessage, id)));
        }

        var result = await action();
        return result.Success
            ? Ok(ApiResult.SuccessResult(successMessage))
            : BadRequest(ApiResult.FailureResult(result.Message));
    }

    private MeetingAdapterModel ToModel(MeetingCreateUpdateDto dto)
    {
        var model = mapper.Map<MeetingAdapterModel>(dto);
        model.Categories = TagStringHelper.ToList(TagStringHelper.ToStored(dto.CategoryNames ?? TagStringHelper.ToList(dto.Categories)));
        return model;
    }

    /// <summary>
    /// 轉成 DTO：分類兩種格式都填，並依呼叫者填 <see cref="MeetingDto.IsUploader"/> 與 <see cref="MeetingDto.CanDelete"/>
    /// （規則同畫面：刪除要上傳者、專案負責人或管理者，加上會議紀錄的修改權限）。
    /// </summary>
    private async Task<List<MeetingDto>> ToDtosAsync(List<MeetingAdapterModel> models)
    {
        var access = await projectAccessService.GetAsync();
        var canEdit = UserClaims.GetUserId(User) is { } userId
            && await permissionChecker.HasPermissionAsync(userId, PermissionKey.For(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit));

        return models.Select(model =>
        {
            var dto = mapper.Map<MeetingDto>(model);
            dto.CategoryNames = [.. model.Categories];
            dto.Categories = TagStringHelper.ToStored(model.Categories);
            dto.IsUploader = access.IsMeetingOwner(model.CreatedByUserId);
            dto.CanDelete = canEdit
                && (dto.IsUploader || (model.ProjectId is { } projectId && access.IsProjectOwner(projectId)));
            return dto;
        }).ToList();
    }
}
