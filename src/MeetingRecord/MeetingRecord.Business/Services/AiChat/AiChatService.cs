using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

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
public sealed record AiChatMessageItem(
    string Role,
    string Content,
    string? AskedBy,
    DateTime CreatedAt,
    IReadOnlyList<AiChatAttachment>? Attachments = null)
{
    public bool IsUser => string.Equals(Role, AiChatService.UserRole, StringComparison.Ordinal);

    /// <summary>這則提問附上的圖片與文件（0.4.95）。回答與舊訊息一律是空清單。</summary>
    public IReadOnlyList<AiChatAttachment> AttachmentList => Attachments ?? [];
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
        "只根據提供的『參考資料』（包含使用者附上的檔案與圖片）回答問題；資料中找不到答案時，直接說明找不到，" +
        "**不要臆測、不要引用參考資料以外的知識**。回答時盡量指出資訊來自哪一份資料。";

    private readonly BackendDBContext context;
    private readonly IEnumerable<ITextGenerationProvider> textGenerationProviders;
    private readonly MeetingFileStore fileStore;
    private readonly AiChatStore chatStore;
    private readonly AttachmentTextExtractor attachmentTextExtractor;
    private readonly CurrentUserService currentUserService;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly AiUsageRecorder usageRecorder;
    private readonly ILogger<AiChatService> logger;

    public AiChatService(
        BackendDBContext context,
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        MeetingFileStore fileStore,
        AiChatStore chatStore,
        AttachmentTextExtractor attachmentTextExtractor,
        CurrentUserService currentUserService,
        IOptions<LlmSettings> llmSettings,
        AiUsageRecorder usageRecorder,
        ILogger<AiChatService> logger)
    {
        this.context = context;
        this.textGenerationProviders = textGenerationProviders;
        this.fileStore = fileStore;
        this.chatStore = chatStore;
        this.attachmentTextExtractor = attachmentTextExtractor;
        this.currentUserService = currentUserService;
        this.llmSettings = llmSettings;
        this.usageRecorder = usageRecorder;
        this.logger = logger;
    }

    /// <summary>
    /// 列出這個對象底下的所有對話，最近更新的排最前面。
    /// 會順帶把 0.4.79 之前的單檔對話搬進新結構。
    /// </summary>
    public Task<IReadOnlyList<AiChatConversationInfo>> ListConversationsAsync(
        AiChatScope scope,
        int targetId,
        CancellationToken cancellationToken = default)
        => chatStore.ListConversationsAsync(scope, targetId, cancellationToken);

    /// <summary>開一段新對話，回傳它的 Id。建立者記的是現在這個人。</summary>
    public Task<string> CreateConversationAsync(
        AiChatScope scope,
        int targetId,
        CancellationToken cancellationToken = default)
        => chatStore.CreateConversationAsync(scope, targetId, ResolveCurrentUserName(), cancellationToken);

    /// <summary>
    /// 改這段對話的名字。<b>不呼叫模型，不會產生費用。</b>
    /// 沒改過名的對話標題會自動取第一句提問，改過之後就固定用改的。
    /// </summary>
    public Task<UpdateOutcome> RenameConversationAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        string title,
        CancellationToken cancellationToken = default)
        => chatStore.RenameConversationAsync(scope, targetId, conversationId, title, cancellationToken);

    /// <summary>取出這段對話的完整歷史（依時間由舊到新）。</summary>
    public Task<List<AiChatMessageItem>> GetHistoryAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        CancellationToken cancellationToken = default)
        => chatStore.ReadHistoryAsync(scope, targetId, conversationId, cancellationToken);

    /// <summary>刪掉這一段對話（同一個對象底下的其他段不受影響）。</summary>
    public Task ClearHistoryAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        chatStore.TryDeleteConversation(scope, targetId, conversationId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 提問。回答會邊生成邊透過 <paramref name="onDelta"/> 回報，結束後連同提問一起寫進對話檔。
    /// </summary>
    public async Task<AiChatAnswer> AskAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        string question,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<PendingAttachment>? attachments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var pending = attachments ?? [];
        ValidateAttachments(pending);

        var history = await GetHistoryAsync(scope, targetId, conversationId, cancellationToken);

        // 附件先落地再生成：文件要從檔案擷取文字，而且「這次的」與「歷史的」附件走同一條讀取路徑。
        // 生成或寫入失敗時一定要刪掉，否則沒寫進對話的附件會變成硬碟上的孤兒。
        var saved = await chatStore.SaveAttachmentsAsync(scope, targetId, conversationId, pending, cancellationToken);

        string answer;
        ChatContextResult contextResult;
        try
        {
            (answer, contextResult) = await GenerateAnswerAsync(
                scope, targetId, conversationId, question, history, saved, onDelta, cancellationToken);

            await chatStore.AppendTurnAsync(
                scope, targetId, conversationId, question, ResolveCurrentUserName(), answer, cancellationToken, saved);
        }
        catch
        {
            chatStore.TryDeleteAttachments(scope, targetId, conversationId, saved);
            throw;
        }

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
    /// 就地更正一則訊息的文字。<b>不呼叫模型，不會產生費用</b>——這是「打錯字」「答案裡有個
    /// 明顯錯誤」時用的，不是重新發問。
    ///
    /// 傳整個 <paramref name="original"/> 而不只傳內容：對話是同專案／會議底下所有人共用的，
    /// 角色與原內容要一起當樂觀鎖，才擋得掉「兩個人同時編輯、後寫的默默蓋掉前者」。
    /// </summary>
    public Task<UpdateOutcome> UpdateMessageAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        int index,
        AiChatMessageItem original,
        string newContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);

        return chatStore.UpdateMessagesAsync(
            scope,
            targetId,
            conversationId,
            [new MessageEdit(index, original.Role, original.Content, newContent)],
            cancellationToken);
    }

    /// <summary>
    /// 改寫某一則提問並重新產生它的回答，覆蓋原本那一則。
    ///
    /// ⚠️ <b>會呼叫模型、會產生費用</b>，呼叫端必須先跳二次確認（§6.3）。
    ///
    /// 餵給模型的歷史只取「這一輪之前」的訊息——把後面的輪次也帶進去，模型會看到
    /// 自己還沒被改寫的舊答案，等於拿未來解釋過去。
    /// </summary>
    public async Task<AiChatAnswer> RegenerateAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        int questionIndex,
        string expectedQuestion,
        string newQuestion,
        Action<string>? onDelta = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newQuestion);

        var history = await GetHistoryAsync(scope, targetId, conversationId, cancellationToken);

        // 先擋掉對不上的情況再送 API——確認之後才花錢。
        if (questionIndex < 0 || questionIndex >= history.Count || !history[questionIndex].IsUser)
        {
            throw new InvalidOperationException("這則提問已經不在對話裡了，請重新整理後再試。");
        }

        if (!string.Equals(history[questionIndex].Content, expectedQuestion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("這則提問已被其他人改過，請重新整理後再試。");
        }

        var answerIndex = questionIndex + 1;
        if (answerIndex >= history.Count || history[answerIndex].IsUser)
        {
            throw new InvalidOperationException("找不到這則提問對應的回答，請重新整理後再試。");
        }

        var trimmed = newQuestion.Trim();

        // 改寫的是原本那一則提問，所以它當時附上的檔案仍然算「這次的附件」（0.4.95）。
        // 附件本身不動——UpdateMessagesAsync 只改文字，附件欄位原樣保留。
        var (answer, contextResult) = await GenerateAnswerAsync(
            scope,
            targetId,
            conversationId,
            trimmed,
            TakeHistoryBefore(history, questionIndex),
            history[questionIndex].AttachmentList,
            onDelta,
            cancellationToken);

        // 提問與回答必須一起換掉。只改到一半（問題改了、答案還是舊的）是最糟的中間態，
        // 所以兩筆一次送進同一個原子重寫。
        var outcome = await chatStore.UpdateMessagesAsync(
            scope,
            targetId,
            conversationId,
            [
                new MessageEdit(
                    questionIndex,
                    UserRole,
                    history[questionIndex].Content,
                    trimmed,
                    // 換成實際操作的人，否則「王小明問的」其實是李小華改的。
                    ResolveCurrentUserName()),
                new MessageEdit(
                    answerIndex,
                    AssistantRole,
                    history[answerIndex].Content,
                    answer),
            ],
            cancellationToken);

        if (outcome != UpdateOutcome.Updated)
        {
            logger.LogWarning(
                "Regenerated answer was discarded. Scope={Scope}, TargetId={TargetId}, Index={Index}, Outcome={Outcome}",
                scope,
                targetId,
                questionIndex,
                outcome);

            throw new InvalidOperationException(
                outcome == UpdateOutcome.NotFound
                    ? "這段對話已經被清空，新的回答沒有寫入。"
                    : "這段對話在產生期間被其他人更動，新的回答沒有寫入。請重新整理後再試。");
        }

        logger.LogInformation(
            "AI chat answer regenerated. Scope={Scope}, TargetId={TargetId}, Index={Index}, AnswerLength={AnswerLength}",
            scope,
            targetId,
            questionIndex,
            answer.Length);

        return new AiChatAnswer(
            answer,
            contextResult.UsedLabels,
            contextResult.TruncatedLabels,
            contextResult.SkippedLabels);
    }

    /// <summary>
    /// 取出 <paramref name="index"/> 之前的訊息。重新產生時用來還原「當時的脈絡」。
    ///
    /// 抽成 internal static 純函式以便單元測試（本專案的既有慣例）。
    /// 這裡不必限制輪數——<see cref="BuildUserPrompt"/> 已經會做 <see cref="TakeRecentHistory"/>。
    /// </summary>
    internal static IReadOnlyList<AiChatMessageItem> TakeHistoryBefore(
        IReadOnlyList<AiChatMessageItem> history,
        int index)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (index <= 0)
        {
            return [];
        }

        return index >= history.Count ? history : [.. history.Take(index)];
    }

    /// <summary>
    /// 建脈絡 → 組提示 → 呼叫模型。<see cref="AskAsync"/> 與 <see cref="RegenerateAsync"/>
    /// 的差別只在餵進來的 <paramref name="history"/> 與事後怎麼落庫，中間這段完全一樣。
    /// </summary>
    private async Task<(string Answer, ChatContextResult Context)> GenerateAnswerAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        string question,
        IReadOnlyList<AiChatMessageItem> history,
        IReadOnlyList<AiChatAttachment> currentAttachments,
        Action<string>? onDelta,
        CancellationToken cancellationToken)
    {
        var provider = ResolveProvider();

        var baseSources = scope == AiChatScope.Project
            ? await BuildProjectSourcesAsync(targetId, cancellationToken)
            : await BuildMeetingSourcesAsync(targetId, cancellationToken);

        // 使用者附上的檔案（這次的＋最近幾輪的）放在最前面：順序就是優先權，
        // 使用者特地附上的東西被截掉，比會議紀錄被截掉更說不過去。
        var attachments = CollectAttachments(history, currentAttachments, MaxHistoryTurns);

        var attachmentSources = attachments
            .Where(x => x.Kind == AiChatAttachmentKind.Document)
            .Select(x => new ChatSource(
                $"使用者附件：{x.FileName}",
                attachmentTextExtractor.TryExtractFromFullPath(
                    chatStore.GetAttachmentFullPath(scope, targetId, conversationId, x.StoredName),
                    x.FileName)));

        var contextResult = ChatContextBuilder.Build(attachmentSources.Concat(baseSources));

        var (images, imageLabels, skippedImages) = await LoadImagesAsync(
            scope, targetId, conversationId, SelectImages(attachments, AiChatAttachmentPolicy.MaxImagesPerRequest), cancellationToken);

        contextResult = contextResult with
        {
            UsedLabels = [.. contextResult.UsedLabels, .. imageLabels],
            SkippedLabels = [.. contextResult.SkippedLabels, .. skippedImages],
        };

        if (!contextResult.HasContent)
        {
            throw new InvalidOperationException(
                scope == AiChatScope.Project
                    ? "這個專案目前沒有可供查詢的資料：尚未產生任何會議紀錄，附件也沒有可擷取的文字。"
                    : "這場會議目前沒有可供查詢的資料：尚未產生會議紀錄，也沒有逐字稿。");
        }

        var userPrompt = BuildUserPrompt(contextResult.Context, history, question, currentAttachments);

        // 記帳放在這裡而不是 AskAsync／RegenerateAsync，是因為這一支是兩條路徑共用的唯一出口。
        // ⚠️ 特別重要的是 RegenerateAsync：它在模型呼叫**成功之後**還可能因為樂觀鎖失敗而拋例外，
        //    那時錢已經花了。帳記在這裡才不會漏掉那一種。
        TextGenerationResult generated;
        try
        {
            generated = await provider.GenerateAsync(SystemPrompt, userPrompt, onDelta, cancellationToken, images);
        }
        catch (Exception ex)
        {
            await RecordUsageAsync(
                provider,
                scope,
                targetId,
                ex is OperationCanceledException ? AiUsageOutcome.Cancelled : AiUsageOutcome.Failed,
                tokens: null,
                errorMessage: ex.Message);
            throw;
        }

        await RecordUsageAsync(
            provider, scope, targetId, AiUsageOutcome.Succeeded, generated.Usage, errorMessage: null);

        return (generated.Content, contextResult);
    }

    /// <summary>
    /// 把這次問答寫進用量帳本。
    ///
    /// <para>
    /// ⚠️ 「重新產生答案」也會走到這裡，所以帳本的呼叫次數會**高於**儀表板既有的
    /// 「AI 問答次數」——後者是數對話檔裡的提問行，而重新產生是覆寫原訊息、不增行。
    /// 那個數字一直在低估，帳本不重蹈覆轍。
    /// </para>
    /// </summary>
    private Task RecordUsageAsync(
        ITextGenerationProvider provider,
        AiChatScope scope,
        int targetId,
        AiUsageOutcome outcome,
        AiTokenUsage? tokens,
        string? errorMessage)
    {
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);

        return usageRecorder.RecordAsync(new AiUsageEntry(
            AiUsageFeature.AiChat,
            outcome,
            provider.ProviderName,
            provider.ModelName,
            Tokens: tokens,
            UserId: userId,
            UserName: userName,
            MeetingId: scope == AiChatScope.Meeting ? targetId : null,
            ProjectId: scope == AiChatScope.Project ? targetId : null,
            ErrorMessage: errorMessage));
    }

    /// <summary>
    /// 組出送進模型的使用者訊息：參考資料 → 先前對話 → 這次的問題。
    ///
    /// 抽成 internal static 純函式以便單元測試（本專案的既有慣例）。
    /// </summary>
    internal static string BuildUserPrompt(
        string contextText,
        IReadOnlyList<AiChatMessageItem> history,
        string question,
        IReadOnlyList<AiChatAttachment>? currentAttachments = null)
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
                builder.AppendLine($"{(message.IsUser ? "使用者" : "助理")}：{message.Content}{DescribeAttachments(message.AttachmentList)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine($"問題：{question.Trim()}{DescribeAttachments(currentAttachments ?? [])}");

        return builder.ToString();
    }

    /// <summary>
    /// 在提問後面標註附了哪些檔案，讓模型知道「這張圖」「這份文件」指的是哪一個。
    /// 文件內容在參考資料裡（標題是「使用者附件：檔名」），圖片隨訊息附上。
    /// </summary>
    private static string DescribeAttachments(IReadOnlyList<AiChatAttachment> attachments)
        => attachments.Count == 0
            ? string.Empty
            : $"（附件：{string.Join("、", attachments.Select(x => x.Kind == AiChatAttachmentKind.Image ? $"圖片 {x.FileName}" : x.FileName))}）";

    /// <summary>
    /// 這次要一起送的附件：這次的排最前面，再來是最近 <paramref name="maxTurns"/> 輪提問的附件（新的在前）。
    /// 視窗與文字歷史相同——追問「那張圖第二行是什麼」時模型才看得到那張圖，
    /// 但更早的附件不再重送，每次追問的費用才不會無限累積。
    /// </summary>
    internal static IReadOnlyList<AiChatAttachment> CollectAttachments(
        IReadOnlyList<AiChatMessageItem> history,
        IReadOnlyList<AiChatAttachment> currentAttachments,
        int maxTurns)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(currentAttachments);

        var recentFromHistory = TakeRecentHistory(history, maxTurns)
            .Where(x => x.IsUser)
            .Reverse()
            .SelectMany(x => x.AttachmentList);

        return [.. currentAttachments, .. recentFromHistory];
    }

    /// <summary>挑出要送的圖片：依 <see cref="CollectAttachments"/> 的順序（新的在前）取前 <paramref name="max"/> 張。</summary>
    internal static IReadOnlyList<AiChatAttachment> SelectImages(IReadOnlyList<AiChatAttachment> attachments, int max)
        => [.. attachments.Where(x => x.Kind == AiChatAttachmentKind.Image).Take(max)];

    /// <summary>服務層再擋一次：畫面擋得住挑選與貼上，但擋不住直接呼叫這支服務的程式碼。</summary>
    private static void ValidateAttachments(IReadOnlyList<PendingAttachment> attachments)
    {
        if (attachments.Count > AiChatAttachmentPolicy.MaxAttachmentsPerQuestion)
        {
            throw new InvalidOperationException($"一次最多附上 {AiChatAttachmentPolicy.MaxAttachmentsPerQuestion} 個檔案。");
        }

        foreach (var attachment in attachments)
        {
            if (AiChatAttachmentPolicy.Validate(attachment.FileName, attachment.Content.LongLength) is { } error)
            {
                throw new InvalidOperationException(error);
            }
        }
    }

    /// <summary>讀出要送的圖片。檔案不見（被手動刪掉）時列進「無法讀取」，不讓整個提問失敗。</summary>
    private async Task<(List<PromptImage> Images, List<string> UsedLabels, List<string> SkippedLabels)> LoadImagesAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        IReadOnlyList<AiChatAttachment> images,
        CancellationToken cancellationToken)
    {
        var loaded = new List<PromptImage>();
        var used = new List<string>();
        var skipped = new List<string>();

        foreach (var image in images)
        {
            var label = $"使用者圖片：{image.FileName}";
            var fullPath = chatStore.GetAttachmentFullPath(scope, targetId, conversationId, image.StoredName);
            var mediaType = AiChatAttachmentPolicy.GetImageMediaType(image.FileName);

            if (mediaType is null || !File.Exists(fullPath))
            {
                skipped.Add(label);
                continue;
            }

            loaded.Add(new PromptImage(mediaType, await File.ReadAllBytesAsync(fullPath, cancellationToken)));
            used.Add(label);
        }

        return (loaded, used, skipped);
    }

    /// <summary>讀出一個附件的內容（畫面預覽圖片、下載文件用）。找不到時回傳 null。</summary>
    public async Task<byte[]?> ReadAttachmentAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        AiChatAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        var fullPath = chatStore.GetAttachmentFullPath(scope, targetId, conversationId, attachment.StoredName);
        return File.Exists(fullPath) ? await File.ReadAllBytesAsync(fullPath, cancellationToken) : null;
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

    /// <summary>專案層級的脈絡來源：先放各份會議紀錄（短且已整理過），再放附件全文。</summary>
    private async Task<List<ChatSource>> BuildProjectSourcesAsync(int projectId, CancellationToken cancellationToken)
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

        return sources;
    }

    /// <summary>單一會議的脈絡來源：會議紀錄優先（摘要），再放逐字稿（長，可能被截）。</summary>
    private async Task<List<ChatSource>> BuildMeetingSourcesAsync(int meetingId, CancellationToken cancellationToken)
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

        return sources;
    }

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
