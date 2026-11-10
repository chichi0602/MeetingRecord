using AutoMapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Dtos.Projects;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 專案項目頁裡的會議（1.1.25，WASM 遷移 M3）：歷史會議、產生與修改會議紀錄、逐字稿、PDF、移出專案、待辦 PDF。
///
/// <para>
/// 權限一律看「專案項目」（同 Server 專案頁），不借用會議紀錄頁的權限——有專案權限、沒有會議紀錄頁權限的人也要能用。
/// 資料範圍與封存、上傳者、生成中等規則都在 <c>MeetingService</c>；這裡另外確認會議屬於這個專案，不是的一律 404。
/// 稽核摘要照 Server 專案頁（<c>page=Project</c>），API 另加 <c>via=Api</c>。
/// </para>
/// </summary>
[Route("api/v1/Project/{projectId:int}")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class ProjectMeetingController(
    MeetingService meetingService,
    ProjectService projectService,
    PromptTemplateService promptTemplateService,
    TodoService todoService,
    ProjectAccessService projectAccessService,
    IAuditLogService auditLogService,
    IAuditActorProvider actorProvider,
    IPdfRenderer pdfRenderer,
    IOptions<SystemSettings> systemSettings,
    IMapper mapper) : ControllerBase
{
    private const string PdfContentType = "application/pdf";
    private const string TranscriptContentType = "text/plain;charset=utf-8";
    private const string ProjectNotFound = "找不到這個專案。";
    private const string MeetingNotFound = "找不到這個專案底下的會議。";

    /// <summary>專案底下的會議（看得到的；依會議紀錄完成時間由新到舊）。清單不帶會議紀錄全文。</summary>
    [HttpGet("meetings")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<MeetingDto>>>> Meetings(int projectId)
    {
        if ((await projectService.GetAsync(projectId)).Id == 0)
        {
            return NotFound(ApiResult<List<MeetingDto>>.NotFoundResult(ProjectNotFound));
        }

        var access = await projectAccessService.GetAsync();
        var meetings = await meetingService.GetByProjectAsync(projectId);
        return Ok(ApiResult<List<MeetingDto>>.SuccessResult(meetings.Select(x => ToDto(x, access, includeDraft: false)).ToList()));
    }

    /// <summary>單一會議（含會議紀錄全文，開「會議紀錄與逐字稿」視窗用）。</summary>
    [HttpGet("meetings/{meetingId:int}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<MeetingDto>>> Meeting(int projectId, int meetingId)
    {
        var meeting = await FindMeetingAsync(projectId, meetingId);
        return meeting is null
            ? NotFound(ApiResult<MeetingDto>.NotFoundResult(MeetingNotFound))
            : Ok(ApiResult<MeetingDto>.SuccessResult(ToDto(meeting, await projectAccessService.GetAsync(), includeDraft: true)));
    }

    /// <summary>「AI 轉會議紀錄」可選的逐字稿：轉錄完成、看得到的會議（不限這個專案；選了未歸屬的會順便歸屬進來）。</summary>
    [HttpGet("transcripts/selectable")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<ProjectTranscriptOptionDto>>>> SelectableTranscripts(int projectId)
    {
        var items = await meetingService.GetSelectableTranscriptsAsync();
        return Ok(ApiResult<List<ProjectTranscriptOptionDto>>.SuccessResult(items
            .Select(x => new ProjectTranscriptOptionDto { Id = x.Id, Title = x.Title, MeetingDate = x.MeetingDate, ProjectId = x.ProjectId, ProjectTitle = x.ProjectTitle })
            .ToList()));
    }

    /// <summary>可選的提示詞（啟用中的）；不借用提示詞頁的權限。</summary>
    [HttpGet("prompt-templates/selectable")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<MeetingPromptOptionDto>>>> SelectablePromptTemplates(int projectId)
    {
        var templates = await promptTemplateService.GetEnabledSelectableAsync();
        return Ok(ApiResult<List<MeetingPromptOptionDto>>.SuccessResult(templates.Select(x => new MeetingPromptOptionDto { Id = x.Id, Name = x.Name }).ToList()));
    }

    /// <summary>
    /// 產生（或重新產生）會議紀錄並歸屬到這個專案：會花 AI 費用，額度、封存、提示詞、已歸屬別的專案、生成中的檢查都在服務層。
    /// 會議可以還沒歸屬（從可選逐字稿挑的），所以這裡不先檢查「屬於這個專案」。
    /// </summary>
    [HttpPost("meetings/{meetingId:int}/draft")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> RequestDraft(int projectId, int meetingId, [FromBody] MeetingDraftRequestDto request)
    {
        if ((await projectService.GetAsync(projectId)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(ProjectNotFound));
        }

        var result = await meetingService.RequestDraftAsync(meetingId, projectId, request.PromptTemplateId, request.Attendees);
        return result.Success ? Ok(ApiResult.SuccessResult("已排入產生會議紀錄。")) : BadRequest(ApiResult.FailureResult(result.Message));
    }

    /// <summary>修改會議紀錄全文。</summary>
    [HttpPut("meetings/{meetingId:int}/draft")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> UpdateDraft(int projectId, int meetingId, [FromBody] MeetingDraftContentDto request)
    {
        if (await FindMeetingAsync(projectId, meetingId) is null)
        {
            return NotFound(ApiResult.NotFoundResult(MeetingNotFound));
        }

        var result = await meetingService.UpdateDraftAsync(meetingId, request.Content);
        return result.Success ? Ok(ApiResult.SuccessResult("會議紀錄已儲存。")) : BadRequest(ApiResult.FailureResult(result.Message));
    }

    /// <summary>
    /// 從專案移除（只限上傳者與管理者、封存唯讀，服務層把關）。權限同 Server 專案頁：專案項目的「修改」或「刪除」任一。
    /// </summary>
    [HttpDelete("meetings/{meetingId:int}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, new[] { PermissionActions.Edit, PermissionActions.Delete })]
    public async Task<ActionResult<ApiResult>> Detach(int projectId, int meetingId)
    {
        if (await FindMeetingAsync(projectId, meetingId) is null)
        {
            return NotFound(ApiResult.NotFoundResult(MeetingNotFound));
        }

        var result = await meetingService.DetachFromProjectAsync(meetingId, projectId);
        return result.Success ? Ok(ApiResult.SuccessResult("已從專案移除。")) : BadRequest(ApiResult.FailureResult(result.Message));
    }

    /// <summary>逐字稿全文。</summary>
    [HttpGet("meetings/{meetingId:int}/transcript")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<MeetingTranscriptDto>>> Transcript(int projectId, int meetingId)
    {
        var meeting = await FindMeetingAsync(projectId, meetingId);
        if (meeting is null)
        {
            return NotFound(ApiResult<MeetingTranscriptDto>.NotFoundResult(MeetingNotFound));
        }

        var text = meeting.HasTranscript ? await meetingService.ReadTranscriptAsync(meetingId) : null;
        return Ok(ApiResult<MeetingTranscriptDto>.SuccessResult(new MeetingTranscriptDto { Text = text ?? string.Empty }));
    }

    /// <summary>下載逐字稿 .txt（UTF-8 含 BOM）；下載算匯出，寫稽核。</summary>
    [HttpGet("meetings/{meetingId:int}/transcript/file")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Export)]
    public async Task<IActionResult> TranscriptFile(int projectId, int meetingId)
    {
        var meeting = await FindMeetingAsync(projectId, meetingId);
        var text = meeting is { HasTranscript: true } ? await meetingService.ReadTranscriptAsync(meetingId) : null;
        if (meeting is null || string.IsNullOrEmpty(text))
        {
            return NotFound(ApiResult.NotFoundResult("這場會議沒有逐字稿。"));
        }

        var fileName = MeetingDocumentExporter.BuildTranscriptFileName(meeting);
        var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(System.Text.Encoding.UTF8.GetBytes(text))
            .ToArray();
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.MeetingTranscriptDownload, targetType: "Meeting", targetId: meetingId.ToString(),
            detail: $"file={fileName}; bytes={bytes.Length}; page=Project; via={AuditVia.Api}");
        return File(bytes, TranscriptContentType, fileName);
    }

    /// <summary>瀏覽器已把逐字稿複製到剪貼簿：寫稽核（複製算匯出）。</summary>
    [HttpPost("meetings/{meetingId:int}/transcript/copied")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Export)]
    public async Task<ActionResult<ApiResult>> TranscriptCopied(int projectId, int meetingId)
    {
        var meeting = await FindMeetingAsync(projectId, meetingId);
        if (meeting is null)
        {
            return NotFound(ApiResult.NotFoundResult(MeetingNotFound));
        }

        var length = meeting.HasTranscript ? (await meetingService.ReadTranscriptAsync(meetingId))?.Length ?? 0 : 0;
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.MeetingTranscriptCopy, targetType: "Meeting", targetId: meetingId.ToString(),
            detail: $"chars={length}; page=Project; via={AuditVia.Api}");
        return Ok(ApiResult.SuccessResult("已記錄。"));
    }

    /// <summary>會議紀錄 PDF（表頭帶專案名稱）。帶 <c>content</c> 就用它（編修中、還沒存的內容），沒帶用資料庫裡的。</summary>
    [HttpPost("meetings/{meetingId:int}/draft/pdf")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Export)]
    public async Task<IActionResult> DraftPdf(int projectId, int meetingId, [FromBody] MeetingPdfRequestDto request)
    {
        var meeting = await FindMeetingAsync(projectId, meetingId);
        if (meeting is null)
        {
            return NotFound(ApiResult.NotFoundResult(MeetingNotFound));
        }

        var snapshot = meeting.Clone();
        if (request.Content is not null)
        {
            snapshot.DraftContent = request.Content;
        }

        if (string.IsNullOrWhiteSpace(snapshot.DraftContent))
        {
            return BadRequest(ApiResult.FailureResult("這場會議還沒有會議紀錄。"));
        }

        var fileName = MeetingDocumentExporter.BuildFileName(snapshot);
        var pdf = await pdfRenderer.RenderAsync(MeetingDocumentExporter.BuildHtml(snapshot, meeting.ProjectTitle));
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.MeetingExportPdf, targetType: "Meeting", targetId: meetingId.ToString(),
            detail: $"file={fileName}; bytes={pdf.Length}; page=Project; via={AuditVia.Api}");
        return File(pdf, PdfContentType, fileName);
    }

    /// <summary>專案全部待辦的 PDF 總覽（1.0.54，看進度用）。唯讀動作，已完成（封存）的專案也能匯出。</summary>
    [HttpGet("todos/pdf")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Export)]
    public async Task<IActionResult> TodosPdf(int projectId)
    {
        var project = await projectService.GetAsync(projectId);
        if (project.Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult(ProjectNotFound));
        }

        var exportedAt = DateTime.Now;
        var todos = await todoService.GetForProjectAsync(projectId);
        var html = ProjectTodoReportExporter.BuildHtml(new ProjectTodoReportRequest(
            systemSettings.Value.SystemInformation.SystemVersion,
            actorProvider.GetActor().Account ?? string.Empty,
            exportedAt,
            project,
            todos));
        var pdf = await pdfRenderer.RenderAsync(html);
        var fileName = ProjectTodoReportExporter.BuildFileName(project, exportedAt);
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.ProjectTodoExportPdf, targetType: "Project", targetId: projectId.ToString(),
            detail: $"file={fileName}; todos={todos.Count}; bytes={pdf.Length}; via={AuditVia.Api}");
        return File(pdf, PdfContentType, fileName);
    }

    /// <summary>看得到、而且屬於這個專案的會議；不是的回 null（呼叫端回 404）。</summary>
    private async Task<MeetingAdapterModel?> FindMeetingAsync(int projectId, int meetingId)
    {
        var meeting = await meetingService.GetAsync(meetingId);
        return meeting.Id != 0 && meeting.ProjectId == projectId ? meeting : null;
    }

    private MeetingDto ToDto(MeetingAdapterModel model, ProjectAccess access, bool includeDraft)
    {
        var dto = mapper.Map<MeetingDto>(model);
        dto.CategoryNames = [.. model.Categories];
        dto.Categories = TagStringHelper.ToStored(model.Categories);
        dto.IsUploader = access.IsMeetingOwner(model.CreatedByUserId);
        dto.DraftContent = includeDraft ? model.DraftContent : null;
        return dto;
    }
}
