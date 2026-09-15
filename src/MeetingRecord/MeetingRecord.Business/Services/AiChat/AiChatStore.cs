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
/// **一段對話一個檔**，路徑由「範圍＋對象 Id」直接算出來（<c>project/3.jsonl</c>、
/// <c>meeting/12.jsonl</c>），所以資料庫連相對路徑都不必存——這也是這一版能把整張資料表
/// 移除的原因。根目錄取自 <see cref="SystemSettings.ExternalFileSystem"/>，
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

    /// <summary>某段對話的檔案完整路徑。專案 12 與會議 12 會落在不同子目錄，不會互相污染。</summary>
    public string GetFullPath(AiChatScope scope, int targetId)
    {
        var folder = scope == AiChatScope.Project ? "project" : "meeting";
        return Path.Combine(rootPath, folder, $"{targetId}.jsonl");
    }

    /// <summary>把一輪問答（兩則訊息）接到對話尾端。</summary>
    public async Task AppendTurnAsync(
        AiChatScope scope,
        int targetId,
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

        var fullPath = GetFullPath(scope, targetId);
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
        CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(scope, targetId);
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
    /// 刪除整段對話的檔案。檔案不存在不算錯誤，失敗只記 Warning 不阻斷主流程
    /// （比照 <c>MeetingFileStore.TryDeleteTranscript</c>）。
    /// </summary>
    public void TryDelete(AiChatScope scope, int targetId)
    {
        var fullPath = GetFullPath(scope, targetId);

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                logger.LogInformation(
                    "AI chat history deleted. Scope={Scope}, TargetId={TargetId}", scope, targetId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete AI chat history file. FullPath={FullPath}", fullPath);
        }

        // ⚠️ 刻意不 TryRemove 這個路徑的鎖。移除之後，已經持鎖的寫入者與下一個
        // GetOrAdd 拿到的會是兩把不同的號誌，互斥直接失效。多留一個 SemaphoreSlim
        // 遠比那個競態便宜——對話檔的數量本來就是「有問過問題的專案＋會議」的量級。
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
        IReadOnlyList<MessageEdit> edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return UpdateOutcome.Updated;
        }

        var fullPath = GetFullPath(scope, targetId);
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
            if (TryParseStored(lines[index], logMalformed: false) is not null)
            {
                map.Add(index);
            }
        }

        return map;
    }

    /// <summary>
    /// 全站累計的提問則數（儀表板用）。
    ///
    /// 逐檔數行而不是維護一個計數器：對話檔數量是「有問過問題的專案＋會議」的量級，
    /// 儀表板一次載入掃過去可以接受，而額外的計數器要跟檔案保持同步反而更容易錯。
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

        return stored is null
            ? null
            : new AiChatMessageItem(stored.Role, stored.Content, stored.AskedBy, stored.CreatedAt);
    }

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
