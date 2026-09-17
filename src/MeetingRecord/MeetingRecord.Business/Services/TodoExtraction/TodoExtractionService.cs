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
    private readonly AiUsageRecorder usageRecorder;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<TodoExtractionService> logger;

    public TodoExtractionService(
        BackendDBContext context,
        IEnumerable<ITextGenerationProvider> textGenerationProviders,
        IOptions<LlmSettings> llmSettings,
        AiUsageRecorder usageRecorder,
        CurrentUserService currentUserService,
        ILogger<TodoExtractionService> logger)
    {
        this.context = context;
        this.textGenerationProviders = textGenerationProviders;
        this.llmSettings = llmSettings;
        this.usageRecorder = usageRecorder;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>
    /// 抽出待辦候選。抽不到東西時回空清單（不是錯誤）；
    /// 會議不存在或還沒有會議紀錄才拋例外。
    /// </summary>
    public async Task<IReadOnlyList<ExtractedTodo>> ExtractAsync(
        int meetingId,
        CancellationToken cancellationToken = default)
    {
        var provider = ResolveProvider();

        // 從資料庫讀，不信任 UI 傳來的複本——畫面上那份可能已經是別人改過之前的舊資料。
        var meeting = await context.Meeting.AsNoTracking()
            .Where(x => x.Id == meetingId)
            // ProjectId 是給用量帳本用的：要能回答「哪個專案最燒錢」。
            .Select(x => new { x.Title, x.MeetingDate, x.DraftContent, x.ProjectId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"找不到 Id 為 {meetingId} 的會議紀錄，可能已被刪除。");

        if (string.IsNullOrWhiteSpace(meeting.DraftContent))
        {
            throw new InvalidOperationException("這場會議還沒有產生會議紀錄，沒有可以抽取待辦的內容。");
        }

        var userPrompt = BuildUserPrompt(meeting.DraftContent, meeting.MeetingDate);

        // onDelta 傳 null：輸出是 JSON，逐字顯示對使用者沒有意義。
        TextGenerationResult generated;
        try
        {
            generated = await provider.GenerateAsync(SystemPrompt, userPrompt, null, cancellationToken);
        }
        catch (Exception ex)
        {
            // 先記帳再往外拋：串流跑到一半斷掉時，已經生成的 token 供應商照算。
            await RecordUsageAsync(
                provider,
                meetingId,
                meeting.ProjectId,
                ex is OperationCanceledException ? AiUsageOutcome.Cancelled : AiUsageOutcome.Failed,
                tokens: null,
                errorMessage: ex.Message);
            throw;
        }

        await RecordUsageAsync(
            provider, meetingId, meeting.ProjectId, AiUsageOutcome.Succeeded, generated.Usage, errorMessage: null);

        var results = TodoExtractionParser.Parse(generated.Content);

        logger.LogInformation(
            "Todo extraction completed. MeetingId={MeetingId}, DraftLength={DraftLength}, ExtractedCount={ExtractedCount}",
            meetingId,
            meeting.DraftContent.Length,
            results.Count);

        return results;
    }

    /// <summary>把這次呼叫寫進用量帳本。這條路徑走在使用者的請求裡，所以拿得到 CurrentUserService。</summary>
    private Task RecordUsageAsync(
        ITextGenerationProvider provider,
        int meetingId,
        int? projectId,
        AiUsageOutcome outcome,
        AiTokenUsage? tokens,
        string? errorMessage)
    {
        var (userId, userName) = AiUsageAttribution.Resolve(currentUserService.CurrentUser);

        return usageRecorder.RecordAsync(new AiUsageEntry(
            AiUsageFeature.TodoExtraction,
            outcome,
            provider.ProviderName,
            provider.ModelName,
            Tokens: tokens,
            UserId: userId,
            UserName: userName,
            MeetingId: meetingId,
            ProjectId: projectId,
            ErrorMessage: errorMessage));
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
        var titles = await context.Todo.AsNoTracking()
            .Where(x => x.MeetingId == meetingId)
            .Select(x => x.Title)
            .ToListAsync(cancellationToken);

        // Trim 是為了與寫入端一致——候選存進去時是 candidate.Title.Trim()。
        return titles
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 組出送進模型的使用者訊息。抽成 internal static 純函式以便單元測試（本專案既有慣例）。
    ///
    /// <para>
    /// **會議日期會放進提示詞當作相對日期的基準**，並明確要求只輸出 yyyy-MM-dd 或 null——
    /// 讓模型自己算「下週五」比讓它留白更容易錯，而解析端也刻意只收絕對日期。
    /// </para>
    /// </summary>
    internal static string BuildUserPrompt(string draftContent, DateTime? meetingDate)
    {
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
