using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiChat;

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

        writeLocks.TryRemove(fullPath, out _);
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
    /// 解析一行。壞掉的行（手動編輯過、寫到一半斷電）跳過，不要讓整段歷史都讀不出來。
    /// </summary>
    private AiChatMessageItem? TryParseLine(string raw)
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
            if (stored is null || string.IsNullOrEmpty(stored.Role))
            {
                return null;
            }

            return new AiChatMessageItem(stored.Role, stored.Content, stored.AskedBy, stored.CreatedAt);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipped a malformed AI chat line.");
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
