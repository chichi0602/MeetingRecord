using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.TextGeneration;

/// <summary>
/// 單一會議的草稿生成工作流程：讀逐字稿 → 切段 → （多段時）逐段摘要 → 套提示詞 → 落庫。
///
/// 由背景服務在自己的 DI scope 內解析執行，不經過任何 Blazor circuit，
/// 所以這裡刻意不做團隊權限檢查（入列的動作已在服務層檢查過）。
/// </summary>
public class MeetingDraftJobRunner
{
    /// <summary>各段摘要之間的接合字串，與逐字稿本身的分段接合方式一致。</summary>
    private const string SummarySeparator = "\n\n";

    /// <summary>
    /// 所有呼叫共用的系統指令。輸出語言在此強制，避免每個提示詞範本都要自己交代一次。
    /// </summary>
    private const string SystemPrompt =
        "你是專業的會議紀錄助理。請全程使用繁體中文（台灣用語）回答，不要使用簡體字或中國大陸用語。" +
        "只根據提供的逐字稿內容作答，不要臆測或補充逐字稿沒有提到的資訊。";

    private readonly BackendDBContext context;
    private readonly IEnumerable<ITextGenerationProvider> textGenerationProviders;
    private readonly MeetingFileStore fileStore;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly IMeetingDraftProgressNotifier progressNotifier;
    private readonly ILogger<MeetingDraftJobRunner> logger;

    public MeetingDraftJobRunner(
        BackendDBContext context,
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        MeetingFileStore fileStore,
        IOptions<LlmSettings> llmSettings,
        IMeetingDraftProgressNotifier progressNotifier,
        ILogger<MeetingDraftJobRunner> logger)
    {
        this.context = context;
        this.textGenerationProviders = textGenerationProviders;
        this.fileStore = fileStore;
        this.llmSettings = llmSettings;
        this.progressNotifier = progressNotifier;
        this.logger = logger;
    }

    public async Task RunAsync(int meetingId, CancellationToken cancellationToken)
    {
        var meeting = await context.Meeting.FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken);
        if (meeting is null)
        {
            logger.LogWarning("Draft generation skipped because meeting was not found. MeetingId={MeetingId}", meetingId);
            return;
        }

        if (meeting.TranscriptionStatus != TranscriptionStatus.Completed
            || string.IsNullOrWhiteSpace(meeting.TranscriptRelativePath))
        {
            // 入列時已檢查過轉錄狀態，走到這裡代表期間又被重新轉錄了。
            // 這種情況不能停在「待處理」不動，否則這筆會永遠卡著沒人接手。
            logger.LogWarning("Draft generation skipped because transcript is not ready. MeetingId={MeetingId}", meetingId);
            await MarkFailedAsync(meeting, "逐字稿尚未轉錄完成，請待轉錄完成後重新產生會議紀錄。");
            return;
        }

        meeting.DraftStatus = DraftStatus.Processing;
        meeting.DraftStartedAt = DateTime.Now;
        meeting.DraftCompletedAt = null;
        meeting.DraftError = null;
        await context.SaveChangesAsync(cancellationToken);

        progressNotifier.ReportPreparing(meetingId);

        try
        {
            var provider = ResolveProvider();
            var template = await ResolveTemplateContentAsync(meeting.DraftPromptTemplateId, cancellationToken);

            var transcript = await fileStore.ReadTranscriptAsync(meeting.TranscriptRelativePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new InvalidOperationException("找不到逐字稿內容，或逐字稿為空白，無法產生會議紀錄。");
            }

            var condensed = await CondenseAsync(provider, transcript, meetingId, cancellationToken);

            var userPrompt = PromptVariableHelper.Render(template, new Dictionary<string, string?>
            {
                ["transcript"] = condensed,
                ["meetingTitle"] = meeting.Title,
                ["meetingDate"] = meeting.MeetingDate?.ToString("yyyy/MM/dd"),
            });

            cancellationToken.ThrowIfCancellationRequested();
            progressNotifier.ReportGenerating(meetingId);
            var draft = await provider.GenerateAsync(
                SystemPrompt,
                userPrompt,
                characters => progressNotifier.ReportGeneratedCharacters(meetingId, characters),
                cancellationToken);

            meeting.DraftContent = draft;
            meeting.DraftStatus = DraftStatus.Completed;
            meeting.DraftError = null;
            meeting.DraftCompletedAt = DateTime.Now;
            meeting.UpdatedAt = DateTime.Now;
            await context.SaveChangesAsync(cancellationToken);

            progressNotifier.ReportCompleted(meetingId);

            logger.LogInformation(
                "Draft generation completed. MeetingId={MeetingId}, Length={Length}",
                meetingId,
                draft.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Draft generation failed. MeetingId={MeetingId}", meetingId);
            await MarkFailedAsync(meeting, ex.Message);
        }
    }

    /// <summary>
    /// map-reduce 的 map 段：逐字稿超過單次上限時先逐段摘要再合併，否則直接使用原文。
    ///
    /// 只有一段時刻意不做摘要——對短會議而言「摘要後再整理」等於資訊被壓縮兩次，
    /// 反而讓品質下降；多數會議都落在這條路徑上。
    /// </summary>
    private async Task<string> CondenseAsync(
        ITextGenerationProvider provider,
        string transcript,
        int meetingId,
        CancellationToken cancellationToken)
    {
        var chunks = TranscriptChunker.Split(transcript);
        if (chunks.Count <= 1)
        {
            logger.LogInformation("Draft generation uses full transcript. MeetingId={MeetingId}", meetingId);
            return transcript;
        }

        logger.LogInformation(
            "Transcript exceeds single-call limit, summarising in chunks. MeetingId={MeetingId}, ChunkCount={ChunkCount}",
            meetingId,
            chunks.Count);

        progressNotifier.ReportSummarizing(meetingId, 0, chunks.Count);

        var summaries = new StringBuilder();
        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Summarising transcript chunk. MeetingId={MeetingId}, Chunk={Chunk}/{ChunkCount}",
                meetingId,
                index + 1,
                chunks.Count);

            var chunkPrompt =
                $"以下是一場會議逐字稿的第 {index + 1}/{chunks.Count} 段。" +
                "請摘要這一段的討論重點、決議與待辦，並保留人名、日期、數字與專有名詞等具體資訊。" +
                "不要加上開場白或結語，直接輸出摘要內容。\n\n" +
                chunks[index];

            // 這裡不接字元進度：分段摘要本來就是逐段回報，粒度已經夠細。
            var summary = await provider.GenerateAsync(SystemPrompt, chunkPrompt, null, cancellationToken);

            // 進度必須在 continue 之前回報，否則整段空白的段落會被跳過不計。
            progressNotifier.ReportSummarizing(meetingId, index + 1, chunks.Count);

            if (string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            if (summaries.Length > 0)
            {
                summaries.Append(SummarySeparator);
            }

            summaries.Append($"【第 {index + 1} 段】\n{summary.Trim()}");
        }

        if (summaries.Length == 0)
        {
            throw new InvalidOperationException("逐字稿分段摘要全部為空白，無法產生會議紀錄。");
        }

        return summaries.ToString();
    }

    /// <summary>依 <c>LlmSettings.DefaultProvider</c> 挑出對應的供應商實作。</summary>
    private ITextGenerationProvider ResolveProvider()
    {
        var settings = llmSettings.Value;
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException(
                $"尚未設定文字生成供應商，請在 {LlmSettings.SectionName}:DefaultProvider 指定。");
        }

        var name = settings.DefaultProvider.Trim();
        var provider = textGenerationProviders
            .FirstOrDefault(x => string.Equals(x.ProviderName, name, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            var supported = string.Join("、", textGenerationProviders.Select(x => x.ProviderName));
            throw new InvalidOperationException(
                $"找不到名為「{name}」的文字生成供應商實作。目前支援：{supported}。");
        }

        return provider;
    }

    /// <summary>
    /// 取出提示詞範本內容。這裡不套團隊權控——入列時服務層已檢查過，
    /// 且背景工作沒有登入者的身分脈絡。
    /// </summary>
    private async Task<string> ResolveTemplateContentAsync(int? promptTemplateId, CancellationToken cancellationToken)
    {
        if (promptTemplateId is null or 0)
        {
            throw new InvalidOperationException("尚未指定提示詞範本，無法產生會議紀錄。");
        }

        var template = await context.PromptTemplate.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == promptTemplateId, cancellationToken);

        if (template is null)
        {
            throw new InvalidOperationException($"找不到 Id 為 {promptTemplateId} 的提示詞範本，可能已被刪除。");
        }

        if (string.IsNullOrWhiteSpace(template.Content))
        {
            throw new InvalidOperationException($"提示詞範本「{template.Name}」的內容為空白。");
        }

        return template.Content;
    }

    private async Task MarkFailedAsync(AccessDatas.Models.Meeting meeting, string message)
    {
        progressNotifier.ReportFailed(meeting.Id, message);

        try
        {
            meeting.DraftStatus = DraftStatus.Failed;
            meeting.DraftError = message;
            meeting.DraftCompletedAt = DateTime.Now;
            meeting.UpdatedAt = DateTime.Now;

            // 失敗狀態必須寫得進去，因此這裡不帶入已取消的 CancellationToken。
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist draft failure state. MeetingId={MeetingId}", meeting.Id);
        }
    }
}
