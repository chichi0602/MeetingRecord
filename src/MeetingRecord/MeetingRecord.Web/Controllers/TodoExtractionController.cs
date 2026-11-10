using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.TodoExtraction;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Todos;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 從會議紀錄抽出待辦（1.1.25，WASM 遷移 M3）：專案頁的「抽出待辦」視窗用。
///
/// <para>
/// 權限同 Server 專案頁：要「待辦事項」的新增（抽出來就是要建待辦；更新既有待辦也在同一個視窗裡）。
/// 抽取會花 AI 費用，資料範圍、封存（1.1.25 起服務層也擋）、用量上限都在 <c>TodoExtractionService</c>；
/// 新增與更新走 <c>TodoService</c>，規則與 Server 頁同一份，稽核來源照舊記 <c>via=AiExtract</c>。
/// </para>
/// </summary>
[Route("api/v1/todo-extraction")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class TodoExtractionController(
    TodoExtractionService extractionService,
    TodoService todoService,
    MeetingService meetingService) : ControllerBase
{
    /// <summary>抽出待辦候選（會花 AI 費用），連同先前已加入過的標題與本專案未完成的待辦。</summary>
    [HttpPost("meetings/{meetingId:int}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<TodoExtractionResultDto>>> Extract(int meetingId, CancellationToken cancellationToken)
    {
        if ((await meetingService.GetAsync(meetingId)).Id == 0)
        {
            return NotFound(ApiResult<TodoExtractionResultDto>.NotFoundResult("找不到這場會議。"));
        }

        try
        {
            var existingTitles = await extractionService.GetExistingTitlesAsync(meetingId, cancellationToken);
            var openTodos = await extractionService.GetOpenTodosAsync(meetingId, cancellationToken);
            var extracted = await extractionService.ExtractAsync(meetingId, cancellationToken, openTodos);
            return Ok(ApiResult<TodoExtractionResultDto>.SuccessResult(new TodoExtractionResultDto
            {
                Items = extracted.Select(x => new ExtractedTodoDto
                {
                    Title = x.Title,
                    Description = x.Description,
                    Owner = x.Owner,
                    DueDate = x.DueDate,
                    Priority = x.Priority,
                    MatchedTodoId = x.MatchedTodoId,
                    ParentTodoId = x.ParentTodoId,
                }).ToList(),
                ExistingTitles = [.. existingTitles],
                OpenTodos = openTodos.Select(x => new OpenTodoSummaryDto
                {
                    Id = x.Id,
                    Title = x.Title,
                    Owner = x.Owner,
                    DueDate = x.DueDate,
                    Status = x.Status,
                    MeetingTitle = x.MeetingTitle,
                    IsSubtask = x.IsSubtask,
                }).ToList(),
            }));
        }
        catch (InvalidOperationException ex)
        {
            // 服務層擋下（沒有會議紀錄、已完成的專案、用量上限、沒有設定 AI）時的訊息是給使用者看的。
            return BadRequest(ApiResult<TodoExtractionResultDto>.FailureResult(ex.Message));
        }
    }

    /// <summary>把一條候選加為新待辦（可掛到主待辦底下）。</summary>
    [HttpPost("todos")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<int>>> Add([FromBody] ExtractedTodoAddRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return BadRequest(ApiResult<int>.FailureResult("待辦沒有標題。"));
        }

        var model = TodoExtractionService.ToAdapterModel(
            new ExtractedTodo(
                request.Title.Trim(),
                request.Description,
                string.IsNullOrWhiteSpace(request.Owner) ? null : request.Owner.Trim(),
                request.DueDate,
                request.Priority),
            request.ProjectId,
            request.MeetingId);
        model.ParentTodoId = request.ParentTodoId;

        var check = await todoService.BeforeAddCheckAsync(model);
        if (!check.Success)
        {
            return BadRequest(ApiResult<int>.FailureResult(check.Message));
        }

        var result = await todoService.AddAsync(model, AuditVia.AiExtract);
        return result.Success
            ? Ok(ApiResult<int>.SuccessResult(model.Id))
            : BadRequest(ApiResult<int>.FailureResult(result.Message));
    }

    /// <summary>用候選更新既有待辦：只改期限與負責人，異動紀錄記下來源會議並通知專案成員。</summary>
    [HttpPut("todos/{todoId:int}")]
    [HasPermission(MagicObjectHelper.角色_待辦事項, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult>> ApplyUpdate(int todoId, [FromBody] ExtractedTodoUpdateRequestDto request)
    {
        var meeting = await meetingService.GetAsync(request.MeetingId);
        if (meeting.Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult("找不到這場會議。"));
        }

        var result = await todoService.ApplyExtractedUpdateAsync(
            todoId,
            request.DueDate,
            string.IsNullOrWhiteSpace(request.Owner) ? null : request.Owner.Trim(),
            meeting.Id,
            meeting.Title);
        return result.Success ? Ok(ApiResult.SuccessResult("已更新。")) : BadRequest(ApiResult.FailureResult(result.Message));
    }
}
