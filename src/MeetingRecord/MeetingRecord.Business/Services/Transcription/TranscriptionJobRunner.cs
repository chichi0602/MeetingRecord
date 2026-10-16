using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 單一會議的轉錄工作流程：轉檔切段 → 逐段送 STT → 串接逐字稿 → 落檔 → 更新狀態。
///
/// 由背景服務在自己的 DI scope 內解析執行，不經過任何 Blazor circuit，
/// 所以這裡刻意不做團隊權限檢查（入列的動作已在服務層檢查過）。
/// </summary>
public class TranscriptionJobRunner
{
    /// <summary>分段逐字稿之間的接合字串。</summary>
    private const string SegmentSeparator = "\n\n";

    private readonly BackendDBContext context;
    private readonly IMediaConverter mediaConverter;
    private readonly IEnumerable<ITranscriptionProvider> transcriptionProviders;
    private readonly MeetingFileStore fileStore;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ITranscriptionProgressNotifier progressNotifier;
    private readonly AiCallGateway gateway;
    private readonly ILogger<TranscriptionJobRunner> logger;

    public TranscriptionJobRunner(
        BackendDBContext context,
        IMediaConverter mediaConverter,
        IEnumerable<ITranscriptionProvider> transcriptionProviders,
        MeetingFileStore fileStore,
        IOptions<LlmSettings> llmSettings,
        ITranscriptionProgressNotifier progressNotifier,
        AiCallGateway gateway,
        ILogger<TranscriptionJobRunner> logger)
    {
        this.context = context;
        this.mediaConverter = mediaConverter;
        this.transcriptionProviders = transcriptionProviders;
        this.fileStore = fileStore;
        this.llmSettings = llmSettings;
        this.progressNotifier = progressNotifier;
        this.gateway = gateway;
        this.logger = logger;
    }

    /// <param name="isCancelledByUser">
    /// 回報這次中斷是不是使用者主動要求的。應用程式關機同樣會拋 OperationCanceledException，
    /// 沒有這個判斷就會把關機時中斷的工作全部誤標成「已取消」。
    /// </param>
    /// <param name="requestedByUserId">
    /// 觸發這次轉錄的使用者，供用量帳本歸屬。⚠️ 必須由呼叫端傳進來——背景服務自建的 scope 裡
    /// CurrentUser 是空白物件（Id=0），自己去問只會把全部背景呼叫記到「空白使用者」而且不報錯。
    /// </param>
    public async Task RunAsync(
        int meetingId,
        CancellationToken cancellationToken,
        Func<bool>? isCancelledByUser = null,
        int? requestedByUserId = null,
        string? requestedByUserName = null)
    {
        var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
        if (meeting is null)
        {
            logger.LogWarning("Transcription skipped because meeting was not found. MeetingId={MeetingId}", meetingId);
            return;
        }

        if (string.IsNullOrWhiteSpace(meeting.MediaRelativePath))
        {
            logger.LogWarning("Transcription skipped because meeting has no media file. MeetingId={MeetingId}", meetingId);
            return;
        }

        // 1.0.29：一個音檔的所有段落共用同一個 ThreadKey，AI 對話紀錄才看得出是同一次轉錄。
        var callContext = new AiCallContext(
            AiUsageFeature.Transcription,
            requestedByUserId,
            requestedByUserName,
            meeting.Id,
            meeting.ProjectId,
            ThreadKey: $"transcription:{meeting.Id}:{Guid.NewGuid():N}");

        // 1.0.29：用量上限在工作開始時檢查一次（轉檔之前）；一旦開始就做完——
        // 轉到第 5 段才擋下的話，前 4 段的錢白花了又沒有逐字稿。
        var blocked = await gateway.AdmitAsync(callContext, cancellationToken);
        if (blocked is not null)
        {
            logger.LogWarning(
                "Transcription blocked by AI quota. MeetingId={MeetingId}, Scope={Scope}",
                meetingId,
                blocked.Scope);
            await MarkFailedAsync(meeting, AiQuotaExceededException.BuildMessage(blocked));
            return;
        }

        callContext = callContext with { QuotaAdmitted = true };

        meeting.TranscriptionStatus = TranscriptionStatus.Processing;
        meeting.TranscriptionStartedAt = DateTime.Now;
        meeting.TranscriptionCompletedAt = null;
        meeting.TranscriptionError = null;
        await context.SaveChangesAsync(cancellationToken);

        var previousTranscriptRelativePath = meeting.TranscriptRelativePath;

        try
        {
            var provider = ResolveProvider();
            var sourceFullPath = fileStore.GetMediaFullPath(meeting.MediaRelativePath);

            progressNotifier.ReportConverting(meetingId);

            using var segments = await mediaConverter.ConvertToMp3SegmentsAsync(sourceFullPath, cancellationToken);

            // 每一段各自的秒數：轉錄按時長計費，而最後一段通常不是整整 15 分鐘。
            // 全部算成整段會把費用高估。
            var segmentSeconds = MediaDurationParser.SplitSegmentSeconds(
                segments.TotalDuration,
                segments.SegmentFullPaths.Count,
                FfmpegMediaConverter.SegmentSeconds);

            var transcript = new StringBuilder();
            for (var index = 0; index < segments.SegmentFullPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var segmentPath = segments.SegmentFullPaths[index];
                logger.LogInformation(
                    "Transcribing segment. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}",
                    meetingId,
                    index + 1,
                    segments.SegmentFullPaths.Count);

                var thisSegmentSeconds = index < segmentSeconds.Count ? segmentSeconds[index] : (double?)null;

                // 記帳與 AI 對話紀錄由 gateway 處理（失敗時音檔多半已上傳、已計費，照樣記）。
                // ⚠️ gateway 在呼叫結束時就記帳，所以下面兩個 continue（指令洩漏被丟棄、整段無人說話）不會漏記——
                //    那一次轉錄照樣花了錢。轉錄每段一筆（每 15 分鐘一段），按時長計費。
                string segmentText;
                await using (var segmentStream = File.OpenRead(segmentPath))
                {
                    segmentText = await gateway.TranscribeAsync(
                        provider,
                        callContext with
                        {
                            RelatedInfo = $"會議 #{meeting.Id}〈{meeting.Title}〉語音轉錄 {index + 1}/{segments.SegmentFullPaths.Count}",
                        },
                        segmentStream,
                        Path.GetFileName(segmentPath),
                        thisSegmentSeconds,
                        segments.IsDurationEstimated,
                        index + 1,
                        segments.SegmentFullPaths.Count,
                        cancellationToken);
                }

                // 進度必須在 continue 之前回報，否則整段空白（無人說話）的段落會被跳過不計。
                progressNotifier.ReportSegment(meetingId, index + 1, segments.SegmentFullPaths.Count);

                // 沒有可辨識語音時，供應商可能不回空字串，而是把自己的系統指令當成結果吐回來。
                // 這種段落等同無語音，直接丟棄；記 Warning 是為了日後看得出模型在漏指令，而不是默默吃掉。
                if (TranscriptionNoiseFilter.IsProviderInstructionLeak(segmentText))
                {
                    logger.LogWarning(
                        "Discarded transcription segment because the provider returned its own instructions. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}",
                        meetingId,
                        index + 1,
                        segments.SegmentFullPaths.Count);

                    continue;
                }

                if (string.IsNullOrWhiteSpace(segmentText))
                {
                    continue;
                }

                if (transcript.Length > 0)
                {
                    transcript.Append(SegmentSeparator);
                }

                transcript.Append(segmentText.Trim());
            }

            // 轉錄服務輸出的是簡體字（提示詞管不到），存檔前轉成台灣字形（1.0.41，使用者要求不要有簡體字）。
            var transcriptRelativePath = await fileStore.SaveTranscriptAsync(
                meeting.CreatedAt,
                ChineseTextConverter.ToTraditional(transcript.ToString()) ?? string.Empty,
                cancellationToken);

            meeting.TranscriptRelativePath = transcriptRelativePath;

            // 1.0.29：會議自己記時長（儀表板「會議時數」讀這一欄）；以前是加總帳本的轉錄秒數，但帳本可以被清除了。
            meeting.MediaDurationSeconds = segmentSeconds.Count > 0 ? segmentSeconds.Sum() : null;
            meeting.TranscriptionStatus = TranscriptionStatus.Completed;
            meeting.TranscriptionError = null;
            meeting.TranscriptionCompletedAt = DateTime.Now;
            meeting.UpdatedAt = DateTime.Now;
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // 存不進去（例如會議剛被刪掉）就沒有任何資料列指向這份逐字稿，
                // 不刪的話一份機密逐字稿會永遠留在磁碟上（0.4.115）。
                fileStore.TryDeleteTranscript(transcriptRelativePath);
                throw;
            }

            progressNotifier.ReportCompleted(meetingId);

            // 重跑轉錄時，舊逐字稿在新檔寫入成功後才刪除，避免中途失敗兩份都沒有。
            if (!string.IsNullOrWhiteSpace(previousTranscriptRelativePath)
                && previousTranscriptRelativePath != transcriptRelativePath)
            {
                fileStore.TryDeleteTranscript(previousTranscriptRelativePath);
            }

            logger.LogInformation(
                "Transcription completed. MeetingId={MeetingId}, SegmentCount={SegmentCount}, Length={Length}",
                meetingId,
                segments.SegmentFullPaths.Count,
                transcript.Length);
        }
        catch (OperationCanceledException) when (isCancelledByUser?.Invoke() == true)
        {
            // 只有「使用者按了取消」才標成已取消。應用程式關機時同樣會拋這個例外，
            // 但那些工作是被中斷、不是被放棄，得留給啟動時的復原邏輯標成失敗。
            logger.LogInformation("Transcription cancelled by user. MeetingId={MeetingId}", meetingId);
            await MarkCancelledAsync(meeting);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription failed. MeetingId={MeetingId}", meetingId);
            await MarkFailedAsync(meeting, ex.Message);
        }
    }

    /// <summary>
    /// 依 <c>LlmSettings.TranscriptionProvider</c>（或 DefaultProvider）挑出對應的供應商實作。
    /// </summary>
    private ITranscriptionProvider ResolveProvider()
    {
        var settings = llmSettings.Value;
        if (!settings.IsTranscriptionConfigured)
        {
            throw new InvalidOperationException(
                $"尚未設定語音轉錄供應商，請在 {LlmSettings.SectionName}:TranscriptionProvider 或 {LlmSettings.SectionName}:DefaultProvider 指定。");
        }

        var name = settings.EffectiveTranscriptionProviderName;
        var provider = transcriptionProviders
            .FirstOrDefault(x => string.Equals(x.ProviderName, name, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            var supported = string.Join("、", transcriptionProviders.Select(x => x.ProviderName));
            throw new InvalidOperationException(
                $"找不到名為「{name}」的語音轉錄供應商實作。目前支援：{supported}。");
        }

        return provider;
    }

    /// <summary>
    /// 標記為使用者取消。沿用失敗的通知管道（畫面上都是「這筆結束了」），
    /// 但狀態與錯誤訊息分開——取消不是失敗，不該顯示成紅色的錯誤。
    /// </summary>
    private async Task MarkCancelledAsync(AccessDatas.Models.Meeting meeting)
    {
        progressNotifier.ReportFailed(meeting.Id, "已由使用者取消，可從清單重新轉錄。");

        try
        {
            meeting.TranscriptionStatus = TranscriptionStatus.Cancelled;
            meeting.TranscriptionError = null;
            meeting.TranscriptionCompletedAt = DateTime.Now;
            meeting.UpdatedAt = DateTime.Now;

            // 權杖已經被取消了，這裡必須用 None，否則狀態寫不進去。
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist transcription cancelled state. MeetingId={MeetingId}", meeting.Id);
        }
    }

    private async Task MarkFailedAsync(AccessDatas.Models.Meeting meeting, string message)
    {
        progressNotifier.ReportFailed(meeting.Id, message);

        try
        {
            meeting.TranscriptionStatus = TranscriptionStatus.Failed;
            meeting.TranscriptionError = message;
            meeting.TranscriptionCompletedAt = DateTime.Now;
            meeting.UpdatedAt = DateTime.Now;

            // 失敗狀態必須寫得進去，因此這裡不帶入已取消的 CancellationToken。
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist transcription failure state. MeetingId={MeetingId}", meeting.Id);
        }
    }
}
