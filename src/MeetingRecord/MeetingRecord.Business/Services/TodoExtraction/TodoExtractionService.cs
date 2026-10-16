using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.AccessDatas;
using MeetingRecord.Business.Services.AiUsage;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Business.Services.TodoExtraction;

/// <summary>
/// 用 LLM 從一份會議紀錄草稿抽出待辦候選。
///
/// <para>
/// 形狀比照 <c>AiChatService</c>——同樣是「從 UI 同步呼叫 LLM 並等結果」，
/// 不是背景工作，所以不走佇列。複用既有的 <see cref="ITextGenerationProvider"/>，
/// 不另接一條到 Azure OpenAI 的路徑。
/// </para>
///
/// <para>
/// **只讀 <c>Meeting.DraftContent</c>，不讀逐字稿。** 草稿已經是 AI 整理過的結構化內容，
/// 待辦事項通常已經被列出來，抽取準確度高、長度短、便宜；逐字稿雜訊多，
/// 會抽出一堆「其實只是閒聊」的東西讓使用者逐條去勾掉。
/// </para>
/// </summary>
public class TodoExtractionService
{
    private const string SystemPrompt =
        "你是會議紀錄的待辦事項擷取助理。請全程使用繁體中文（台灣用語），不要使用簡體字或中國大陸用語。" +
        "你的任務是從會議紀錄中找出**明確的行動項目**——有人要去做某件事。" +
        "純粹的討論、背景說明、已完成的事項都不是待辦，不要列進去。" +
        "找不到任何明確的行動項目時，回傳空陣列 []，**不要為了湊數而杜撰**。";

    private readonly BackendDBContext context;
    private readonly IEnumerable<ITextGenerationProvider> textGenerationProviders;
    private readonly IOptions<LlmSettings> llmSettings;
    private readonly AiCallGateway gateway;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<TodoExtractionService> logger;
    private readonly ProjectAccessService projectAccess;

    public TodoExtractionService(
        BackendDBContext context,
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        IOptions<LlmSettings> llmSettings,
        AiCallGateway gateway,
        CurrentUserService currentUserService,
        ILogger<TodoExtractionService> logger,
        ProjectAccessService projectAccess)
    {
        this.context = context;
        this.textGenerationProviders = textGenerationProviders;
        this.llmSettings = llmSettings;
        this.gateway = gateway;
        this.currentUserService = currentUserService;
        this.logger = logger;
        this.projectAccess = projectAccess;
    }

    /// <summary>
    /// 抽出待辦候選。抽不到東西時回空清單（不是錯誤）；
    /// 會議不存在或還沒有會議紀錄才拋例外。
    /// </summary>
    /// <param name="openTodos">
    /// 1.0.44：本專案未完成的待辦（<see cref="GetOpenTodosAsync"/>），送給模型比對「是不是同一件事」。
    /// 畫面先查好傳進來，候選的「對應既有」才和畫面上顯示的是同一份；傳 null 時這裡自己查。
    /// </param>
    public async Task<IReadOnlyList<ExtractedTodo>> ExtractAsync(
        int meetingId,
        CancellationToken cancellationToken = default,
        IReadOnlyList<OpenTodoSummary>? openTodos = null)
    {
        var provider = ResolveProvider();

        // 從資料庫讀，不信任 UI 傳來的複本——畫面上那份可能已經是別人改過之前的舊資料。
        var meeting = await context.Meeting.AsNoTracking()
            .Where(x => x.Id == meetingId)
            // ProjectId 是給用量帳本用的：要能回答「哪個專案最燒錢」。
            .Select(x => new { x.Title, x.MeetingDate, x.DraftContent, x.ProjectId, x.CreatedByUserId })
            .FirstOrDefaultAsync(cancellationToken);

        // 看不到的會議當成不存在（0.4.99）：這支會花錢呼叫 AI，也會把會議內容送出去。
        if (meeting is null || !(await projectAccess.GetAsync()).CanViewMeeting(meeting.ProjectId, meeting.CreatedByUserId))
        {
            throw new InvalidOperationException($"找不到 Id 為 {meetingId} 的會議紀錄，可能已被刪除。");
        }

        if (string.IsNullOrWhiteSpace(meeting.DraftContent))
        {
            throw new InvalidOperationException("這場會議還沒有產生會議紀錄，沒有可以抽取待辦的內容。");
        }

        openTodos ??= await GetOpenTodosAsync(meetingId, cancellationToken);
        var userPrompt = BuildUserPrompt(meeting.DraftContent, meeting.MeetingDate, openTodos);

        // onDelta 傳 null：輸出是 JSON，逐字顯示對使用者沒有意義。
        // 1.0.29：記帳、AI 對話紀錄、用量上限都由 gateway 處理（這條路徑走在使用者的請求裡，拿得到 CurrentUserService）。
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);
        var generated = await gateway.GenerateAsync(
            provider,
            new AiCallContext(
                AiUsageFeature.TodoExtraction,
                userId,
                userName,
                meetingId,
                meeting.ProjectId,
                RelatedInfo: $"會議 #{meetingId}〈{meeting.Title}〉抽出待辦"),
            SystemPrompt,
            userPrompt,
            null,
            cancellationToken);

        var results = TodoExtractionParser.Parse(
            generated.Content,
            openTodos.Select(x => x.Id).ToHashSet(),
            openTodos.Where(x => !x.IsSubtask).Select(x => x.Id).ToHashSet());

        logger.LogInformation(
            "Todo extraction completed. MeetingId={MeetingId}, DraftLength={DraftLength}, ExtractedCount={ExtractedCount}",
            meetingId,
            meeting.DraftContent.Length,
            results.Count);

        return results;
    }

    /// <summary>
    /// 這場會議先前已經加入過的待辦標題（已 Trim，比對忽略大小寫）。
    ///
    /// <para>
    /// 用來在候選清單上標示「已加入過」並預設不勾選。⚠️ <b>那只是預設值，不是禁止</b>——
    /// 使用者仍可刻意勾回去（開完後續會議再追蹤同一件事是合理的），
    /// 所以服務層<b>不會</b>擋重複的寫入，去重只做在畫面上。
    /// </para>
    ///
    /// <para>
    /// 回傳集合而不是筆數：畫面同時需要「先前共幾則」與「哪幾則和這次重複」，
    /// 筆數由集合長度得出，不必為了標題再查一次。
    /// 比對用 <see cref="StringComparer.OrdinalIgnoreCase"/>，與 <c>PromptTemplateService</c>
    /// 建立內建範本時比對既有名稱的做法一致（在記憶體比，不必在意 SQLite 的定序差異）。
    /// </para>
    /// </summary>
    public async Task<IReadOnlySet<string>> GetExistingTitlesAsync(
        int meetingId,
        CancellationToken cancellationToken = default)
    {
        var titles = await (await projectAccess.GetAsync()).Filter(context.Todo.AsNoTracking())
            .Where(x => x.MeetingId == meetingId)
            .Select(x => x.Title)
            .ToListAsync(cancellationToken);

        // Trim 是為了與寫入端一致——候選存進去時是 candidate.Title.Trim()。
        return titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>送給模型比對的既有待辦最多幾筆（依最近更新排序）。太多會讓提示詞變長、比對也變差。</summary>
    public const int MaxOpenTodosForMatching = 100;

    /// <summary>
    /// 這場會議所屬專案的未完成待辦（1.0.44），給模型比對「這次抽到的是不是既有的那件事」。
    ///
    /// <para>
    /// 使用者問「兩場會議抽出同一件待辦、期限改了，會變兩筆還是只改時間」：以前只比同一場會議、標題完全相同，
    /// AI 每次措辭都不一樣，跨會議一定變兩筆。改成讓 AI 對照既有待辦判斷，畫面上預設「更新既有」。
    /// 未歸屬專案的會議（不會走到抽出待辦）或看不到的會議回空清單。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<OpenTodoSummary>> GetOpenTodosAsync(
        int meetingId,
        CancellationToken cancellationToken = default)
    {
        var projectId = await context.Meeting.AsNoTracking()
            .Where(x => x.Id == meetingId)
            .Select(x => x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        if (projectId is null)
        {
            return [];
        }

        var access = await projectAccess.GetAsync();
        if (!access.CanViewProject(projectId.Value))
        {
            return [];
        }

        return await access.Filter(context.Todo.AsNoTracking())
            .Where(x => x.ProjectId == projectId.Value && x.Status != TodoAdapterModel.CompletedStatus)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(MaxOpenTodosForMatching)
            .Select(x => new OpenTodoSummary(
                x.Id,
                x.Title,
                x.Owner,
                x.DueDate,
                x.Status,
                x.Meeting != null ? x.Meeting.Title : null,
                x.ParentTodoId != null))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 組出送進模型的使用者訊息。抽成 internal static 純函式以便單元測試（本專案既有慣例）。
    ///
    /// <para>
    /// **會議日期會放進提示詞當作相對日期的基準**，並明確要求只輸出 yyyy-MM-dd 或 null——
    /// 讓模型自己算「下週五」比讓它留白更容易錯，而解析端也刻意只收絕對日期。
    /// </para>
    /// </summary>
    internal static string BuildUserPrompt(
        string draftContent,
        DateTime? meetingDate,
        IReadOnlyList<OpenTodoSummary>? openTodos = null)
    {
        var hasOpenTodos = openTodos is { Count: > 0 };
        var builder = new StringBuilder();

        builder.AppendLine("請從以下會議紀錄中擷取待辦事項。");
        builder.AppendLine();

        builder.AppendLine("輸出格式：**只回傳一個 JSON 陣列，不要有任何說明文字或程式碼圍籬**。");
        builder.AppendLine("每個元素的欄位如下：");
        builder.AppendLine("""
            [
              {
                "title": "必填，一句話講清楚要做什麼，20 字以內",
                "description": "選填，補充細節；沒有就給 null",
                "owner": "選填，負責人姓名；會議紀錄沒指名就給 null，不要猜",
                "dueDate": "選填，格式必須是 yyyy-MM-dd；沒有明確日期就給 null",
                "priority": "必填，只能是「低」「中」「高」三者之一"
              }
            ]
            """);
        builder.AppendLine();

        if (hasOpenTodos)
        {
            // 1.0.44：讓模型判斷「這條是不是既有的那件事」，畫面才能預設更新既有、不重複新增。
            builder.AppendLine("每個元素另外加一個欄位 \"matchedTodoId\"：");
            builder.AppendLine(
                "若這條待辦和下方「本專案目前未完成的待辦」其中一筆是**同一件事**（即使措辭不同、或期限與負責人改了），" +
                "填那一筆的 id（數字）；是新的事情就給 null；**不確定就給 null，不要硬配**。" +
                "同一筆既有待辦最多只對應一條。對應到既有待辦時，owner 與 dueDate 請填**這場會議講的最新值**，這場會議沒提到就給 null。");
            builder.AppendLine();
            builder.AppendLine("再加一個欄位 \"parentTodoId\"：");
            builder.AppendLine(
                "若這條待辦是下方某一筆既有待辦的**一部分或子步驟**（例如「畫統計圖」屬於「完成報表」），填那一筆的 id；" +
                "只能填標示為「主項目」的那些（子項目底下不能再掛）。與 matchedTodoId 擇一：是同一件事就填 matchedTodoId、不要填 parentTodoId。" +
                "**不確定就給 null**。");
            builder.AppendLine();
            builder.AppendLine("本專案目前未完成的待辦：");
            foreach (var todo in openTodos!)
            {
                builder.AppendLine(
                    $"- id={todo.Id}｜{todo.Title}｜負責人：{(string.IsNullOrWhiteSpace(todo.Owner) ? "未指定" : todo.Owner)}" +
                    $"｜期限：{todo.DueDate?.ToString("yyyy-MM-dd") ?? "未設定"}｜狀態：{todo.Status}｜{(todo.IsSubtask ? "子項目" : "主項目")}");
            }

            builder.AppendLine();
        }

        if (meetingDate.HasValue)
        {
            builder.AppendLine($"這場會議的日期是 {meetingDate.Value:yyyy-MM-dd}。");
            builder.AppendLine(
                "若紀錄裡寫的是「下週五」「三天內」這類相對日期，請以會議日期為基準換算成 yyyy-MM-dd；" +
                "**換算不出確定日期時給 null，不要猜**。");
        }
        else
        {
            builder.AppendLine(
                "這場會議沒有記錄日期，所以「下週五」這類相對日期無法換算，一律給 null。");
        }

        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine("會議紀錄：");
        builder.AppendLine();
        builder.AppendLine(draftContent);

        return builder.ToString();
    }

    /// <summary>依 <c>LlmSettings.DefaultProvider</c> 挑出供應商，形狀比照 <c>AiChatService</c>。</summary>
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

    /// <summary>把候選轉成可直接送進 <c>TodoService.AddAsync</c> 的模型。</summary>
    public static TodoAdapterModel ToAdapterModel(ExtractedTodo source, int projectId, int meetingId)
        => new()
        {
            Title = source.Title,
            Description = source.Description,
            Owner = source.Owner,
            DueDate = source.DueDate,
            Priority = source.Priority,
            // 狀態不讓 AI 決定：剛抽出來的東西不可能已完成。
            Status = TodoAdapterModel.StatusOptions[0],
            ProjectId = projectId,
            MeetingId = meetingId,
        };
}
