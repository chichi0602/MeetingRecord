using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;
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

    private const string SpeakerClipContentType = "audio/wav";

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
    public async Task<ActionResult<ApiResult>> Retranscribe(
        int id,
        [FromBody] TranscriptionOptionsDto? options,
        [FromServices] TranscriptionOptionsResolver transcriptionOptions)
    {
        // 1.1.73：沒帶選項＝沿用這場上次的選擇；帶了就先驗證這個組合能不能用。
        ResolvedTranscriptionOptions? resolved = null;
        if (options is not null)
        {
            (resolved, var error) = transcriptionOptions.Resolve(options);
            if (error is not null)
            {
                return BadRequest(ApiResult.FailureResult(error));
            }
        }

        return await RunAsync(id, () => meetingService.RequeueTranscriptionAsync(id, resolved), "已重新排入轉錄。");
    }

    /// <summary>
    /// 1.1.73：上傳／重新轉錄確認時可選的轉錄引擎、分講者與預設值。
    /// 講者上限的預設值取專案與會人員名冊的人數（看不到的專案當成沒有專案）。
    /// </summary>
    [HttpGet("transcription-options")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<TranscriptionOptionsMetaDto>>> TranscriptionOptions(
        [FromQuery] int? projectId,
        [FromServices] TranscriptionOptionsResolver transcriptionOptions)
    {
        int? participantCount = null;
        if (projectId is { } pid)
        {
            participantCount = (await projectService.GetSelectableAsync())
                .FirstOrDefault(x => x.Id == pid)?.Participants.Count;
        }

        return Ok(ApiResult<TranscriptionOptionsMetaDto>.SuccessResult(transcriptionOptions.GetMeta(participantCount)));
    }

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

    /// <summary>「標示講者」視窗的資料：整場對齊的講者、目前的對照、範例句子與專案名冊（1.1.64）。</summary>
    [HttpGet("{id}/speakers")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<MeetingSpeakersDto>>> Speakers(int id)
    {
        var speakers = await meetingService.GetSpeakersAsync(id);
        return speakers is null
            ? NotFound(ApiResult<MeetingSpeakersDto>.NotFoundResult(string.Format(NotFoundMessage, id)))
            : Ok(ApiResult<MeetingSpeakersDto>.SuccessResult(speakers));
    }

    /// <summary>儲存講者對照（逐字稿檔案不改，讀取時才套用）。</summary>
    [HttpPut("{id}/speakers")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public Task<ActionResult<ApiResult>> UpdateSpeakers(int id, [FromBody] MeetingSpeakerNamesDto request, [FromQuery] bool generate = false)
        => generate
            // 1.1.69：轉錄完等標示講者的會議，存名字後用上傳時選的提示詞產生會議紀錄。
            ? RunAsync(id, () => meetingService.NameSpeakersAndGenerateAsync(id, request.Names), "講者名字已儲存，開始產生會議紀錄。")
            : RunAsync(id, () => meetingService.UpdateSpeakerNamesAsync(id, request.Names), "講者名字已儲存。");

    /// <summary>1.1.71：某位講者的試聽片段（WAV，支援 Range）。同網域 &lt;audio src&gt; 會自動帶登入 Cookie；GET 不需要防偽標頭。</summary>
    [HttpGet("{id}/speakers/{label}/clip")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<IActionResult> SpeakerClip(int id, string label)
    {
        var path = await meetingService.GetSpeakerClipPathAsync(id, label);
        return path is null
            ? NotFound(ApiResult.NotFoundResult("找不到這位講者的試聽片段。"))
            : PhysicalFile(path, SpeakerClipContentType, enableRangeProcessing: true);
    }

    /// <summary>1.1.71：會議紀錄的舊版清單（新到舊，不含內容）。</summary>
    [HttpGet("{id}/draft/versions")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<MeetingDraftVersionDto>>>> DraftVersions(int id)
    {
        var versions = await meetingService.GetDraftVersionsAsync(id);
        return versions is null
            ? NotFound(ApiResult<List<MeetingDraftVersionDto>>.NotFoundResult(string.Format(NotFoundMessage, id)))
            : Ok(ApiResult<List<MeetingDraftVersionDto>>.SuccessResult(versions));
    }

    /// <summary>1.1.71：某一份舊版的內容。</summary>
    [HttpGet("{id}/draft/versions/{versionId:int}")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<MeetingDraftVersionDetailDto>>> DraftVersion(int id, int versionId)
    {
        var version = await meetingService.GetDraftVersionAsync(id, versionId);
        return version is null
            ? NotFound(ApiResult<MeetingDraftVersionDetailDto>.NotFoundResult("找不到這個版本。"))
            : Ok(ApiResult<MeetingDraftVersionDetailDto>.SuccessResult(version));
    }

    /// <summary>1.1.71：還原成某一份舊版（目前的內容會先留成一版）。</summary>
    [HttpPost("{id}/draft/versions/{versionId:int}/restore")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit)]
    public Task<ActionResult<ApiResult>> RestoreDraftVersion(int id, int versionId)
        => RunAsync(id, () => meetingService.RestoreDraftVersionAsync(id, versionId), "已還原這個版本。");

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

    /// <summary>
    /// 1.1.68 成本閘門：上傳／重新轉錄前的預估費用、提醒門檻與本月剩餘額度。秒數由畫面讀檔案長度或用上次轉錄的長度。
    /// 只是給確認視窗用的提示；真正的擋下在轉錄工作量到長度之後（<c>TranscriptionJobRunner</c>）。
    /// </summary>
    [HttpGet("transcription-estimate")]
    [HasPermission(MagicObjectHelper.角色_會議紀錄, new[] { PermissionActions.Create, PermissionActions.Edit })]
    public async Task<ActionResult<ApiResult<MeetingTranscriptionEstimateDto>>> TranscriptionEstimate(
        [FromQuery] double seconds,
        [FromServices] MeetingRecord.Business.Services.AiUsage.TranscriptionCostEstimator estimator,
        [FromServices] TranscriptionOptionsResolver transcriptionOptions,
        [FromQuery] int? engine = null,
        [FromQuery] bool? diarize = null)
    {
        if (double.IsNaN(seconds) || seconds <= 0 || seconds > TimeSpan.FromDays(2).TotalSeconds)
        {
            return BadRequest(ApiResult<MeetingTranscriptionEstimateDto>.FailureResult("時長不正確。"));
        }

        // 1.1.73：依選的引擎與分講者用那個引擎／部署的單價；沒帶就照設定的部署（舊行為）。
        var estimated = engine is { } selected && Enum.IsDefined(typeof(TranscriptionEngine), selected)
            ? estimator.EstimateTwd(seconds, transcriptionOptions.PricingModel((TranscriptionEngine)selected, diarize))
            : estimator.EstimateTwd(seconds);

        return Ok(ApiResult<MeetingTranscriptionEstimateDto>.SuccessResult(new MeetingTranscriptionEstimateDto
        {
            Seconds = seconds,
            EstimatedTwd = estimated,
            ConfirmThresholdTwd = estimator.ConfirmThresholdTwd,
            RemainingTwd = await estimator.GetRemainingTwdAsync(UserClaims.GetUserId(User)),
        }));
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
