using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiChat;

/// <summary>
/// 一筆就地修改。<see cref="Index"/> 是「第幾則有效訊息」（與
/// <see cref="AiChatStore.ReadHistoryAsync"/> 回傳清單的索引一致），不是檔案行號。
/// </summary>
/// <param name="Index">要改的是第幾則訊息。</param>
/// <param name="ExpectedRole">預期的角色，對不上就視為衝突——重新產生時的「提問的下一則」不保證真的是回答。</param>
/// <param name="ExpectedContent">預期的原內容，對不上代表別人已經改過了。</param>
/// <param name="NewContent">要寫進去的新內容。</param>
/// <param name="NewAskedBy">要改寫的提問者；傳 null 表示維持原值。</param>
public readonly record struct MessageEdit(
    int Index,
    string ExpectedRole,
    string ExpectedContent,
    string NewContent,
    string? NewAskedBy = null);

/// <summary>
/// 對話清單上的一列。
/// </summary>
/// <param name="Id">對話 Id，同時也是檔名（不含副檔名）。</param>
/// <param name="Title">
/// 顯示標題。使用者改過名就用改過的；沒改過則取第一句提問（見
/// <see cref="AiChatStore.ListConversationsAsync"/>）。
/// </param>
/// <param name="CreatedBy">開啟這段對話的人。對話是共用的，清單上要看得出來是誰開的。</param>
/// <param name="CreatedAt">建立時間。</param>
/// <param name="UpdatedAt">最後一則訊息的時間；沒有訊息時等於建立時間。</param>
/// <param name="MessageCount">有效訊息則數（不含 meta 行）。</param>
public sealed record AiChatConversationInfo(
    string Id,
    string Title,
    string? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int MessageCount);

/// <summary>修改的結果。</summary>
public enum UpdateOutcome
{
    /// <summary>已寫入。</summary>
    Updated,

    /// <summary>索引越界，或角色／內容與預期不符（多半是別人先改過或清空了）。</summary>
    Conflict,

    /// <summary>整段對話已經不存在。</summary>
    NotFound,
}

/// <summary>
/// AI 問答對話的實體檔案存取。
///
/// <para>
/// 0.4.60 之前對話存在 <c>AiChatMessage</c> 資料表，每一則提問與回答的完整文字都進資料庫。
/// 那些文字從來不會被查詢或索引，只會「整段對話一次讀出來顯示」，放資料庫只是讓它膨脹。
/// </para>
///
/// <para>
/// **一段對話一個檔**，路徑由「範圍＋對象 Id＋對話 Id」直接算出來
/// （<c>project/3/&lt;對話 Id&gt;.jsonl</c>、<c>meeting/12/&lt;對話 Id&gt;.jsonl</c>），
/// 所以資料庫連相對路徑都不必存——這也是這一版能把整張資料表移除的原因。
/// 0.4.79 起一個對象底下可以有多段對話，**清單也直接從檔案系統列出來，不引入資料表**。
/// 根目錄取自 <see cref="SystemSettings.ExternalFileSystem"/>，
/// 比照 <c>MeetingFileStore</c>，禁止直接讀 IConfiguration。
/// </para>
///
/// <para>
/// 格式是 **JSONL（一行一則訊息）**而不是單一 JSON 陣列：新增一輪對話只要 append 兩行，
/// 不必把整個檔案讀出來、反序列化、再整個寫回去。
/// </para>
///
/// <para>
/// 註冊為 **Singleton**：它沒有 DbContext 相依，而且要持有寫入鎖——同一段對話可能有
/// 兩個人同時提問，併發 append 會讓兩輪對話的行交錯。
/// </para>
/// </summary>
public class AiChatStore
{
    /// <summary>
    /// 磁碟格式。刻意與畫面用的 <see cref="AiChatMessageItem"/> 分開：
    /// UI 的 DTO 日後若改欄位，不該讓既有的檔案讀不出來。
    /// </summary>
    private sealed record StoredMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("askedBy")] string? AskedBy,
        [property: JsonPropertyName("createdAt")] DateTime CreatedAt);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // 中文若被逃脫成 \uXXXX，使用者用記事本打開會完全看不懂。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>與逐字稿一致：使用者可能直接用記事本開來看，沒有 BOM 會是亂碼。</summary>
    private static readonly UTF8Encoding FileEncoding = new(encoderShouldEmitUTF8Identifier: true);

    private readonly string rootPath;
    private readonly ILogger<AiChatStore> logger;

    /// <summary>依檔案路徑序列化寫入。Singleton 才留得住，這也是這個類別不能是 Scoped 的原因。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> writeLocks = new(StringComparer.OrdinalIgnoreCase);

    public AiChatStore(IOptions<SystemSettings> systemSettings, ILogger<AiChatStore> logger)
    {
        rootPath = systemSettings.Value.ExternalFileSystem.AiChatPath;
        this.logger = logger;
    }

    /// <summary>
    /// 一個對象（專案或會議）底下所有對話的資料夾。
    ///
    /// <para>
    /// 0.4.79 之前一個對象只有一段對話（<c>project/3.jsonl</c>），現在是
    /// <c>project/3/&lt;對話 Id&gt;.jsonl</c>。**「一段對話一個檔」這個基本單位沒有變**，
    /// 只是一個對象底下可以有多個；路徑依然由「範圍＋對象 Id」直接算出來，
    /// 資料庫仍然連相對路徑都不必存。
    /// </para>
    /// </summary>
    public string GetConversationDirectory(AiChatScope scope, int targetId)
    {
        var folder = scope == AiChatScope.Project ? "project" : "meeting";
        return Path.Combine(rootPath, folder, targetId.ToString());
    }

    /// <summary>某段對話的檔案完整路徑。</summary>
    public string GetFullPath(AiChatScope scope, int targetId, string conversationId)
        => Path.Combine(GetConversationDirectory(scope, targetId), $"{SanitizeConversationId(conversationId)}.jsonl");

    /// <summary>
    /// 0.4.79 之前的單檔路徑。只有轉檔用得到。
    /// </summary>
    private string GetLegacyPath(AiChatScope scope, int targetId)
    {
        var folder = scope == AiChatScope.Project ? "project" : "meeting";
        return Path.Combine(rootPath, folder, $"{targetId}.jsonl");
    }

    /// <summary>
    /// 產生新的對話 Id。
    ///
    /// <para>
    /// 前段是可排序的時間戳，**檔名本身就帶時間**——列清單時不必把每個檔案打開就能排序。
    /// 後段補一段隨機碼，避免同一毫秒建立兩段對話時撞名。
    /// </para>
    /// </summary>
    private static string NewConversationId()
        => $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..21];

    /// <summary>
    /// ⚠️ 對話 Id 會直接變成檔名，一定要擋掉路徑跳脫（<c>..</c>、分隔符號）。
    /// 它雖然只由本類別產生，但會經過畫面與網址來回一趟。
    /// </summary>
    private static string SanitizeConversationId(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var cleaned = new string([.. conversationId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')]);

        return cleaned.Length == 0
            ? throw new ArgumentException($"對話 Id 不合法：{conversationId}", nameof(conversationId))
            : cleaned;
    }

    /// <summary>
    /// meta 行的角色。
    ///
    /// <para>
    /// 標題與建立者放在對話檔的**第一行**，不另開一個 meta 檔——那會讓「一段對話」
    /// 變成兩個檔案，與 0.4.60「留一張表只是把一段對話拆成兩個地方維護」的理由自相矛盾。
    /// </para>
    ///
    /// <para>
    /// ⚠️ meta 行<b>不是訊息</b>：它必須同時被 <see cref="ReadHistoryAsync"/> 與
    /// <see cref="MapValidLineIndexes"/> 排除，否則兩邊的「第 n 則」會錯開一位，
    /// 編輯訊息時會改到隔壁那一則。
    /// </para>
    /// </summary>
    private const string MetaRole = "meta";

    /// <summary>自動標題最多取幾個字。太長的話清單會被一句話撐爆。</summary>
    private const int AutoTitleMaxLength = 20;

    /// <summary>
    /// 列出一個對象底下的所有對話，最近更新的排最前面。
    ///
    /// <para>
    /// 會順帶把 0.4.79 之前的單檔對話搬進新結構（見 <see cref="MigrateLegacyIfNeeded"/>），
    /// 所以畫面只要呼叫這一支就好，不必自己判斷要不要轉檔。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AiChatConversationInfo>> ListConversationsAsync(
        AiChatScope scope,
        int targetId,
        CancellationToken cancellationToken = default)
    {
        MigrateLegacyIfNeeded(scope, targetId);

        var directory = GetConversationDirectory(scope, targetId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<AiChatConversationInfo>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            var info = await TryReadConversationInfoAsync(file, cancellationToken);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return [.. result.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 讀出一段對話的清單資訊。
    ///
    /// <para>
    /// 標題的規則：使用者改過名就用改過的；**沒改過就取第一句提問**。
    /// 刻意用「讀的時候算」而不是「第一次發問時寫回檔案」——後者會讓 append
    /// 變成「讀出來、改一行、整檔重寫」，而整檔重寫是這個類別最危險的路徑。
    /// </para>
    /// </summary>
    private async Task<AiChatConversationInfo?> TryReadConversationInfoAsync(
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);

            string? explicitTitle = null;
            string? createdBy = null;
            DateTime? createdAt = null;
            string? firstQuestion = null;
            DateTime? lastAt = null;
            var messageCount = 0;

            foreach (var raw in lines)
            {
                var stored = TryParseStored(raw, logMalformed: false);
                if (stored is null)
                {
                    continue;
                }

                if (string.Equals(stored.Role, MetaRole, StringComparison.Ordinal))
                {
                    explicitTitle = stored.Content;
                    createdBy = stored.AskedBy;
                    createdAt = stored.CreatedAt;
                    continue;
                }

                messageCount++;
                lastAt = stored.CreatedAt;

                if (firstQuestion is null
                    && string.Equals(stored.Role, AiChatService.UserRole, StringComparison.Ordinal))
                {
                    firstQuestion = stored.Content;
                }
            }

            var id = Path.GetFileNameWithoutExtension(fullPath);
            var created = createdAt ?? File.GetCreationTime(fullPath);

            return new AiChatConversationInfo(
                id,
                BuildTitle(explicitTitle, firstQuestion),
                createdBy,
                created,
                lastAt ?? created,
                messageCount);
        }
        catch (IOException ex)
        {
            // 正在被寫入的檔案讀不到，不該讓整份清單開不出來。
            logger.LogWarning(ex, "Failed to read AI chat conversation. FullPath={FullPath}", fullPath);
            return null;
        }
    }

    /// <summary>標題：改過名優先，其次取第一句提問，都沒有就是一段還沒問過問題的新對話。</summary>
    private static string BuildTitle(string? explicitTitle, string? firstQuestion)
    {
        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            return explicitTitle.Trim();
        }

        if (string.IsNullOrWhiteSpace(firstQuestion))
        {
            return "新對話";
        }

        var trimmed = firstQuestion.Trim().ReplaceLineEndings(" ");

        return trimmed.Length <= AutoTitleMaxLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, AutoTitleMaxLength), "…");
    }

    /// <summary>
    /// 建立一段新對話，回傳它的 Id。
    ///
    /// <para>
    /// 一開始就寫一行 meta，讓這段對話即使還沒問過問題也會出現在清單上——
    /// 否則使用者按了「開新對話」卻什麼都沒發生。
    /// </para>
    /// </summary>
    public async Task<string> CreateConversationAsync(
        AiChatScope scope,
        int targetId,
        string? createdBy,
        CancellationToken cancellationToken = default)
    {
        var conversationId = NewConversationId();
        var fullPath = GetFullPath(scope, targetId, conversationId);
        EnsureParentDirectory(fullPath);

        var meta = JsonSerializer.Serialize(
            new StoredMessage(MetaRole, string.Empty, createdBy, DateTime.Now), SerializerOptions);

        await File.WriteAllTextAsync(fullPath, meta + Environment.NewLine, FileEncoding, cancellationToken);

        logger.LogInformation(
            "AI chat conversation created. Scope={Scope}, TargetId={TargetId}, ConversationId={ConversationId}",
            scope,
            targetId,
            conversationId);

        return conversationId;
    }

    /// <summary>
    /// 把 0.4.79 之前的單檔對話搬進新結構。
    ///
    /// <para>
    /// ⚠️ **先寫新檔、成功之後才刪舊檔**：中途失敗寧可留兩份，也不要兩邊都沒有。
    /// 資料夾已存在就什麼都不做（冪等），所以每次列清單都呼叫也不會重複搬。
    /// </para>
    /// </summary>
    private void MigrateLegacyIfNeeded(AiChatScope scope, int targetId)
    {
        var legacyPath = GetLegacyPath(scope, targetId);
        if (!File.Exists(legacyPath))
        {
            return;
        }

        var directory = GetConversationDirectory(scope, targetId);
        if (Directory.Exists(directory))
        {
            // 已經轉過了。舊檔還在代表上一次搬完之後刪除失敗，這裡順手補刪。
            TryDeleteFile(legacyPath);
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, $"{NewConversationId()}.jsonl");
            File.Copy(legacyPath, target, overwrite: false);

            logger.LogInformation(
                "Legacy AI chat conversation migrated. Scope={Scope}, TargetId={TargetId}, Target={Target}",
                scope,
                targetId,
                target);

            TryDeleteFile(legacyPath);
        }
        catch (IOException ex)
        {
            // 搬不動就維持原狀：舊檔還在，下次再試。不能讓對話視窗因此打不開。
            logger.LogWarning(ex, "Failed to migrate legacy AI chat conversation. FullPath={FullPath}", legacyPath);
        }
    }

    private void TryDeleteFile(string fullPath)
    {
        try
        {
            File.Delete(fullPath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to delete file. FullPath={FullPath}", fullPath);
        }
    }

    /// <summary>改這段對話的名字。對話不存在時回 <see cref="UpdateOutcome.NotFound"/>。</summary>
    public async Task<UpdateOutcome> RenameConversationAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        string title,
        CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(scope, targetId, conversationId);
        var gate = writeLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(fullPath))
            {
                return UpdateOutcome.NotFound;
            }

            var lines = (await File.ReadAllLinesAsync(fullPath, cancellationToken)).ToList();
            var metaIndex = lines.FindIndex(x =>
                TryParseStored(x, logMalformed: false) is { } stored
                && string.Equals(stored.Role, MetaRole, StringComparison.Ordinal));

            var trimmed = title.Trim();

            if (metaIndex >= 0)
            {
                var current = TryParseStored(lines[metaIndex], logMalformed: false)!;
                lines[metaIndex] = JsonSerializer.Serialize(current with { Content = trimmed }, SerializerOptions);
            }
            else
            {
                // 轉檔進來的舊對話沒有 meta 行，補一行在最前面。
                lines.Insert(0, JsonSerializer.Serialize(
                    new StoredMessage(MetaRole, trimmed, null, DateTime.Now), SerializerOptions));
            }

            var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
            var tempPath = fullPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, content, FileEncoding, cancellationToken);
            File.Move(tempPath, fullPath, overwrite: true);

            return UpdateOutcome.Updated;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>刪掉其中一段對話。其他段不受影響。</summary>
    public void TryDeleteConversation(AiChatScope scope, int targetId, string conversationId)
    {
        var fullPath = GetFullPath(scope, targetId, conversationId);

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                logger.LogInformation(
                    "AI chat conversation deleted. Scope={Scope}, TargetId={TargetId}, ConversationId={ConversationId}",
                    scope,
                    targetId,
                    conversationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete AI chat conversation. FullPath={FullPath}", fullPath);
        }
    }

    /// <summary>把一輪問答（兩則訊息）接到對話尾端。</summary>
    public async Task AppendTurnAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        string question,
        string? askedBy,
        string answer,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.Now;
        var lines = new StringBuilder();

        lines.AppendLine(JsonSerializer.Serialize(
            new StoredMessage(AiChatService.UserRole, question.Trim(), askedBy, now), SerializerOptions));
        lines.AppendLine(JsonSerializer.Serialize(
            new StoredMessage(AiChatService.AssistantRole, answer, null, now), SerializerOptions));

        var fullPath = GetFullPath(scope, targetId, conversationId);
        EnsureParentDirectory(fullPath);

        var gate = writeLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // AppendAllText 只在檔案位置為 0 時寫 BOM，所以既有檔案不會被重複插入前置碼。
            await File.AppendAllTextAsync(fullPath, lines.ToString(), FileEncoding, cancellationToken);
        }
        finally
        {
            gate.Release();
        }

        logger.LogInformation(
            "AI chat turn appended. Scope={Scope}, TargetId={TargetId}, AnswerLength={AnswerLength}",
            scope,
            targetId,
            answer.Length);
    }

    /// <summary>讀出整段對話（依寫入順序，即時間由舊到新）。對話不存在時回空清單。</summary>
    public async Task<List<AiChatMessageItem>> ReadHistoryAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(scope, targetId, conversationId);
        if (!File.Exists(fullPath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        var items = new List<AiChatMessageItem>(lines.Length);

        foreach (var raw in lines)
        {
            var item = TryParseLine(raw);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>
    /// 刪除一個對象底下的**所有**對話（專案或會議被刪除時）。
    /// 不存在不算錯誤，失敗只記 Warning 不阻斷主流程
    /// （比照 <c>MeetingFileStore.TryDeleteTranscript</c>）。
    ///
    /// <para>
    /// ⚠️ 0.4.79 起刪的是**整個資料夾**而不是單一檔案。只刪一個檔的話，
    /// 該對象底下其他幾段對話會永遠留在硬碟上變成孤兒。
    /// 舊結構的單檔也要一起刪——可能有還沒被轉檔就直接刪除的對象。
    /// </para>
    /// </summary>
    public void TryDelete(AiChatScope scope, int targetId)
    {
        var directory = GetConversationDirectory(scope, targetId);

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation(
                    "AI chat history deleted. Scope={Scope}, TargetId={TargetId}", scope, targetId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete AI chat history folder. FullPath={FullPath}", directory);
        }

        // 還沒轉檔就被刪除的對象，舊的單檔仍在。
        var legacyPath = GetLegacyPath(scope, targetId);
        if (File.Exists(legacyPath))
        {
            TryDeleteFile(legacyPath);
        }

        // ⚠️ 刻意不 TryRemove 這些路徑的鎖。移除之後，已經持鎖的寫入者與下一個
        // GetOrAdd 拿到的會是兩把不同的號誌，互斥直接失效。多留幾個 SemaphoreSlim
        // 遠比那個競態便宜——鎖以完整路徑為鍵，0.4.79 之後粒度自動細到「單段對話」，
        // 上限是「有問過問題的專案＋會議」再乘上每個對象開過的對話段數。
    }

    /// <summary>
    /// 就地改寫對話中的若干則訊息（編輯內容、重新產生答案）。
    ///
    /// <para>
    /// 用「第幾則」定位而不是訊息 Id：本系統沒有單則刪除，append 只加在尾端、
    /// 編輯也不改行數，所以索引是穩定的。額外的好處是 LLM 生成要跑好幾秒，
    /// 期間別人 append 不會位移既有索引，「讀歷史 → 呼叫 API → 寫回索引 N」
    /// 這條長流程天生安全。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 這是唯一會<b>整檔重寫</b>的路徑，所以一定要寫暫存檔再 <c>File.Move</c>。
    /// 直接覆寫的話，沒有進鎖的 <see cref="ReadHistoryAsync"/> 會讀到被 truncate
    /// 的檔案，整段對話會在別人畫面上憑空消失。
    /// </para>
    ///
    /// <para>
    /// 對話是同專案／會議底下所有人共用的，所以每筆修改都要帶
    /// <see cref="MessageEdit.ExpectedRole"/> 與 <see cref="MessageEdit.ExpectedContent"/>
    /// 當樂觀鎖。任一筆對不上就整批不動。
    /// </para>
    /// </summary>
    public async Task<UpdateOutcome> UpdateMessagesAsync(
        AiChatScope scope,
        int targetId,
        string conversationId,
        IReadOnlyList<MessageEdit> edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return UpdateOutcome.Updated;
        }

        var fullPath = GetFullPath(scope, targetId, conversationId);
        var gate = writeLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 有人按了「清空這段對話」時檔案已經不在。這裡絕不能重建——
            // 那會讓一則已經被刪掉的訊息憑空復活。
            if (!File.Exists(fullPath))
            {
                return UpdateOutcome.NotFound;
            }

            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
            var map = MapValidLineIndexes(lines);

            // 先全部驗過再動手，確保「兩行同時改」不會出現只改到一半的中間態。
            foreach (var edit in edits)
            {
                if (edit.Index < 0 || edit.Index >= map.Count)
                {
                    return UpdateOutcome.Conflict;
                }

                var current = TryParseStored(lines[map[edit.Index]], logMalformed: false);
                if (current is null
                    || !string.Equals(current.Role, edit.ExpectedRole, StringComparison.Ordinal)
                    || !string.Equals(current.Content, edit.ExpectedContent, StringComparison.Ordinal))
                {
                    return UpdateOutcome.Conflict;
                }
            }

            foreach (var edit in edits)
            {
                var physical = map[edit.Index];
                var current = TryParseStored(lines[physical], logMalformed: false)!;
                var updated = current with
                {
                    Content = edit.NewContent,
                    AskedBy = edit.NewAskedBy ?? current.AskedBy,
                };

                lines[physical] = JsonSerializer.Serialize(updated, SerializerOptions);
            }

            // 結尾一定要留換行，否則下一次 AppendTurnAsync 會直接接在最後一行後面。
            var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;

            var tempPath = fullPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, content, FileEncoding, cancellationToken);
            File.Move(tempPath, fullPath, overwrite: true);

            logger.LogInformation(
                "AI chat messages updated. Scope={Scope}, TargetId={TargetId}, EditCount={EditCount}",
                scope,
                targetId,
                edits.Count);

            return UpdateOutcome.Updated;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 「第 n 則有效訊息」對應到「檔案第幾行」。
    ///
    /// ⚠️ 兩者不相等：壞掉的行與空行會被 <see cref="ReadHistoryAsync"/> 跳過，
    /// 但重寫檔案時<b>必須原樣保留</b>，不能默默丟掉使用者的資料。
    /// </summary>
    private List<int> MapValidLineIndexes(string[] lines)
    {
        var map = new List<int>(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            // ⚠️ 排除條件必須與 ReadHistoryAsync 完全一致（壞行、空行、meta 行）。
            // 少排除一種，兩邊的「第 n 則」就會錯開一位，編輯時會改到隔壁那一則。
            if (TryParseStored(lines[index], logMalformed: false) is { } stored && !IsMeta(stored))
            {
                map.Add(index);
            }
        }

        return map;
    }

    /// <summary>
    /// 全站累計的提問則數（儀表板用）。
    ///
    /// <para>
    /// 逐檔數行而不是維護一個計數器：對話檔數量是「有問過問題的專案＋會議」再乘上
    /// 每個對象開過的對話段數，儀表板一次載入掃過去可以接受，而額外的計數器要跟檔案
    /// 保持同步反而更容易錯。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 必須是 <see cref="SearchOption.AllDirectories"/>。0.4.79 起對話檔多了一層
    /// 對象資料夾，只掃根目錄的話這個數字會直接變成 0。
    /// </para>
    /// </summary>
    public int CountQuestions()
    {
        if (!Directory.Exists(rootPath))
        {
            return 0;
        }

        var count = 0;

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                foreach (var raw in File.ReadLines(file))
                {
                    if (TryParseLine(raw) is { } item && item.IsUser)
                    {
                        count++;
                    }
                }
            }
            catch (IOException ex)
            {
                // 正在被寫入的檔案讀不到不該讓整個儀表板掛掉。
                logger.LogWarning(ex, "Failed to read AI chat file while counting. FullPath={FullPath}", file);
            }
        }

        return count;
    }

    /// <summary>
    /// 解析一行成畫面用的 DTO。壞掉的行（手動編輯過、寫到一半斷電）跳過，
    /// 不要讓整段歷史都讀不出來。
    /// </summary>
    private AiChatMessageItem? TryParseLine(string raw)
    {
        var stored = TryParseStored(raw, logMalformed: true);

        // ⚠️ meta 行不是訊息。漏掉這個判斷的話，畫面上會出現一則沒有內容、
        // 發話者是「AI 助理」的空氣訊息（role 不是 user 就會被當成回答）。
        return stored is null || IsMeta(stored)
            ? null
            : new AiChatMessageItem(stored.Role, stored.Content, stored.AskedBy, stored.CreatedAt);
    }

    private static bool IsMeta(StoredMessage stored)
        => string.Equals(stored.Role, MetaRole, StringComparison.Ordinal);

    /// <summary>
    /// 解析一行成磁碟格式。重寫檔案時要保留 <c>askedBy</c> 與 <c>createdAt</c>，
    /// 所以不能只拿畫面用的 DTO。
    /// </summary>
    /// <param name="logMalformed">
    /// 掃描整檔做索引映射時會把每一行再解析一次，那時不該重複記一遍 Warning。
    /// </param>
    private StoredMessage? TryParseStored(string raw, bool logMalformed)
    {
        // 第一行帶著檔案的 BOM，不去掉的話反序列化會直接失敗。
        var line = raw.TrimStart('\uFEFF').Trim();
        if (line.Length == 0)
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredMessage>(line, SerializerOptions);

            return stored is null || string.IsNullOrEmpty(stored.Role) ? null : stored;
        }
        catch (JsonException ex)
        {
            if (logMalformed)
            {
                logger.LogWarning(ex, "Skipped a malformed AI chat line.");
            }

            return null;
        }
    }

    private static void EnsureParentDirectory(string fullPath)
    {
        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }
}
