using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// 1.1.69：轉錄完成後，自動替上傳者排入產生會議紀錄（上傳時選了提示詞、逐字稿沒有要等標示的講者）。
///
/// <para>
/// ⚠️ 不能直接呼叫 <c>MeetingService.RequestDraftAsync</c>：這裡跑在轉錄背景服務自建的 scope，
/// 沒有登入的使用者，資料範圍會判定「看不到這筆」。上傳者本來就看得到自己上傳的會議，
/// 所以這裡只做和「誰」無關的檢查（提示詞、轉錄狀態、是否產生中、專案唯讀、額度），
/// 欄位寫法與入列順序照 <c>RequestDraftAsync</c>。有講者要標示的走 <c>PUT …/speakers?generate=true</c>，那條有使用者，照常走 <c>RequestDraftAsync</c>。
/// </para>
/// </summary>
public sealed class AutoDraftStarter(
    BackendDBContext context,
    IAiQuotaService quotaService,
    IMeetingDraftProgressNotifier draftNotifier,
    IMeetingDraftQueue draftQueue,
    IJobCancellationRegistry cancellationRegistry,
    ILogger<AutoDraftStarter> logger)
{
    /// <summary>
    /// 排入產生。成功回 null；排不進去回原因（呼叫端寫進 <see cref="Meeting.DraftError"/>，不影響轉錄的「已完成」）。
    /// <paramref name="meeting"/> 是呼叫端追蹤中的實體，這裡會改它並存檔。
    /// </summary>
    public async Task<string?> StartAsync(Meeting meeting, int? requestedByUserId, string? requestedByUserName, CancellationToken cancellationToken)
    {
        if (meeting.AutoDraftPromptTemplateId is not { } templateId)
        {
            return null;
        }

        var failure = await CheckAsync(meeting, templateId, requestedByUserId, cancellationToken);

        // 不論成敗都不再自動產生，避免之後重新轉錄又自動產生一次。
        meeting.AutoDraftPromptTemplateId = null;
        meeting.AwaitingSpeakerNames = false;
        if (failure is not null)
        {
            logger.LogWarning("Auto draft was not queued. MeetingId={MeetingId}, Reason={Reason}", meeting.Id, failure);
            meeting.DraftError = $"沒有自動產生會議紀錄：{failure}";
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(CancellationToken.None);
            return failure;
        }

        var template = await context.PromptTemplate.AsNoTracking().FirstAsync(x => x.Id == templateId, cancellationToken);
        meeting.DraftPromptTemplateId = template.Id;
        meeting.DraftPromptTemplateName = template.Name;
        meeting.DraftAttendees = null;
        meeting.DraftStatus = DraftStatus.Pending;
        meeting.DraftError = null;
        meeting.DraftStartedAt = null;
        meeting.DraftCompletedAt = null;
        meeting.UpdatedAt = DateTime.Now;
        await context.SaveChangesAsync(cancellationToken);

        // 同 RequestDraftAsync：清殘留取消標記 → 登錄進度 → 入列。
        cancellationRegistry.ClearPendingCancel(BackgroundJobKind.MeetingDraft, meeting.Id);
        draftNotifier.Enqueued(meeting.Id, meeting.Title, meeting.ProjectId, meeting.CreatedByUserId);
        await draftQueue.EnqueueAsync(new MeetingJobRequest(meeting.Id, requestedByUserId, requestedByUserName), cancellationToken);
        logger.LogInformation("Auto draft queued after transcription. MeetingId={MeetingId}, Template={Template}", meeting.Id, template.Name);
        return null;
    }

    private async Task<string?> CheckAsync(Meeting meeting, int templateId, int? userId, CancellationToken cancellationToken)
    {
        if (!await context.PromptTemplate.AsNoTracking().AnyAsync(x => x.Id == templateId && x.IsEnabled, cancellationToken))
        {
            return "上傳時選的提示詞已不存在或已停用，請手動產生。";
        }

        if (meeting.TranscriptionStatus != TranscriptionStatus.Completed || string.IsNullOrWhiteSpace(meeting.TranscriptRelativePath))
        {
            return "逐字稿尚未轉錄完成。";
        }

        if (meeting.DraftStatus is DraftStatus.Pending or DraftStatus.Processing)
        {
            return "這份逐字稿已在產生會議紀錄。";
        }

        // 背景觸發一律當一般使用者：已完成（封存）的專案不自動產生。
        if (meeting.ProjectId is { } projectId
            && await context.Project.AsNoTracking().AnyAsync(x => x.Id == projectId && x.Status == ProjectAdapterModel.ArchivedStatus, cancellationToken))
        {
            return "專案已完成（封存）。";
        }

        return await quotaService.CheckAsync(userId, cancellationToken) is { } blocked
            ? AiQuotaExceededException.BuildMessage(blocked)
            : null;
    }
}
