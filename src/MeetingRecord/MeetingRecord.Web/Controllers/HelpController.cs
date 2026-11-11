using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.HelpAssistant;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Helps;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Filters;
using MeetingRecord.Web.Helps;
using MeetingRecord.Web.Streaming;

namespace MeetingRecord.Web.Controllers;

/// <summary>
/// 使用說明（1.1.29，WASM 遷移 M4）：說明內容與「問 AI 助理」。都要使用說明頁的檢視權限（同畫面）。
///
/// <para>
/// 提問會花 AI 費用、要邊產生邊顯示：回應是串流（一行一筆 JSON，見 <see cref="HelpAssistantStreamEventDto"/>）。
/// 要不要把「管理員」章節送給 AI 由伺服器依呼叫者判斷（一般使用者不送，1.0.43 使用者決定），不收前端的值。
/// 前端按「停止」＝中斷請求：請求的取消權杖傳進服務層，AI 呼叫跟著停（帳本記成 Cancelled）。
/// 稽核由伺服器寫，內容同 Server 頁，另加 <c>via=Api</c>。
/// </para>
/// </summary>
[Route("api/v1/help")]
[ApiController]
[Authorize(AuthenticationSchemes = MagicObjectHelper.ApiScheme)]
public sealed partial class HelpController(
    HelpManualProvider manualProvider,
    HelpAssistantService assistantService,
    IRecordAccessScopeProvider accessScopeProvider,
    IAuditLogService auditLogService,
    ILogger<HelpController> logger) : ControllerBase
{
    /// <summary>說明內容（去掉檔頭、保留分類標記）與 AI 助理能不能用。</summary>
    [HttpGet("manual")]
    [HasPermission(MagicObjectHelper.角色_使用說明, PermissionActions.View)]
    public ActionResult<ApiResult<HelpManualDto>> Manual()
    {
        if (manualProvider.Markdown is not { } markdown)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                ApiResult<HelpManualDto>.FailureResult(HelpManualProvider.LoadFailedMessage, StatusCodes.Status500InternalServerError));
        }

        var available = assistantService.IsAvailable;
        return Ok(ApiResult<HelpManualDto>.SuccessResult(new HelpManualDto
        {
            Markdown = markdown,
            AssistantAvailable = available,
            AssistantUnavailableReason = available ? string.Empty : UnavailableMessage(),
        }));
    }

    /// <summary>問 AI 助理。回應是串流；問題空白、對話串編號格式不對、助理不能用、第一段文字前就失敗，回一般的 400。</summary>
    [HttpPost("assistant/ask")]
    [HasPermission(MagicObjectHelper.角色_使用說明, PermissionActions.View)]
    public async Task Ask([FromBody] HelpAssistantAskRequestDto request, CancellationToken cancellationToken)
    {
        var question = (request.Question ?? string.Empty).Trim();
        if (question.Length == 0)
        {
            await BadRequestAsync("請輸入問題。");
            return;
        }

        if (request.ThreadKey is { } key && !ThreadKeyPattern().IsMatch(key))
        {
            await BadRequestAsync("對話串編號格式不正確。");
            return;
        }

        if (manualProvider.Manual is not { } manual)
        {
            await new ObjectResult(ApiResult.FailureResult(HelpManualProvider.LoadFailedMessage, StatusCodes.Status500InternalServerError))
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            }.ExecuteResultAsync(ControllerContext);
            return;
        }

        if (!assistantService.IsAvailable)
        {
            await BadRequestAsync(UnavailableMessage());
            return;
        }

        var includeAdmin = (await accessScopeProvider.GetAsync()).IsAdmin;
        var history = (request.History ?? [])
            .Select(x => new HelpAssistantTurn(x.Question ?? string.Empty, x.Answer ?? string.Empty))
            .ToList();
        var threadKey = request.ThreadKey ?? $"help:{Guid.NewGuid():N}";

        var outcome = await NdjsonStreamer.RunAsync(
            this,
            onDelta => assistantService.AskAsync(manual.BuildAssistantContext(includeAdmin), history, question, onDelta, cancellationToken, threadKey),
            delta => new HelpAssistantStreamEventDto { Type = HelpAssistantStreamEventDto.Delta, Text = delta },
            answer => new HelpAssistantStreamEventDto { Type = HelpAssistantStreamEventDto.Done, Answer = answer.Content, Model = answer.ModelName },
            error => new BadRequestObjectResult(ApiResult.FailureResult(HelpAssistantFailure.Describe(LogFailure(error, cancellationToken)))),
            error => new HelpAssistantStreamEventDto { Type = HelpAssistantStreamEventDto.Error, Message = HelpAssistantFailure.Describe(LogFailure(error, cancellationToken)) },
            cancellationToken);

        var result = outcome.Error switch
        {
            null => "成功",
            OperationCanceledException when cancellationToken.IsCancellationRequested => "停止",
            var error => $"失敗：{error.GetType().Name}",
        };

        // 問題全文已經在 AI 對話紀錄裡，稽核只記長度與結果（同 Server 頁）。斷線也要記得到，所以不傳請求的取消權杖。
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.HelpAiAsk,
            success: outcome.Error is null,
            targetType: "Help",
            detail: $"questionChars={question.Length}; includeAdmin={includeAdmin}; model={outcome.Result?.ModelName ?? "-"}; outcome={result}; via={AuditVia.Api}");
    }

    private string UnavailableMessage() => $"AI 助理目前無法使用：{assistantService.UnavailableReason}";

    private Task BadRequestAsync(string message)
        => new BadRequestObjectResult(ApiResult.FailureResult(message)).ExecuteResultAsync(ControllerContext);

    /// <summary>同 Server 頁：額度用完記 warning、使用者停止不記、其他記 error。</summary>
    private Exception LogFailure(Exception error, CancellationToken cancellationToken)
    {
        if (error is AiQuotaExceededException)
        {
            logger.LogWarning("Help assistant blocked by AI quota.");
        }
        else if (!(error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger.LogError(error, "Help assistant failed.");
        }

        return error;
    }

    [GeneratedRegex("^help:[0-9a-f]{32}$")]
    private static partial Regex ThreadKeyPattern();
}
