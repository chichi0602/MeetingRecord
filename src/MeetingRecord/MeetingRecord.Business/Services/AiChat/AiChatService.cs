using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiChat;

/// <summary>問答的對象。</summary>
public enum AiChatScope
{
    /// <summary>整個專案：讀本專案的所有會議紀錄與附件。</summary>
    Project = 0,

    /// <summary>單一會議：讀該會議的會議紀錄與逐字稿。</summary>
    Meeting = 1,
}

/// <summary>畫面用的一則對話訊息。</summary>
public sealed record AiChatMessageItem(string Role, string Content, string? AskedBy, DateTime CreatedAt)
{
    public bool IsUser => string.Equals(Role, AiChatService.UserRole, StringComparison.Ordinal);
}

/// <summary>一次提問的結果：回答本身，以及這次實際讀到了什麼。</summary>
public sealed record AiChatAnswer(
    string Answer,
    IReadOnlyList<string> UsedLabels,
    IReadOnlyList<string> TruncatedLabels,
    IReadOnlyList<string> SkippedLabels);

/// <summary>
/// 以專案或單一會議為範圍的 AI 問答。
///
/// <para>
/// 直接複用既有的 <see cref="ITextGenerationProvider"/>（轉錄與會議紀錄生成也走它），
/// 不另接一條到 Azure OpenAI 的路徑。
/// </para>
/// </summary>
public class AiChatService
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    /// <summary>
    /// 帶進提示詞的歷史輪數上限（一輪＝一問一答）。
    ///
    /// 更早的對話仍留在資料庫供畫面翻閱，只是不再送進模型——脈絡本身就吃掉大量長度，
    /// 歷史無上限地累積會讓每一次提問都比上一次更貴。
    /// </summary>
    public const int MaxHistoryTurns = 6;

    private const string SystemPrompt =
        "你是會議資料的問答助理。請全程使用繁體中文（台灣用語）回答，不要使用簡體字或中國大陸用語。" +
        "只根據提供的『參考資料』回答問題；資料中找不到答案時，直接說明找不到，" +
        "**不要臆測、不要引用參考資料以外的知識**。回答時盡量指出資訊來自哪一份資料。";

    private readonly BackendDBContext context;
    private readonly IEnumerable<ITextGenerationProvider> textGenerationProviders;
    private readonly MeetingFileStore fileStore;
    private readonly AiChatStore chatStore;
    private readonly AttachmentTextExtractor attachmentTextExtractor;
    private readonly CurrentUserService currentUserService;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly ILogger<AiChatService> logger;

    public AiChatService(
        BackendDBContext context,
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        MeetingFileStore fileStore,
        AiChatStore chatStore,
        AttachmentTextExtractor attachmentTextExtractor,
        CurrentUserService currentUserService,
        IOptions<LlmSettings> llmSettings,
        ILogger<AiChatService> logger)
    {
        this.context = context;
        this.textGenerationProviders = textGenerationProviders;
        this.fileStore = fileStore;
        this.chatStore = chatStore;
        this.attachmentTextExtractor = attachmentTextExtractor;
        this.currentUserService = currentUserService;
        this.llmSettings = llmSettings;
        this.logger = logger;
    }

    /// <summary>取出這段對話的完整歷史（依時間由舊到新）。</summary>
    public Task<List<AiChatMessageItem>> GetHistoryAsync(
        AiChatScope scope,
        int targetId,
        CancellationToken cancellationToken = default)
        => chatStore.ReadHistoryAsync(scope, targetId, cancellationToken);

    /// <summary>清空這段對話（直接刪掉那個對話檔）。</summary>
    public Task ClearHistoryAsync(
        AiChatScope scope,
        int targetId,
        CancellationToken cancellationToken = default)
    {
        chatStore.TryDelete(scope, targetId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 提問。回答會邊生成邊透過 <paramref name="onDelta"/> 回報，結束後連同提問一起寫進對話檔。
    /// </summary>
    public async Task<AiChatAnswer> AskAsync(
        AiChatScope scope,
        int targetId,
        string question,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var provider = ResolveProvider();

        var contextResult = scope == AiChatScope.Project
            ? await BuildProjectContextAsync(targetId, cancellationToken)
            : await BuildMeetingContextAsync(targetId, cancellationToken);

        if (!contextResult.HasContent)
        {
            throw new InvalidOperationException(
                scope == AiChatScope.Project
                    ? "這個專案目前沒有可供查詢的資料：尚未產生任何會議紀錄，附件也沒有可擷取的文字。"
                    : "這場會議目前沒有可供查詢的資料：尚未產生會議紀錄，也沒有逐字稿。");
        }

        var history = await GetHistoryAsync(scope, targetId, cancellationToken);
        var userPrompt = BuildUserPrompt(contextResult.Context, history, question);

        var answer = await provider.GenerateAsync(SystemPrompt, userPrompt, onDelta, cancellationToken);

        await SaveTurnAsync(scope, targetId, question, answer, cancellationToken);

        logger.LogInformation(
            "AI chat answered. Scope={Scope}, TargetId={TargetId}, ContextLength={ContextLength}, AnswerLength={AnswerLength}",
            scope,
            targetId,
            contextResult.Context.Length,
            answer.Length);

        return new AiChatAnswer(
            answer,
            contextResult.UsedLabels,
            contextResult.TruncatedLabels,
            contextResult.SkippedLabels);
    }

    /// <summary>
    /// 組出送進模型的使用者訊息：參考資料 → 先前對話 → 這次的問題。
    ///
    /// 抽成 internal static 純函式以便單元測試（本專案的既有慣例）。
    /// </summary>
    internal static string BuildUserPrompt(
        string contextText,
        IReadOnlyList<AiChatMessageItem> history,
        string question)
    {
        var builder = new StringBuilder();

        builder.AppendLine("以下是可供你回答的參考資料：");
        builder.AppendLine();
        builder.AppendLine(contextText);

        var recent = TakeRecentHistory(history, MaxHistoryTurns);
        if (recent.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("---");
            builder.AppendLine("先前的對話（供理解代名詞與追問脈絡）：");
            foreach (var message in recent)
            {
                builder.AppendLine($"{(message.IsUser ? "使用者" : "助理")}：{message.Content}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine($"問題：{question.Trim()}");

        return builder.ToString();
    }

    /// <summary>
    /// 取最近 <paramref name="maxTurns"/> 輪對話。不足時全取。
    ///
    /// 一輪是一問一答，所以取的訊息數是輪數的兩倍。
    /// </summary>
    internal static IReadOnlyList<AiChatMessageItem> TakeRecentHistory(
        IReadOnlyList<AiChatMessageItem> history,
        int maxTurns)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTurns);

        var maxMessages = maxTurns * 2;
        if (history.Count <= maxMessages)
        {
            return history;
        }

        return [.. history.Skip(history.Count - maxMessages)];
    }

    /// <summary>專案層級的脈絡：先放各份會議紀錄（短且已整理過），再放附件全文。</summary>
    private async Task<ChatContextResult> BuildProjectContextAsync(int projectId, CancellationToken cancellationToken)
    {
        var meetings = await context.Meeting.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.DraftCompletedAt ?? x.CreatedAt)
            .Select(x => new { x.Title, x.MeetingDate, x.DraftContent })
            .ToListAsync(cancellationToken);

        var files = await context.ProjectFile.AsNoTracking()
            .Where(x => x.ProjectId == projectId)
            .OrderBy(x => x.Id)
            .Select(x => new { x.OriginalFileName, x.RelativePath })
            .ToListAsync(cancellationToken);

        var sources = new List<ChatSource>();

        foreach (var meeting in meetings)
        {
            var date = meeting.MeetingDate?.ToString("yyyy/MM/dd") ?? "日期未填";
            sources.Add(new ChatSource($"會議紀錄：{meeting.Title}（{date}）", meeting.DraftContent));
        }

        foreach (var file in files)
        {
            // 每次提問都重新擷取一次。刻意不做快取——Business 層取用不到 Web 層的
            // ICacheService（分層是 Web → Business），為此另引一套快取不划算。
            var text = attachmentTextExtractor.TryExtract(file.RelativePath, file.OriginalFileName);

            sources.Add(new ChatSource($"附件：{file.OriginalFileName}", text));
        }

        return ChatContextBuilder.Build(sources);
    }

    /// <summary>單一會議的脈絡：會議紀錄優先（摘要），再放逐字稿（長，可能被截）。</summary>
    private async Task<ChatContextResult> BuildMeetingContextAsync(int meetingId, CancellationToken cancellationToken)
    {
        var meeting = await context.Meeting.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == meetingId, cancellationToken)
            ?? throw new InvalidOperationException($"找不到 Id 為 {meetingId} 的會議紀錄，可能已被刪除。");

        var transcript = await fileStore.ReadTranscriptAsync(meeting.TranscriptRelativePath, cancellationToken);

        var sources = new List<ChatSource>
        {
            new($"會議紀錄：{meeting.Title}", meeting.DraftContent),
            new($"逐字稿：{meeting.Title}", transcript),
        };

        return ChatContextBuilder.Build(sources);
    }

    private Task SaveTurnAsync(
        AiChatScope scope,
        int targetId,
        string question,
        string answer,
        CancellationToken cancellationToken)
        => chatStore.AppendTurnAsync(
            scope, targetId, question, ResolveCurrentUserName(), answer, cancellationToken);

    private string? ResolveCurrentUserName()
    {
        var user = currentUserService.CurrentUser;

        return !string.IsNullOrWhiteSpace(user.Name)
            ? user.Name
            : !string.IsNullOrWhiteSpace(user.Account)
                ? user.Account
                : null;
    }

    /// <summary>依 <c>LlmSettings.DefaultProvider</c> 挑出供應商，形狀比照 <c>MeetingDraftJobRunner</c>。</summary>
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
}
