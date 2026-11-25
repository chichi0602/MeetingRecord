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
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.Transcription;

/// <summary>
/// 單一會議的轉錄工作流程：轉檔切段 → 逐段送 STT → 串接逐字稿 → 落檔 → 更新狀態。
///
/// 由背景服務在自己的 DI scope 內解析執行，不在任何使用者的請求裡，
/// 所以這裡刻意不做團隊權限檢查（入列的動作已在服務層檢查過）。
/// </summary>
public class TranscriptionJobRunner
{
    /// <summary>分段逐字稿之間的接合字串。</summary>
    private const string SegmentSeparator = "\n\n";

    /// <summary>
    /// 疑似被截斷時最多再對半切幾層（1.1.61）。兩層＝最多拆成四份，5 分鐘一段時每份約 75 秒。
    /// </summary>
    private const int MaxSplitDepth = 2;

    /// <summary>短於這個秒數的段落不再對半切：不可能講出接近輸出上限的字數，切了只是白花錢。</summary>
    private const double MinSplitSeconds = 60;

    private readonly BackendDBContext context;
    private readonly IMediaConverter mediaConverter;
    private readonly IEnumerable<ITranscriptionProvider> transcriptionProviders;
    private readonly MeetingFileStore fileStore;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ITranscriptionProgressNotifier progressNotifier;
    private readonly AiCallGateway gateway;
    private readonly ILogger<TranscriptionJobRunner> logger;
    private readonly IMediaSegmentSplitter? segmentSplitter;
    private readonly IAudioClipCutter? clipCutter;

    /// <summary>1.1.68 成本閘門；選填，沿用 segmentSplitter 的做法，既有測試不用改。</summary>
    private readonly TranscriptionCostEstimator? costEstimator;

    /// <summary>1.1.69 轉錄完自動產生會議紀錄；選填，理由同上。</summary>
    private readonly AutoDraftStarter? autoDraftStarter;

    /// <summary>1.1.73 使用者選的引擎與分講者；選填，理由同上（沒註冊時一律照設定的部署）。</summary>
    private readonly TranscriptionOptionsResolver? optionsResolver;

    private readonly AzureLlmSpeechTranscriptionProvider? llmSpeechProvider;

    private readonly IOptions<TranscriptionSettings>? transcriptionSettings;

    /// <summary>1.1.69：轉完要先標示講者時，進度面板「已完成」後面的附註。</summary>
    public const string AwaitingSpeakerNamesNote = "請標示講者";

    /// <param name="segmentSplitter">
    /// 1.1.61：疑似被截斷的段落用它對半切再重轉。選擇性參數——沒有註冊時（例如端對端測試的假轉檔）照常轉錄、只是少這道保險。
    /// </param>
    /// <param name="clipCutter">
    /// 1.1.64：剪講者的聲音參考，讓整場的講者代號一致。選擇性參數——沒有註冊時照舊每段各自標字母。
    /// </param>
    public TranscriptionJobRunner(
        BackendDBContext context,
        IMediaConverter mediaConverter,
        IEnumerable<ITranscriptionProvider> transcriptionProviders,
        MeetingFileStore fileStore,
        IOptions<LlmSettings> llmSettings,
        ITranscriptionProgressNotifier progressNotifier,
        AiCallGateway gateway,
        ILogger<TranscriptionJobRunner> logger,
        IMediaSegmentSplitter? segmentSplitter = null,
        IAudioClipCutter? clipCutter = null,
        TranscriptionCostEstimator? costEstimator = null,
        AutoDraftStarter? autoDraftStarter = null,
        TranscriptionOptionsResolver? optionsResolver = null,
        AzureLlmSpeechTranscriptionProvider? llmSpeechProvider = null,
        IOptions<TranscriptionSettings>? transcriptionSettings = null)
    {
        this.optionsResolver = optionsResolver;
        this.llmSpeechProvider = llmSpeechProvider;
        this.transcriptionSettings = transcriptionSettings;
        this.segmentSplitter = segmentSplitter;
        this.clipCutter = clipCutter;
        this.costEstimator = costEstimator;
        this.autoDraftStarter = autoDraftStarter;
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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 1.1.73：引擎每場自己選。LLM Speech 一次送整個檔案（超過上限才切段），Azure OpenAI 照舊 5 分鐘一段。
            var isLlmSpeech = meeting.TranscriptionEngine == TranscriptionEngine.AzureLlmSpeech;
            var provider = isLlmSpeech ? await ResolveLlmSpeechProviderAsync(meeting, cancellationToken) : ResolveProvider(meeting.TranscriptionDiarize);
            var maxSegmentSeconds = isLlmSpeech
                ? transcriptionSettings?.Value.AzureSpeech.MaxFileSeconds ?? FfmpegMediaConverter.SegmentSeconds
                : FfmpegMediaConverter.SegmentSeconds;
            var sourceFullPath = fileStore.GetMediaFullPath(meeting.MediaRelativePath);

            progressNotifier.ReportConverting(meetingId);

            using var segments = isLlmSpeech
                ? await mediaConverter.ConvertToMp3SegmentsAsync(sourceFullPath, maxSegmentSeconds, cancellationToken)
                : await mediaConverter.ConvertToMp3SegmentsAsync(sourceFullPath, cancellationToken);

            // 每一段各自的秒數：轉錄按時長計費，而最後一段通常不是整整 5 分鐘。
            // 全部算成整段會把費用高估。1.1.61 起切在停頓處，各段不一樣長，以轉檔器算好的為準。
            var segmentSeconds = segments.SegmentSeconds ?? MediaDurationParser.SplitSegmentSeconds(
                segments.TotalDuration,
                segments.SegmentFullPaths.Count,
                maxSegmentSeconds);

            // 1.1.68 成本閘門：長度要到這裡（本機轉檔量完）才知道，而第一段還沒送出、一毛錢都還沒花。
            // 開工時的 AdmitAsync 只看「是否已達上限」，看不出「這一趟會不會超過」；這裡補上。
            var totalSeconds = segments.TotalDuration?.TotalSeconds
                ?? (segmentSeconds.Count > 0 ? segmentSeconds.Sum() : (double?)null);
            if (costEstimator is not null && totalSeconds is { } seconds
                && await costEstimator.CheckAffordableAsync(seconds, requestedByUserId, provider.ModelName, cancellationToken) is { } costMessage)
            {
                logger.LogWarning(
                    "Transcription blocked because the estimated cost exceeds the remaining AI quota. MeetingId={MeetingId}, Seconds={Seconds}",
                    meetingId,
                    seconds);
                await MarkFailedAsync(meeting, costMessage);
                return;
            }

            // 1.1.64：會分講者的供應商帶聲音參考，讓整場的講者代號一致（見 SpeakerAlignmentSession）。
            // LLM Speech 一次呼叫內的編號本來就一致，不需要（也不接受）聲音參考。
            var alignment = !isLlmSpeech && provider is ISpeakerAwareTranscriptionProvider { SupportsSpeakers: true } && clipCutter is not null
                ? new SpeakerAlignmentSession()
                : null;

            // 1.1.73：LLM Speech 的試聽片段（講者代號 → wav），轉錄時順手剪好。
            var llmSpeechClips = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            var transcript = new StringBuilder();
            for (var index = 0; index < segments.SegmentFullPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var segmentPath = segments.SegmentFullPaths[index];
                logger.LogInformation(
                    "Transcribing segment. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}, Engine={Engine}",
                    meetingId,
                    index + 1,
                    segments.SegmentFullPaths.Count,
                    meeting.TranscriptionEngine);

                var thisSegmentSeconds = index < segmentSeconds.Count ? segmentSeconds[index] : (double?)null;

                var segmentText = isLlmSpeech
                    ? await TranscribeLlmSpeechSegmentAsync(
                        (AzureLlmSpeechTranscriptionProvider)provider,
                        callContext,
                        meeting,
                        segmentPath,
                        thisSegmentSeconds,
                        segments.IsDurationEstimated,
                        index + 1,
                        segments.SegmentFullPaths.Count,
                        llmSpeechClips,
                        cancellationToken)
                    : await TranscribeSegmentAsync(
                        provider,
                        callContext,
                        meeting,
                        segmentPath,
                        thisSegmentSeconds,
                        segments.IsDurationEstimated,
                        index + 1,
                        segments.SegmentFullPaths.Count,
                        label: string.Empty,
                        depth: 0,
                        alignment,
                        cancellationToken);

                // 進度必須在 continue 之前回報，否則整段空白（無人說話）的段落會被跳過不計。
                progressNotifier.ReportSegment(meetingId, index + 1, segments.SegmentFullPaths.Count);

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

            // 1.1.64：新逐字稿的講者編號是重新配發的，舊的真名對照不能沿用。
            meeting.SpeakerNames = null;

            // 1.1.71：把聲音參考存下來給「標示講者」試聽；舊片段等新的存好才刪。
            var previousSpeakerClips = TranscriptSpeakerLabels.ParseNames(meeting.SpeakerClips).Values.ToList();
            var speakerClips = await SaveSpeakerClipsAsync(meeting, CollectClips(alignment, llmSpeechClips), transcript.ToString(), cancellationToken);
            meeting.SpeakerClips = TranscriptSpeakerLabels.SerializeNames(speakerClips);

            // 1.1.69：上傳時選了自動產生、而逐字稿有講者 1～4 → 先等使用者標示講者，存檔時才產生（只付一次費用）。
            var awaitingSpeakerNames = meeting.AutoDraftPromptTemplateId is not null
                && TranscriptSpeakerLabels.ListAlignedSpeakers(transcript.ToString()).Count > 0;
            meeting.AwaitingSpeakerNames = awaitingSpeakerNames;

            // 1.0.29：會議自己記時長（儀表板「會議時數」讀這一欄）；以前是加總帳本的轉錄秒數，但帳本可以被清除了。
            meeting.MediaDurationSeconds = segmentSeconds.Count > 0 ? segmentSeconds.Sum() : null;
            meeting.TranscriptionStatus = TranscriptionStatus.Completed;
            meeting.TranscriptionError = null;
            meeting.TranscriptionCompletedAt = DateTime.Now;
            meeting.TranscriptionElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
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
                fileStore.TryDeleteSpeakerClips(speakerClips.Values);
                throw;
            }

            fileStore.TryDeleteSpeakerClips(previousSpeakerClips);

            progressNotifier.ReportCompleted(meetingId, awaitingSpeakerNames ? AwaitingSpeakerNamesNote : null);

            // 重跑轉錄時，舊逐字稿在新檔寫入成功後才刪除，避免中途失敗兩份都沒有。
            if (!string.IsNullOrWhiteSpace(previousTranscriptRelativePath)
                && previousTranscriptRelativePath != transcriptRelativePath)
            {
                fileStore.TryDeleteTranscript(previousTranscriptRelativePath);
            }

            // 1.1.69：沒有要等標示的講者 → 直接自動產生會議紀錄。
            // 排不進去（提示詞停用、額度不足…）只寫 DraftError，轉錄本身已經完成，不能因此變成失敗。
            if (!awaitingSpeakerNames && meeting.AutoDraftPromptTemplateId is not null && autoDraftStarter is not null)
            {
                try
                {
                    await autoDraftStarter.StartAsync(meeting, requestedByUserId, requestedByUserName, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Auto draft after transcription failed to start. MeetingId={MeetingId}", meetingId);
                }
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
    /// 轉錄一個分段，回傳可以接進逐字稿的文字（無語音、指令洩漏時回空字串）。
    ///
    /// <para>
    /// 1.1.61 截斷保險：結果長到接近轉錄模型的輸出上限（<see cref="TranscriptionSegmentPlanner.LooksTruncated"/>）時，
    /// 該段後半很可能已被靜默截掉——把音檔在中間的停頓對半切、兩半各自重轉，取代原本的結果。
    /// 最多 <see cref="MaxSplitDepth"/> 層。⚠️ 原本那次與重轉的兩半都會計費（原本那次音檔已上傳、照樣收錢），
    /// 所以只在真的頂到上限時才做；5 分鐘一段的正常會議不會觸發。
    /// </para>
    /// </summary>
    private async Task<string> TranscribeSegmentAsync(
        ITranscriptionProvider provider,
        AiCallContext callContext,
        AccessDatas.Models.Meeting meeting,
        string segmentPath,
        double? seconds,
        bool isDurationEstimated,
        int segmentNumber,
        int segmentCount,
        string label,
        int depth,
        SpeakerAlignmentSession? alignment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 記帳與 AI 對話紀錄由 gateway 處理（失敗時音檔多半已上傳、已計費，照樣記）。
        // ⚠️ gateway 在呼叫結束時就記帳，所以下面回空字串的分支（指令洩漏被丟棄、整段無人說話）不會漏記——
        //    那一次轉錄照樣花了錢。轉錄每段一筆（約每 5 分鐘一段），按時長計費。
        var segmentContext = callContext with
        {
            RelatedInfo = $"會議 #{meeting.Id}〈{meeting.Title}〉語音轉錄 {segmentNumber}/{segmentCount}{label}",
        };

        string segmentText;
        if (alignment is { IsDisabled: false } && provider is ISpeakerAwareTranscriptionProvider speakerAware)
        {
            segmentText = await TranscribeAlignedAsync(
                speakerAware, segmentContext, meeting.Id, segmentPath, seconds, isDurationEstimated,
                segmentNumber, segmentCount, alignment, cancellationToken);
        }
        else
        {
            segmentText = await TranscribePlainAsync(
                provider, segmentContext, segmentPath, seconds, isDurationEstimated,
                segmentNumber, segmentCount, cancellationToken);
        }

        // 沒有可辨識語音時，供應商可能不回空字串，而是把自己的系統指令當成結果吐回來。
        // 這種段落等同無語音，直接丟棄；記 Warning 是為了日後看得出模型在漏指令，而不是默默吃掉。
        if (TranscriptionNoiseFilter.IsProviderInstructionLeak(segmentText))
        {
            logger.LogWarning(
                "Discarded transcription segment because the provider returned its own instructions. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}{Label}",
                meeting.Id,
                segmentNumber,
                segmentCount,
                label);

            return string.Empty;
        }

        if (segmentSplitter is null
            || depth >= MaxSplitDepth
            || seconds is < MinSplitSeconds
            || !TranscriptionSegmentPlanner.LooksTruncated(segmentText))
        {
            return segmentText?.Trim() ?? string.Empty;
        }

        logger.LogWarning(
            "Transcription segment looks truncated; splitting in half and retrying. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}{Label}, EstimatedTokens={EstimatedTokens}",
            meeting.Id,
            segmentNumber,
            segmentCount,
            label,
            TranscriptionSegmentPlanner.EstimateTokens(segmentText));

        var halves = await segmentSplitter.SplitInHalfAsync(segmentPath, cancellationToken);
        if (halves is not { Count: 2 })
        {
            // 切不開就保留原本的結果：可能少了尾巴，但總比整段沒有好。
            logger.LogWarning(
                "Could not split truncated segment; keeping the original text. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}{Label}",
                meeting.Id,
                segmentNumber,
                segmentCount,
                label);

            return segmentText.Trim();
        }

        var parts = new List<string>(2);
        for (var half = 0; half < halves.Count; half++)
        {
            var text = await TranscribeSegmentAsync(
                provider,
                callContext,
                meeting,
                halves[half].FullPath,
                halves[half].Seconds,
                isDurationEstimated,
                segmentNumber,
                segmentCount,
                $"{label}（{(half == 0 ? "前半" : "後半")}）",
                depth + 1,
                alignment,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join("\n", parts);
    }

    private async Task<string> TranscribePlainAsync(
        ITranscriptionProvider provider,
        AiCallContext segmentContext,
        string segmentPath,
        double? seconds,
        bool isDurationEstimated,
        int segmentNumber,
        int segmentCount,
        CancellationToken cancellationToken)
    {
        await using var segmentStream = File.OpenRead(segmentPath);
        return await gateway.TranscribeAsync(
            provider,
            segmentContext,
            segmentStream,
            Path.GetFileName(segmentPath),
            seconds,
            isDurationEstimated,
            segmentNumber,
            segmentCount,
            cancellationToken);
    }

    /// <summary>
    /// 1.1.64 整場對齊：帶上目前所有的聲音參考轉錄這段；這段出現還沒有參考的講者，
    /// 就從這段音檔剪他最長的一句登記成新參考，並把他在這段的字母改成配發的編號。
    ///
    /// <para>
    /// 帶參考被拒（400，例如 Azure 不支援這兩個參數）時停用對齊、不帶參考重送這段，
    /// 這場之後照舊每段各自標字母——寧可沒有對齊，也不要整場轉錄失敗。
    /// </para>
    /// </summary>
    private async Task<string> TranscribeAlignedAsync(
        ISpeakerAwareTranscriptionProvider provider,
        AiCallContext segmentContext,
        int meetingId,
        string segmentPath,
        double? seconds,
        bool isDurationEstimated,
        int segmentNumber,
        int segmentCount,
        SpeakerAlignmentSession alignment,
        CancellationToken cancellationToken)
    {
        DiarizedTranscription result;
        try
        {
            await using var segmentStream = File.OpenRead(segmentPath);
            result = await gateway.TranscribeWithSpeakersAsync(
                provider,
                segmentContext,
                segmentStream,
                Path.GetFileName(segmentPath),
                alignment.References,
                seconds,
                isDurationEstimated,
                segmentNumber,
                segmentCount,
                cancellationToken);
        }
        catch (AiProviderHttpException ex) when (ex.StatusCode == 400 && alignment.References.Count > 0)
        {
            logger.LogWarning(
                ex,
                "Transcription rejected speaker references; disabling speaker alignment for this meeting. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}",
                meetingId,
                segmentNumber,
                segmentCount);
            alignment.Disable();

            return await TranscribePlainAsync(
                provider, segmentContext, segmentPath, seconds, isDurationEstimated,
                segmentNumber, segmentCount, cancellationToken);
        }

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (speaker, start, clipSeconds) in alignment.PlanNewReferences(result.Segments))
        {
            try
            {
                var clipPath = await clipCutter!.CutWavClipAsync(segmentPath, start, clipSeconds, cancellationToken);
                if (clipPath is null)
                {
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(clipPath, cancellationToken);
                renames[speaker] = alignment.Register("data:audio/wav;base64," + Convert.ToBase64String(bytes));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 剪不出參考只是這位講者少了對齊，不影響這段的逐字稿。
                logger.LogWarning(
                    ex,
                    "Could not cut speaker reference clip. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}, Speaker={Speaker}",
                    meetingId,
                    segmentNumber,
                    segmentCount,
                    speaker);
            }
        }

        if (renames.Count > 0)
        {
            logger.LogInformation(
                "Registered speaker references. MeetingId={MeetingId}, Segment={Segment}/{SegmentCount}, Total={Total}",
                meetingId,
                segmentNumber,
                segmentCount,
                alignment.References.Count);
        }

        var segments = SpeakerAlignmentSession.Relabel(result.Segments, renames);
        return DiarizedTranscriptFormatter.Format(segments, result.FallbackText);
    }

    /// <summary>
    /// 1.1.71：存下最終逐字稿裡真的有出現的講者的聲音參考（WAV），回傳「編號 → 相對路徑」。
    /// 存不了只記 Warning：試聽是輔助，不能讓已經完成的轉錄因此失敗。
    /// </summary>
    private async Task<Dictionary<string, string>> SaveSpeakerClipsAsync(
        Meeting meeting,
        IReadOnlyDictionary<string, byte[]> clips,
        string transcriptText,
        CancellationToken cancellationToken)
    {
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (clips.Count == 0)
        {
            return saved;
        }

        var used = TranscriptSpeakerLabels.ListAlignedSpeakers(transcriptText).ToHashSet(StringComparer.Ordinal);
        foreach (var (speaker, bytes) in clips.Where(x => used.Contains(x.Key)))
        {
            try
            {
                saved[speaker] = await fileStore.SaveSpeakerClipAsync(meeting.CreatedAt, speaker, bytes, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not save speaker clip. MeetingId={MeetingId}, Speaker={Speaker}", meeting.Id, speaker);
            }
        }

        return saved;
    }

    /// <summary>試聽片段的來源：Azure OpenAI 是整場對齊的聲音參考（data URL），LLM Speech 是轉錄時剪好的 wav。</summary>
    private static IReadOnlyDictionary<string, byte[]> CollectClips(
        SpeakerAlignmentSession? alignment,
        IReadOnlyDictionary<string, byte[]> llmSpeechClips)
    {
        if (alignment is null || alignment.References.Count == 0)
        {
            return llmSpeechClips;
        }

        const string prefix = "data:audio/wav;base64,";
        var clips = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var reference in alignment.References.Where(x => x.DataUrl.StartsWith(prefix, StringComparison.Ordinal)))
        {
            try
            {
                clips[reference.Name] = Convert.FromBase64String(reference.DataUrl[prefix.Length..]);
            }
            catch (FormatException)
            {
            }
        }

        return clips;
    }

    /// <summary>
    /// 1.1.73：用 LLM Speech 轉一段（通常是整個檔案）。分講者時講者編號在這一段內一致：
    /// 只有一段標成「講者 1」，切成多段時標成「講者 2-1」（第 2 段的講者 1），各段分開命名。
    /// 每位講者剪他最長的一句（2～10 秒）當試聽片段。
    /// </summary>
    private async Task<string> TranscribeLlmSpeechSegmentAsync(
        AzureLlmSpeechTranscriptionProvider provider,
        AiCallContext callContext,
        Meeting meeting,
        string segmentPath,
        double? seconds,
        bool isDurationEstimated,
        int segmentNumber,
        int segmentCount,
        Dictionary<string, byte[]> clips,
        CancellationToken cancellationToken)
    {
        var segmentContext = callContext with
        {
            RelatedInfo = $"會議 #{meeting.Id}〈{meeting.Title}〉語音轉錄（LLM Speech）{segmentNumber}/{segmentCount}",
        };

        DiarizedTranscription result;
        await using (var stream = File.OpenRead(segmentPath))
        {
            result = await gateway.TranscribeWithSpeakersAsync(
                provider, segmentContext, stream, Path.GetFileName(segmentPath), [],
                seconds, isDurationEstimated, segmentNumber, segmentCount, cancellationToken);
        }

        if (result.Segments.Count == 0)
        {
            return result.FallbackText.Trim();
        }

        var labelled = LabelLlmSpeechSegments(result.Segments, segmentNumber, segmentCount);

        if (clipCutter is not null)
        {
            foreach (var (speaker, start, clipSeconds) in PlanLlmSpeechClips(labelled))
            {
                try
                {
                    var clipPath = await clipCutter.CutWavClipAsync(segmentPath, start, clipSeconds, cancellationToken);
                    if (clipPath is not null)
                    {
                        clips[speaker] = await File.ReadAllBytesAsync(clipPath, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 剪不出試聽片段只是少了試聽，不影響逐字稿。
                    logger.LogWarning(ex, "Could not cut speaker clip. MeetingId={MeetingId}, Speaker={Speaker}", meeting.Id, speaker);
                }
            }
        }

        return AzureLlmSpeechTranscriptionProvider.FormatTranscript(labelled, result.FallbackText, diarize: true).Trim();
    }

    /// <summary>只有一段時講者代號維持「1」，切成多段時加上段落編號「2-1」。抽成純函式以便測試。</summary>
    internal static IReadOnlyList<DiarizedSegment> LabelLlmSpeechSegments(
        IReadOnlyList<DiarizedSegment> segments,
        int segmentNumber,
        int segmentCount)
    {
        if (segmentCount <= 1)
        {
            return segments;
        }

        return segments
            .Select(x => x.Speaker == TranscriptSpeakerLabels.UnknownSpeaker ? x : x with { Speaker = $"{segmentNumber}-{x.Speaker}" })
            .ToList();
    }

    /// <summary>每位講者挑最長的一句（至少 2 秒，最多取 10 秒）當試聽片段。</summary>
    internal static IReadOnlyList<(string Speaker, double Start, double Seconds)> PlanLlmSpeechClips(IReadOnlyList<DiarizedSegment> segments)
        => segments
            .Where(x => x.Speaker != TranscriptSpeakerLabels.UnknownSpeaker && x.Seconds >= SpeakerAlignmentSession.MinClipSeconds)
            .GroupBy(x => x.Speaker)
            .Select(group => group.OrderByDescending(x => x.Seconds).First())
            .Select(x => (x.Speaker, x.Start!.Value, Math.Min(x.Seconds!.Value, SpeakerAlignmentSession.MaxClipSeconds)))
            .ToList();

    /// <summary>1.1.73：LLM Speech 帶這一場的選項；片語清單取專案名稱＋與會人員名冊。</summary>
    private async Task<ITranscriptionProvider> ResolveLlmSpeechProviderAsync(Meeting meeting, CancellationToken cancellationToken)
    {
        if (llmSpeechProvider is null || !llmSpeechProvider.IsConfigured)
        {
            throw new InvalidOperationException(
                $"尚未設定 Azure LLM Speech（{TranscriptionSettings.SectionName}:AzureSpeech 的 Endpoint 與 ApiKey），請改用 Azure OpenAI 重新轉錄。");
        }

        IReadOnlyList<string> phrases = [];
        if (meeting.ProjectId is { } projectId)
        {
            var project = await context.Project.AsNoTracking()
                .Where(x => x.Id == projectId)
                .Select(x => new { x.Title, x.Participants })
                .FirstOrDefaultAsync(cancellationToken);
            phrases = TranscriptionOptionsResolver.BuildPhrases(project?.Title, project?.Participants);
        }

        return llmSpeechProvider.WithOptions(new LlmSpeechOptions(
            meeting.TranscriptionDiarize ?? true,
            meeting.TranscriptionMaxSpeakers,
            phrases));
    }

    /// <summary>
    /// 1.1.73：Azure OpenAI 依使用者選的分講者換部署（diarize ↔ 不分講者的部署）。
    /// 沒選（null）或沒註冊 resolver 時照設定的部署。
    /// </summary>
    private ITranscriptionProvider ResolveProvider(bool? diarize)
    {
        var provider = ResolveProvider();
        if (diarize is null || optionsResolver is null || provider is not AzureOpenAiTranscriptionProvider azure)
        {
            return provider;
        }

        var deployment = optionsResolver.ResolveOpenAiDeployment(diarize)
            ?? throw new InvalidOperationException(diarize.Value
                ? "Azure OpenAI 目前設定的轉錄部署不會分講者，請改選不分講者或 Azure LLM Speech 重新轉錄。"
                : "Azure OpenAI 沒有設定不分講者的轉錄部署（TranscriptionModelNoSpeakers），請改選分講者或 Azure LLM Speech 重新轉錄。");

        return string.Equals(deployment, azure.ModelName, StringComparison.OrdinalIgnoreCase)
            ? azure
            : azure.WithOverrides(new TranscriptionOverrides(deployment, null, null));
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
