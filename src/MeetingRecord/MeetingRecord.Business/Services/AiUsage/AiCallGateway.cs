using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.AiUsage;

/// <summary>
/// 一次 AI 呼叫的歸屬與脈絡（1.0.29）。
/// </summary>
/// <param name="Feature">功能別（帳本與對話紀錄共用）。</param>
/// <param name="UserId">觸發者；背景工作由佇列帶進來，⚠️ 不可在背景 scope 裡問 CurrentUserService。</param>
/// <param name="RelatedInfo">給人看的關聯說明，例如「會議 #12〈週會〉分段摘要 2/5」。</param>
/// <param name="ThreadKey">把同一趟工作的多次呼叫串起來，例如 <c>draft:12:{runId}</c>。</param>
/// <param name="QuotaAdmitted">
/// 背景工作已在開始時通過上限檢查（<see cref="AiCallGateway.AdmitAsync"/>），工作內的呼叫不再逐次檢查——
/// 否則轉錄到第 5 段才被擋，前 4 段的錢白花又沒有逐字稿。
/// </param>
public sealed record AiCallContext(
    AiUsageFeature Feature,
    int? UserId,
    string? UserName,
    int? MeetingId = null,
    int? ProjectId = null,
    string? RelatedInfo = null,
    string? ThreadKey = null,
    bool QuotaAdmitted = false);

/// <summary>
/// 所有付費 AI 呼叫的<b>唯一出入口</b>（1.0.29）。一次呼叫在這裡完成四件事：
/// 用量上限檢查 → 產生 CallId → 呼叫供應商（同時收下逐段回應）→ 寫用量帳本與 AI 對話紀錄（同一個 CallId）。
///
/// <para>
/// ⚠️ <b>不可以繞過它直接呼叫 <see cref="ITextGenerationProvider.GenerateAsync"/>／
/// <see cref="ITranscriptionProvider.TranscribeAsync"/></b>：繞過的呼叫不會被上限擋、不會進帳本、也不會有對話紀錄。
/// <c>AiCallGatewayUsageTests</c> 掃描原始碼守住這條規則。
/// </para>
///
/// <para>
/// 刻意做在呼叫端與供應商之間、而不是供應商內部：供應商是「換廠商」的擴充點，做在裡面每家都要重寫一次；
/// 而且供應商送出的 body 含圖片的 base64，那是絕不能存的東西。這裡看到的正好是「送了什麼文字、收到什麼文字」。
/// </para>
///
/// <para>
/// 失敗與取消照樣記錄（帳本與對話紀錄都記，一對一）：取消的呼叫照樣花了錢，串流到一半的回應也留下來。
/// 兩個記錄器都不拋例外，而且必須在呼叫端修改自己的實體<b>之前</b>寫入（共用 scoped DbContext）。
/// </para>
/// </summary>
public class AiCallGateway
{
    private readonly AiUsageRecorder usageRecorder;
    private readonly IAiCallLogRecorder callLogRecorder;
    private readonly IAiQuotaService quotaService;
    private readonly IAuditLogService auditLogService;
    private readonly ILogger<AiCallGateway> logger;

    public AiCallGateway(
        AiUsageRecorder usageRecorder,
        IAiCallLogRecorder callLogRecorder,
        IAiQuotaService quotaService,
        IAuditLogService auditLogService,
        ILogger<AiCallGateway> logger)
    {
        this.usageRecorder = usageRecorder;
        this.callLogRecorder = callLogRecorder;
        this.quotaService = quotaService;
        this.auditLogService = auditLogService;
        this.logger = logger;
    }

    /// <summary>
    /// 背景工作開始時的上限檢查（一次）。已達上限時寫稽核並回傳狀態，<b>不拋例外</b>——由工作自己標成失敗。
    /// </summary>
    public async Task<AiQuotaStatus?> AdmitAsync(AiCallContext context, CancellationToken cancellationToken)
    {
        var blocked = await quotaService.CheckAsync(context.UserId, cancellationToken);
        if (blocked is not null)
        {
            await AuditBlockedAsync(context, blocked);
        }

        return blocked;
    }

    /// <summary>
    /// 文字生成。參數與 <see cref="ITextGenerationProvider.GenerateAsync"/> 相同，多一個 <paramref name="context"/>。
    /// </summary>
    /// <exception cref="AiQuotaExceededException">已達用量上限（沒有送出、不記帳）。</exception>
    public async Task<TextGenerationResult> GenerateAsync(
        ITextGenerationProvider provider,
        AiCallContext context,
        string systemPrompt,
        string userPrompt,
        Action<string>? onDelta,
        CancellationToken cancellationToken,
        IReadOnlyList<PromptImage>? images = null)
    {
        await EnsureQuotaAsync(context, cancellationToken);

        var callId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var received = new StringBuilder();

        void Capture(string delta)
        {
            received.Append(delta);
            onDelta?.Invoke(delta);
        }

        var request = new AiCallLogRequest
        {
            Kind = AiCallLogRequest.ChatKind,
            Messages =
            [
                new AiCallLogMessage { Role = "system", Content = systemPrompt },
                new AiCallLogMessage { Role = "user", Content = userPrompt },
            ],
            // 只記類型與大小：圖片本身可能是任何東西，存下來就是一份不受控的副本。
            Images = images?.Select(x => new AiCallLogImage { MediaType = x.MediaType, Bytes = x.Content.LongLength }).ToList() ?? [],
        };

        TextGenerationResult result;
        try
        {
            result = await provider.GenerateAsync(systemPrompt, userPrompt, Capture, cancellationToken, images);
        }
        catch (Exception ex)
        {
            await RecordAsync(
                provider.ProviderName, provider.ModelName, context, callId, stopwatch,
                OutcomeOf(ex), tokens: null, audioSeconds: null, isDurationEstimated: false,
                request, received.ToString(), isPartial: true, ex);
            throw;
        }

        await RecordAsync(
            provider.ProviderName, provider.ModelName, context, callId, stopwatch,
            AiUsageOutcome.Succeeded, result.Usage, audioSeconds: null, isDurationEstimated: false,
            request, result.Content, isPartial: false, exception: null);

        return result;
    }

    /// <summary>
    /// 語音轉錄一段音訊。對話紀錄只記檔名、時長與段落，<b>不存音訊</b>。
    /// </summary>
    /// <param name="audioSeconds">這一段的秒數（帳本按時長計費）。</param>
    /// <param name="segmentIndex">第幾段（1 起算）。</param>
    /// <exception cref="AiQuotaExceededException">已達用量上限（沒有送出、不記帳）。</exception>
    public async Task<string> TranscribeAsync(
        ITranscriptionProvider provider,
        AiCallContext context,
        Stream audio,
        string fileName,
        double? audioSeconds,
        bool isDurationEstimated,
        int segmentIndex,
        int segmentCount,
        CancellationToken cancellationToken)
    {
        await EnsureQuotaAsync(context, cancellationToken);

        var callId = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var request = new AiCallLogRequest
        {
            Kind = AiCallLogRequest.TranscriptionKind,
            FileName = fileName,
            AudioSeconds = audioSeconds,
            IsAudioDurationEstimated = isDurationEstimated,
            SegmentIndex = segmentIndex,
            SegmentCount = segmentCount,
        };

        string text;
        try
        {
            text = await provider.TranscribeAsync(audio, fileName, cancellationToken);
        }
        catch (Exception ex)
        {
            // 失敗時帳本不填秒數（與 1.0.29 之前相同：沒有轉出結果，不確定供應商算了多少）。
            await RecordAsync(
                provider.ProviderName, provider.ModelName, context, callId, stopwatch,
                OutcomeOf(ex), tokens: null, audioSeconds: null, isDurationEstimated,
                request, string.Empty, isPartial: true, ex);
            throw;
        }

        await RecordAsync(
            provider.ProviderName, provider.ModelName, context, callId, stopwatch,
            AiUsageOutcome.Succeeded, tokens: null, audioSeconds, isDurationEstimated,
            request, text, isPartial: false, exception: null);

        return text;
    }

    private async Task EnsureQuotaAsync(AiCallContext context, CancellationToken cancellationToken)
    {
        if (context.QuotaAdmitted)
        {
            return;
        }

        var blocked = await quotaService.CheckAsync(context.UserId, cancellationToken);
        if (blocked is null)
        {
            return;
        }

        await AuditBlockedAsync(context, blocked);
        throw new AiQuotaExceededException(blocked);
    }

    private async Task AuditBlockedAsync(AiCallContext context, AiQuotaStatus status)
    {
        logger.LogWarning(
            "AI call blocked by quota. Feature={Feature}, Scope={Scope}, Limit={Limit}, Used={Used}, UserId={UserId}, MeetingId={MeetingId}",
            context.Feature,
            status.Scope,
            status.Limit,
            status.Used,
            context.UserId,
            context.MeetingId);

        await auditLogService.WriteAsync(
            "Ai.QuotaBlocked",
            success: false,
            actorUserId: context.UserId,
            actorAccount: context.UserName,
            targetType: "AiUsageFeature",
            targetId: context.Feature.ToString(),
            detail: $"scope={status.Scope}, limit={status.Limit}, used={status.Used:0.##}, meetingId={context.MeetingId}, projectId={context.ProjectId}");
    }

    private async Task RecordAsync(
        string providerName,
        string modelName,
        AiCallContext context,
        Guid callId,
        Stopwatch stopwatch,
        AiUsageOutcome outcome,
        AiTokenUsage? tokens,
        double? audioSeconds,
        bool isDurationEstimated,
        AiCallLogRequest request,
        string responseText,
        bool isPartial,
        Exception? exception)
    {
        stopwatch.Stop();

        // 帳本先：它是計費與上限的依據，比對話紀錄重要。
        await usageRecorder.RecordAsync(new AiUsageEntry(
            context.Feature,
            outcome,
            providerName,
            modelName,
            Tokens: tokens,
            AudioSeconds: audioSeconds,
            IsAudioDurationEstimated: isDurationEstimated,
            UserId: context.UserId,
            UserName: context.UserName,
            MeetingId: context.MeetingId,
            ProjectId: context.ProjectId,
            ErrorMessage: exception?.Message,
            CallId: callId));

        if (!callLogRecorder.IsEnabled)
        {
            return;
        }

        await callLogRecorder.RecordAsync(new AiCallLogEntry
        {
            CallId = callId,
            OccurredAt = DateTime.Now,
            Feature = context.Feature,
            FeatureText = AiUsageFeatureText.Describe(context.Feature),
            Outcome = outcome,
            Provider = providerName,
            Model = modelName,
            UserId = context.UserId,
            UserName = context.UserName,
            MeetingId = context.MeetingId,
            ProjectId = context.ProjectId,
            RelatedInfo = context.RelatedInfo,
            ThreadKey = context.ThreadKey,
            Request = request,
            Response = new AiCallLogResponse
            {
                Text = responseText,
                IsPartial = isPartial && !string.IsNullOrEmpty(responseText),
                InputTokens = tokens?.InputTokens,
                OutputTokens = tokens?.OutputTokens,
                CachedInputTokens = tokens?.CachedInputTokens,
            },
            ErrorMessage = exception?.Message,
            ExceptionType = exception?.GetType().Name,
            HttpStatus = (exception as AiProviderHttpException)?.StatusCode,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
        });
    }

    private static AiUsageOutcome OutcomeOf(Exception ex)
        => ex is OperationCanceledException ? AiUsageOutcome.Cancelled : AiUsageOutcome.Failed;
}
