using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.AiChats;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.AiChat;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Streaming;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// AI 問答（1.1.21，WASM 遷移 M3）：<c>api/v1/ai-chat/{project|meeting}/{targetId}/...</c>。
/// 規則都在 <see cref="AiChatService"/>：資料範圍（看不到的當成不存在 → 404）、附件格式與大小、額度與用量帳本。
/// 動作權限依範圍決定（<see cref="AiChatPermissionAttribute"/>）：專案看專案項目，會議看會議紀錄或專案項目。
///
/// <para>
/// 提問與重新產生會花 AI 費用、而且要邊產生邊顯示：回應是串流（每行一筆 JSON，見 <see cref="AiChatStreamEventDto"/>）。
/// 在第一段文字產生之前就失敗（例如看不到、對話已刪、附件不合規定、額度用完）一樣回一般的 404／400，
/// 開始產生之後才失敗則在串流最後送一筆 <c>error</c>。
/// </para>
/// </summary>
[Route("api/v1/ai-chat/{scope:regex(^(project|meeting)$)}/{targetId:int}")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed class AiChatController(
    AiChatService chatService,
    AiChatOperations operations,
    ILogger<AiChatController> logger) : ControllerBase
{
    /// <summary>服務層「看不到」的訊息：對外當成不存在。</summary>
    private const string NotVisibleMessage = "找不到這個專案或會議，可能已被刪除或沒有權限。";

    /// <summary>附件總量上限：一次最多 <see cref="AiChatAttachmentPolicy.MaxAttachmentsPerQuestion"/> 個、單一文件上限 20 MB，再留一點給問題本身。</summary>
    private const long MaxAskRequestBytes = AiChatAttachmentPolicy.MaxAttachmentsPerQuestion * AiChatAttachmentPolicy.MaxDocumentBytes + 1024 * 1024;

    [HttpGet("conversations")]
    [AiChatPermission(PermissionActions.View)]
    public Task<IActionResult> ListConversations(string scope, int targetId, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var items = await chatService.ListConversationsAsync(ToScope(scope), targetId, cancellationToken);
            return Ok(ApiResult<List<AiChatConversationDto>>.SuccessResult([.. items.Select(AiChatOperations.ToDto)]));
        });

    /// <summary>開新對話（回新對話的 Id）。<c>automatic=true</c>：開視窗時自動建的第一段，不算使用者的動作、不寫稽核（1.1.22）。</summary>
    [HttpPost("conversations")]
    [AiChatPermission(PermissionActions.View)]
    public Task<IActionResult> CreateConversation(string scope, int targetId, [FromQuery] bool automatic, CancellationToken cancellationToken)
        => RunAsync(async () => Ok(ApiResult<string>.SuccessResult(await chatService.CreateConversationAsync(ToScope(scope), targetId, cancellationToken, audit: !automatic))));

    /// <summary>重新命名對話（要修改權限）。</summary>
    [HttpPut("conversations/{conversationId}/title")]
    [AiChatPermission(PermissionActions.Edit)]
    public Task<IActionResult> Rename(string scope, int targetId, string conversationId, [FromBody] AiChatRenameRequestDto request, CancellationToken cancellationToken)
        => RunAsync(async () => Outcome(await chatService.RenameConversationAsync(ToScope(scope), targetId, conversationId, request.Title, cancellationToken), "已重新命名。"));

    /// <summary>刪除整段對話（要刪除權限）。</summary>
    [HttpDelete("conversations/{conversationId}")]
    [AiChatPermission(PermissionActions.Delete)]
    public Task<IActionResult> Delete(string scope, int targetId, string conversationId, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            await chatService.ClearHistoryAsync(ToScope(scope), targetId, conversationId, cancellationToken);
            return Ok(ApiResult.SuccessResult("已刪除對話。"));
        });

    [HttpGet("conversations/{conversationId}/messages")]
    [AiChatPermission(PermissionActions.View)]
    public Task<IActionResult> History(string scope, int targetId, string conversationId, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var items = await chatService.GetHistoryAsync(ToScope(scope), targetId, conversationId, cancellationToken);
            return Ok(ApiResult<List<AiChatMessageDto>>.SuccessResult([.. items.Select(AiChatOperations.ToDto)]));
        });

    /// <summary>修改一則訊息（要修改權限）：原本的角色與內容對不上回 409（多半是別人先改過）。</summary>
    [HttpPut("conversations/{conversationId}/messages/{index:int}")]
    [AiChatPermission(PermissionActions.Edit)]
    public Task<IActionResult> UpdateMessage(string scope, int targetId, string conversationId, int index, [FromBody] AiChatUpdateMessageRequestDto request, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var original = new AiChatMessageItem(request.Role, request.Content, null, default);
            return Outcome(await chatService.UpdateMessageAsync(ToScope(scope), targetId, conversationId, index, original, request.NewContent, cancellationToken), "已修改。");
        });

    /// <summary>提問（multipart：<c>question</c> 與 0～5 個 <c>files</c>）。回應是串流。</summary>
    [HttpPost("conversations/{conversationId}/ask")]
    [AiChatPermission(PermissionActions.View)]
    [RequestSizeLimit(MaxAskRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxAskRequestBytes)]
    public async Task Ask(string scope, int targetId, string conversationId, [FromForm] string question, [FromForm] List<IFormFile>? files, CancellationToken cancellationToken)
    {
        var attachments = new List<PendingAttachment>();
        foreach (var file in files ?? [])
        {
            using var memory = new MemoryStream();
            await file.CopyToAsync(memory, cancellationToken);
            attachments.Add(new PendingAttachment(file.FileName, memory.ToArray()));
        }

        await StreamAsync(onDelta => chatService.AskAsync(ToScope(scope), targetId, conversationId, question, onDelta, cancellationToken, attachments), cancellationToken);
    }

    /// <summary>改問題並重新產生回答（要修改權限）。回應是串流。</summary>
    [HttpPost("conversations/{conversationId}/messages/{questionIndex:int}/regenerate")]
    [AiChatPermission(PermissionActions.Edit)]
    public Task Regenerate(string scope, int targetId, string conversationId, int questionIndex, [FromBody] AiChatRegenerateRequestDto request, CancellationToken cancellationToken)
        => StreamAsync(onDelta => chatService.RegenerateAsync(ToScope(scope), targetId, conversationId, questionIndex, request.ExpectedQuestion, request.NewQuestion, onDelta, cancellationToken), cancellationToken);

    /// <summary>
    /// 讀提問附上的檔案。只認這段對話裡真的有的附件（用 <paramref name="storedName"/> 找），不收用戶端給的檔案資訊。
    /// <c>download=true</c>（文件下載）寫稽核；圖片預覽不寫。
    /// </summary>
    [HttpGet("conversations/{conversationId}/attachments/{storedName}")]
    [AiChatPermission(PermissionActions.View)]
    public Task<IActionResult> Attachment(string scope, int targetId, string conversationId, string storedName, [FromQuery] bool download, CancellationToken cancellationToken)
        => RunAsync(async () =>
            await operations.ReadAttachmentAsync(ToScope(scope), targetId, conversationId, storedName, download, AuditVia.Api, cancellationToken) is { } file
                ? File(file.Content, file.ContentType, file.FileName)
                : NotFound(ApiResult.NotFoundResult("找不到這個附件，可能已被刪除。")));

    /// <summary>匯出 PDF（要匯出權限）：一則或整段對話，內容一律從對話檔讀。</summary>
    [HttpPost("conversations/{conversationId}/pdf")]
    [AiChatPermission(PermissionActions.Export)]
    public Task<IActionResult> ExportPdf(string scope, int targetId, string conversationId, [FromBody] AiChatExportRequestDto request, CancellationToken cancellationToken)
        => RunAsync(async () =>
            await operations.ExportPdfAsync(ToScope(scope), targetId, conversationId, request.MessageIndex, AuditVia.Api, cancellationToken) is { } file
                ? File(file.Content, file.ContentType, file.FileName)
                : NotFound(ApiResult.NotFoundResult("找不到要匯出的內容。")));

    private static AiChatScope ToScope(string scope)
        => string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase) ? AiChatScope.Project : AiChatScope.Meeting;

    /// <summary>服務層的例外都是 <see cref="InvalidOperationException"/>（含額度用完、AI 服務錯誤）：「看不到」回 404，其他回 400 與原文。</summary>
    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InvalidOperationException ex)
        {
            return Failure(ex);
        }
    }

    private ObjectResult Failure(InvalidOperationException ex) => ex.Message == NotVisibleMessage
        ? NotFound(ApiResult.NotFoundResult(ex.Message))
        : BadRequest(ApiResult.FailureResult(ex.Message));

    private IActionResult Outcome(UpdateOutcome outcome, string successMessage) => outcome switch
    {
        UpdateOutcome.Updated => Ok(ApiResult.SuccessResult(successMessage)),
        UpdateOutcome.NotFound => NotFound(ApiResult.NotFoundResult("這段對話已經被刪除或清空了，請重新整理後再試。")),
        _ => Conflict(ApiResult.FailureResult("這則訊息已經被別人改過，請重新整理後再試。", StatusCodes.Status409Conflict)),
    };

    /// <summary>
    /// 串流（<see cref="NdjsonStreamer"/>，1.1.29 抽出與使用說明共用）。
    /// 第一段文字出來之前就失敗，照一般 API 回 404／400；開始寫回應之後失敗，送一筆 <c>error</c>。
    /// </summary>
    private Task StreamAsync(Func<Action<string>, Task<AiChatAnswer>> run, CancellationToken cancellationToken)
        => NdjsonStreamer.RunAsync(
            this,
            run,
            delta => new AiChatStreamEventDto { Type = AiChatStreamEventDto.Delta, Text = delta },
            answer => new AiChatStreamEventDto { Type = AiChatStreamEventDto.Done, Answer = AiChatOperations.ToDto(answer) },
            error =>
            {
                if (error is InvalidOperationException invalid)
                {
                    return Failure(invalid);
                }

                logger.LogError(error, "AI chat streaming failed before the first delta.");
                return new ObjectResult(ApiResult.FailureResult("AI 問答發生錯誤，請稍後再試。", StatusCodes.Status500InternalServerError))
                {
                    StatusCode = StatusCodes.Status500InternalServerError,
                };
            },
            ex =>
            {
                if (ex is not InvalidOperationException)
                {
                    logger.LogError(ex, "AI chat streaming failed after it started.");
                }

                return new AiChatStreamEventDto
                {
                    Type = AiChatStreamEventDto.Error,
                    Message = ex is InvalidOperationException ? ex.Message : "AI 問答發生錯誤，請稍後再試。",
                };
            },
            cancellationToken);
}
