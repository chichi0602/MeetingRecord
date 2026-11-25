using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.Diagnostics;

/// <summary>
/// 轉錄比較（1.1.72，只限管理者）：有人反映 diarize 模型轉出來的逐字稿太精簡、夾英文。
/// 這裡把同一段片段用幾組部署／參數各轉一次，並排比較，拿實際資料決定正式轉錄要怎麼改。
///
/// <para>
/// ⚠️ **不碰會議本身**：不改狀態、不存逐字稿、不進進度面板。費用照常經 <see cref="AiCallGateway"/> 記帳（功能別「轉錄比較」）。
/// 依序呼叫、不平行：帳本寫入共用同一個 scoped DbContext。同一時間只跑一趟。
/// </para>
///
/// <para>後處理和正式轉錄相同（指令外洩丟棄、轉繁體；diarize 由供應商排成「講者 X：」），比較才公平。片段只切一段、不做截斷重切。</para>
/// </summary>
public class TranscriptionComparisonService(
    BackendDBContext context,
    IEnumerable<ITranscriptionProvider> transcriptionProviders,
    IOptions<LlmSettings> llmSettings,
    IMediaExcerptCutter excerptCutter,
    MeetingFileStore fileStore,
    AiCallGateway gateway,
    TranscriptionCostEstimator costEstimator,
    CurrentUserService currentUserService,
    ILogger<TranscriptionComparisonService> logger,
    IAiCallerProvider? callerProvider = null,
    AzureLlmSpeechTranscriptionProvider? llmSpeechProvider = null)
{
    /// <summary>候選會議最多列幾場（最新的）。</summary>
    public const int MeetingTake = 200;

    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<TranscriptionCompareMetaDto> GetMetaAsync(CancellationToken cancellationToken = default)
    {
        var meetings = await context.Meeting
            .AsNoTracking()
            .Where(x => x.MediaRelativePath != null && x.MediaRelativePath != "")
            .OrderByDescending(x => x.CreatedAt)
            .Take(MeetingTake)
            .Select(x => new TranscriptionCompareMeetingDto
            {
                Id = x.Id,
                Title = x.Title,
                MeetingDate = x.MeetingDate,
                MediaFileName = x.MediaOriginalFileName ?? string.Empty,
                MediaDurationSeconds = x.MediaDurationSeconds,
            })
            .ToListAsync(cancellationToken);

        return new TranscriptionCompareMetaDto
        {
            ConfiguredDeployment = llmSettings.Value.GetTranscriptionProvider()?.TranscriptionModel.Trim() ?? string.Empty,
            LlmSpeechAvailable = llmSpeechProvider?.IsConfigured == true,
            Meetings = meetings,
        };
    }

    /// <summary>每組的預估費用：片段長度 × 該部署的單價。</summary>
    public TranscriptionCompareEstimateDto Estimate(TranscriptionCompareRequestDto request)
    {
        var configured = llmSettings.Value.GetTranscriptionProvider()?.TranscriptionModel;
        var costs = request.Variants
            .Select(x => costEstimator.EstimateTwd(
                request.Seconds,
                x.Engine == (int)TranscriptionEngine.AzureLlmSpeech ? AzureSpeechSettings.PricingModel
                : string.IsNullOrWhiteSpace(x.Deployment) ? configured : x.Deployment))
            .ToList();

        return new TranscriptionCompareEstimateDto
        {
            VariantTwd = costs,
            TotalTwd = costs.Count > 0 && costs.All(x => x is not null) ? costs.Sum() : null,
        };
    }

    /// <summary>請求有問題時回原因（給 400）；沒問題回 null。</summary>
    public static string? Validate(TranscriptionCompareRequestDto request)
    {
        if (request.Variants.Count is 0 or > TranscriptionCompareRequestDto.MaxVariants)
        {
            return $"比較條件要有 1～{TranscriptionCompareRequestDto.MaxVariants} 組。";
        }

        if (request.Seconds is < TranscriptionCompareRequestDto.MinSeconds or > TranscriptionCompareRequestDto.MaxSeconds)
        {
            return $"片段長度要在 {TranscriptionCompareRequestDto.MinSeconds}～{TranscriptionCompareRequestDto.MaxSeconds} 秒之間。";
        }

        // 1.1.73：引擎要認得；LLM Speech 分講者時講者上限要在 API 允許的範圍。
        foreach (var variant in request.Variants)
        {
            if (!Enum.IsDefined(typeof(TranscriptionEngine), variant.Engine))
            {
                return "轉錄引擎不正確。";
            }

            if (variant.Engine == (int)TranscriptionEngine.AzureLlmSpeech
                && variant.Diarize
                && variant.MaxSpeakers is < AzureSpeechSettings.MinSpeakers or > AzureSpeechSettings.MaxSpeakers)
            {
                return $"講者上限要在 {AzureSpeechSettings.MinSpeakers}～{AzureSpeechSettings.MaxSpeakers} 之間。";
            }
        }

        return request.StartSeconds < 0 ? "起點不可小於 0。" : null;
    }

    /// <summary>
    /// 剪片段、逐組轉錄。每個事件（先片段、再每組結果）都經 <paramref name="onEvent"/> 回報；
    /// 剪片段之前就失敗會丟例外（呼叫端回一般錯誤）。單組失敗不中斷，寫在那一組的結果裡。
    /// </summary>
    public async Task<int> RunAsync(
        TranscriptionCompareRequestDto request,
        Action<TranscriptionCompareEventDto> onEvent,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } invalid)
        {
            throw new InvalidOperationException(invalid);
        }

        var meeting = await context.Meeting.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.MeetingId, cancellationToken)
            ?? throw new InvalidOperationException("找不到這場會議。");
        if (string.IsNullOrWhiteSpace(meeting.MediaRelativePath))
        {
            throw new InvalidOperationException("這場會議沒有影音檔。");
        }

        var usesLlmSpeech = request.Variants.Any(x => x.Engine == (int)TranscriptionEngine.AzureLlmSpeech);
        var usesOpenAi = request.Variants.Any(x => x.Engine == (int)TranscriptionEngine.AzureOpenAI);
        var provider = transcriptionProviders.OfType<AzureOpenAiTranscriptionProvider>().FirstOrDefault();
        if (usesOpenAi && provider is null)
        {
            throw new InvalidOperationException("轉錄比較的 Azure OpenAI 組需要 Azure OpenAI 的語音轉錄。");
        }

        if (usesLlmSpeech && llmSpeechProvider?.IsConfigured != true)
        {
            throw new InvalidOperationException("尚未設定 Azure LLM Speech（TranscriptionSettings:AzureSpeech 的 Endpoint 與 ApiKey）。");
        }

        // LLM Speech 的片語清單和正式轉錄一樣：專案名稱＋與會人員名冊。
        IReadOnlyList<string> phrases = [];
        if (usesLlmSpeech && meeting.ProjectId is { } projectId)
        {
            var project = await context.Project.AsNoTracking()
                .Where(x => x.Id == projectId)
                .Select(x => new { x.Title, x.Participants })
                .FirstOrDefaultAsync(cancellationToken);
            phrases = TranscriptionOptionsResolver.BuildPhrases(project?.Title, project?.Participants);
        }

        if (!await RunLock.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            throw new InvalidOperationException("已經有一趟轉錄比較在跑，請等它結束再試。");
        }

        try
        {

            var excerpt = await excerptCutter.CutMp3ExcerptAsync(
                fileStore.GetMediaFullPath(meeting.MediaRelativePath), request.StartSeconds, request.Seconds, cancellationToken)
                ?? throw new InvalidOperationException("剪不出片段：起點可能超過影音檔長度。");

            onEvent(new TranscriptionCompareEventDto
            {
                Type = TranscriptionCompareEventDto.Excerpt,
                AudioDataUrl = "data:audio/mpeg;base64," + Convert.ToBase64String(excerpt.Mp3),
                ExcerptSeconds = excerpt.Seconds,
            });

            var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser, callerProvider);
            var range = $"{FormatClock(request.StartSeconds)}～{FormatClock(request.StartSeconds + excerpt.Seconds)}";
            for (var i = 0; i < request.Variants.Count; i++)
            {
                var variant = request.Variants[i];
                ITranscriptionProvider configured;
                string label;
                if (variant.Engine == (int)TranscriptionEngine.AzureLlmSpeech)
                {
                    configured = llmSpeechProvider!.WithOptions(new LlmSpeechOptions(variant.Diarize, variant.MaxSpeakers, phrases));
                    label = TranscriptionEngineText.Describe(variant.Engine, variant.Diarize);
                }
                else
                {
                    configured = provider!.WithOverrides(new TranscriptionOverrides(variant.Deployment, variant.Language, variant.Prompt));
                    label = configured.ModelName;
                }

                var callContext = new AiCallContext(
                    AiUsageFeature.TranscriptionComparison,
                    userId,
                    userName,
                    MeetingId: meeting.Id,
                    RelatedInfo: $"轉錄比較：會議 #{meeting.Id}〈{meeting.Title}〉{range}，第 {i + 1} 組 {label}");

                onEvent(await TranscribeVariantAsync(i, configured, label, callContext, excerpt, cancellationToken));
            }

            return request.Variants.Count;
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<TranscriptionCompareEventDto> TranscribeVariantAsync(
        int index,
        ITranscriptionProvider provider,
        string label,
        AiCallContext callContext,
        MediaExcerpt excerpt,
        CancellationToken cancellationToken)
    {
        var result = new TranscriptionCompareEventDto { Type = TranscriptionCompareEventDto.Result, Index = index, Deployment = label };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var audio = new MemoryStream(excerpt.Mp3, writable: false);
            var text = await gateway.TranscribeAsync(
                provider, callContext, audio, "excerpt.mp3", excerpt.Seconds, isDurationEstimated: false, segmentIndex: 1, segmentCount: 1, cancellationToken);

            if (TranscriptionNoiseFilter.IsProviderInstructionLeak(text))
            {
                result.Message = "模型吐回了自己的指令（正式轉錄會整段丟棄）。";
                text = string.Empty;
            }

            text = ChineseTextConverter.ToTraditional(text?.Trim()) ?? string.Empty;
            result.Success = true;
            result.Text = text;
            result.CharCount = text.Count(c => !char.IsWhiteSpace(c));
            result.LatinRatio = LatinRatio(text);
            result.CostTwd = costEstimator.EstimateTwd(excerpt.Seconds, provider.ModelName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Transcription comparison variant failed. Index={Index}, Deployment={Deployment}", index, provider.ModelName);
            result.Message = AiConnectionTestService.Classify(ex, provider.ModelName);
        }

        result.ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        return result;
    }

    /// <summary>
    /// 英文字母占「字母與數字」的比例（中文字也算字母）。量化「逐字稿夾了很多英文」：
    /// 全中文的會議通常很低，片段裡突然變高就值得聽一下原音。抽成 internal static 以便單元測試。
    /// </summary>
    internal static double LatinRatio(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var total = 0;
        var latin = 0;
        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c))
            {
                continue;
            }

            total++;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            {
                latin++;
            }
        }

        return total == 0 ? 0 : Math.Round((double)latin / total, 3);
    }

    private static string FormatClock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }
}
