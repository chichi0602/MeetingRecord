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
using MeetingRecord.Share.Helpers;

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
    private readonly AiCallGateway gateway;
    private readonly ILogger<AiChatService> logger;
    private readonly ProjectAccessService projectAccess;
    private readonly IAuditLogService auditLogService;

    public AiChatService(
        BackendDBContext context,
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        MeetingFileStore fileStore,
        AiChatStore chatStore,
        AttachmentTextExtractor attachmentTextExtractor,
        CurrentUserService currentUserService,
        IOptions<LlmSettings> llmSettings,
        AiCallGateway gateway,
        ILogger<AiChatService> logger,
        ProjectAccessService projectAccess,
        IAuditLogService auditLogService)
    {
        this.context = context;
        this.textGenerationProviders = textGenerationProviders;
        this.fileStore = fileStore;
        this.chatStore = chatStore;
        this.attachmentTextExtractor = attachmentTextExtractor;
        this.currentUserService = currentUserService;
        this.llmSettings = llmSettings;
        this.gateway = gateway;
        this.logger = logger;
        this.projectAccess = projectAccess;
        this.auditLogService = auditLogService;
    }

    /// <summary>
    /// 對話範圍的權限（看專案的主責與協作團隊）：專案問答要看得到那個專案，會議問答要看得到那場會議。
    /// **每個公開方法第一行都要呼叫**——對話檔與附件在檔案系統上，路徑由範圍＋對象 Id 直接算出來，
    /// 沒擋的話知道 Id 就讀得到別人的對話。看不到時當成不存在。
    /// </summary>
    /// <param name="auditOperation">寫入類操作傳操作名稱（1.0.34）：被資料範圍擋下時記一筆權限拒絕；讀取類不傳、不記。</param>
    private async Task EnsureAccessAsync(AiChatScope scope, int targetId, CancellationToken cancellationToken, string? auditOperation = null)
    {
        var access = await projectAccess.GetAsync();
        if (access.IsAdmin)
        {
            return;
        }

        bool allowed;
        if (scope == AiChatScope.Project)
        {
            allowed = access.CanViewProject(targetId);
        }
        else
        {
            var meeting = await context.Meeting.AsNoTracking()
                .Where(x => x.Id == targetId)
                .Select(x => new { x.ProjectId, x.CreatedByUserId })
                .FirstOrDefaultAsync(cancellationToken);
            allowed = meeting is not null && access.CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId);
        }

        if (!allowed)
        {
            logger.LogWarning("AI chat access denied. Scope={Scope}, TargetId={TargetId}", scope, targetId);
            if (auditOperation is not null)
            {
                await auditLogService.WriteForCurrentUserAsync(
                    AuditActions.PermissionDenied, success: false, targetType: scope.ToString(), targetId: targetId.ToString(),
                    detail: $"reason=DataScope; op=AiChat{auditOperation}");
            }

            throw new InvalidOperationException("找不到這個專案或會議，可能已被刪除或沒有權限。");
        }
    }

    /// <summary>
    /// 列出這個對象底下的所有對話，最近更新的排最前面。
    /// 會順帶把 0.4.79 之前的單檔對話搬進新結構。
    /// </summary>
    public async Task<IReadOnlyList<AiChatConversationInfo>> ListConversationsAsync(
        AiChatScope scope,
        int targetId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(scope, targetId, cancellationToken);
        return await chatStore.ListConversationsAsync(scope, targetId, cancellationToken);
    }

    /// <summary>開一段新對話，回傳它的 Id。建立者記的是現在這個人。</summary>
    /// <param name="audit">
    /// 寫不寫稽核（1.0.34）。開視窗時對象底下還沒有對話會自動建一段，那不算使用者的動作，傳 false。
    /// </param>
    public async Task<string> CreateConversationAsync(
        AiChatScope scope,
        int targetId,
        CancellationToken cancellationToken = default,
        bool audit = true)
    {
        await EnsureAccessAsync(scope, targetId, cancellationToken, audit ? "Create" : null);
        var conversationId = await chatStore.CreateConversationAsync(scope, targetId, ResolveCurrentUserName(), cancellationToken);
        if (audit)
        {
            await AuditAsync(AuditActions.AiChatCreate, scope, targetId, $"conversationId={conversationId}");
        }

        return conversationId;
    }

    /// <summary>
    /// 改這段對話的名字。<b>不呼叫模型，不會產生費用。</b>
    /// 沒改過名的對話標題會自動取第一句提問，改過之後就固定用改的。
    /// </summary>
    public async Task<UpdateOutcome> RenameConversationAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(scope, targetId, cancellationToken, "Rename");
        var outcome = await chatStore.RenameConversationAsync(scope, targetId, conversationId, title, cancellationToken);
        if (outcome == UpdateOutcome.Updated)
        {
            await AuditAsync(AuditActions.AiChatRename, scope, targetId, $"conversationId={conversationId}; title={title.Trim()}");
        }

        return outcome;
    }

    /// <summary>取出這段對話的完整歷史（依時間由舊到新）。</summary>
    public async Task<List<AiChatMessageItem>> GetHistoryAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(scope, targetId, cancellationToken);
        return await chatStore.ReadHistoryAsync(scope, targetId, conversationId, cancellationToken);
    }

    /// <summary>刪掉這一段對話（同一個對象底下的其他段不受影響）。</summary>
    public async Task ClearHistoryAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessAsync(scope, targetId, cancellationToken, "ClearHistory");
        chatStore.TryDeleteConversation(scope, targetId, conversationId);
        await AuditAsync(AuditActions.AiChatClearHistory, scope, targetId, $"conversationId={conversationId}");
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
        await EnsureAccessAsync(scope, targetId, cancellationToken);

        var pending = attachments ?? [];
        ValidateAttachments(pending);

        // 對話已被別人刪掉就不要再花錢產生答案（0.4.115）。寫入時在鎖內還會再確認一次。
        if (!chatStore.ConversationExists(scope, targetId, conversationId))
        {
            throw new InvalidOperationException("這段對話已經被刪除，請開新對話再提問。");
        }

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
                scope, targetId, conversationId, question, ResolveCurrentUserName(), answer, cancellationToken, saved, mustExist: true);
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
    public async Task<UpdateOutcome> UpdateMessageAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        int index,
        AiChatMessageItem original,
        string newContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        await EnsureAccessAsync(scope, targetId, cancellationToken, "MessageUpdate");

        var outcome = await chatStore.UpdateMessagesAsync(
            scope,
            targetId,
            conversationId,
            [new MessageEdit(index, original.Role, original.Content, newContent)],
            cancellationToken);
        if (outcome == UpdateOutcome.Updated)
        {
            // 只記位置與字數，不記內容。
            await AuditAsync(
                AuditActions.AiChatMessageUpdate, scope, targetId,
                $"conversationId={conversationId}; index={index}; role={original.Role}; chars={newContent.Length}");
        }

        return outcome;
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
        await EnsureAccessAsync(scope, targetId, cancellationToken, "Regenerate");

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

        await AuditAsync(
            AuditActions.AiChatRegenerate, scope, targetId,
            $"conversationId={conversationId}; index={questionIndex}; answerChars={answer.Length}");

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

    /// <summary>稽核（1.0.34）：目標是對話所屬的專案或會議；不記任何對話內容。</summary>
    private Task AuditAsync(string action, AiChatScope scope, int targetId, string detail)
        => auditLogService.WriteForCurrentUserAsync(action, success: true, targetType: scope.ToString(), targetId: targetId.ToString(), detail: detail);

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
        // ⚠️ 「重新產生答案」也會走到這裡，所以帳本的呼叫次數會**高於**儀表板的「AI 問答次數」——
        //    後者是數對話檔裡的提問行，而重新產生是覆寫原訊息、不增行。
        // 1.0.29：記帳、AI 對話紀錄、用量上限都由 gateway 處理；已達上限時拋 AiQuotaExceededException。
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);
        var generated = await gateway.GenerateAsync(
            provider,
            new AiCallContext(
                AiUsageFeature.AiChat,
                userId,
                userName,
                MeetingId: scope == AiChatScope.Meeting ? targetId : null,
                ProjectId: scope == AiChatScope.Project ? targetId : null,
                RelatedInfo: BuildRelatedInfo(scope, targetId, question),
                ThreadKey: $"chat:{scope}:{targetId}:{conversationId}"),
            SystemPrompt,
            userPrompt,
            onDelta,
            cancellationToken,
            images);

        return (generated.Content, contextResult);
    }

    /// <summary>AI 對話紀錄清單上的關聯說明：對象＋問題開頭。</summary>
    internal static string BuildRelatedInfo(AiChatScope scope, int targetId, string question)
    {
        const int maxQuestionLength = 60;
        var target = scope == AiChatScope.Project ? $"專案 #{targetId}" : $"會議 #{targetId}";
        var trimmed = question.ReplaceLineEndings(" ").Trim();
        if (trimmed.Length > maxQuestionLength)
        {
            trimmed = trimmed[..maxQuestionLength] + "…";
        }

        return $"{target} AI 問答：{trimmed}";
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
        // 讀附件前也要檢查權限（0.4.114）：開著的視窗在權限被收回後仍讀得到。
        // 這行原本誤放在 LoadImagesAsync 的迴圈裡（那條路徑在提問時已經檢查過），每張圖都多查一次資料庫。
        await EnsureAccessAsync(scope, targetId, cancellationToken);
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
