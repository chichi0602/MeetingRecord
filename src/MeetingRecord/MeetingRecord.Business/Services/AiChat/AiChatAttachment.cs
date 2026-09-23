namespace MeetingRecord.Business.Services.AiChat;

/// <summary>AI 問答附件的種類（0.4.95）。</summary>
public enum AiChatAttachmentKind
{
    /// <summary>圖片：原樣送給模型看（需要支援影像輸入的模型）。</summary>
    Image = 0,

    /// <summary>文件：擷取文字後放進參考資料。</summary>
    Document = 1,
}

/// <summary>
/// 已存進對話資料夾的一個附件。
/// </summary>
/// <param name="FileName">使用者看到的原始檔名。</param>
/// <param name="StoredName">磁碟上的檔名（隨機產生，只在對話資料夾內有意義）。</param>
/// <param name="Kind">圖片或文件。</param>
/// <param name="Size">位元組數。</param>
public sealed record AiChatAttachment(string FileName, string StoredName, AiChatAttachmentKind Kind, long Size);

/// <summary>畫面剛收到、還沒存檔的附件。內容整份在記憶體裡（上限見 <see cref="AiChatAttachmentPolicy"/>）。</summary>
public sealed record PendingAttachment(string FileName, byte[] Content);

/// <summary>
/// 附件的格式與大小規則（0.4.95）。抽成純函式以便單元測試，畫面與服務層共用同一份——
/// 畫面擋一次是為了即時回饋，服務層再擋一次是因為畫面擋不住所有路徑（例如貼上）。
/// </summary>
public static class AiChatAttachmentPolicy
{
    /// <summary>一次提問最多幾個附件。</summary>
    public const int MaxAttachmentsPerQuestion = 5;

    /// <summary>單張圖片上限。Azure 的上限是 20 MB，但圖片越大越貴、上傳越慢，一般截圖遠低於此。</summary>
    public const long MaxImageBytes = 10L * 1024 * 1024;

    /// <summary>單一文件上限，與專案附件一致的量級。</summary>
    public const long MaxDocumentBytes = 20L * 1024 * 1024;

    /// <summary>
    /// 一次呼叫最多帶幾張圖（這次的＋歷史視窗內的）。超過時保留最新的——
    /// 每張圖都會換算成 token 計費，無上限地累積會讓每次追問都比上一次更貴。
    /// </summary>
    public const int MaxImagesPerRequest = 8;

    private static readonly Dictionary<string, string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    /// <summary>檔案挑選對話框的 accept 值。只影響挑選對話框，拖放與貼上仍要靠 <see cref="Classify"/> 擋。</summary>
    public const string AcceptAttribute = ".png,.jpg,.jpeg,.gif,.webp,.pdf,.docx,.txt,.md,.csv";

    /// <summary>給使用者看的支援格式說明。</summary>
    public const string SupportedDescription = "圖片（png、jpg、gif、webp）與文件（pdf、docx、txt、md、csv）";

    /// <summary>依副檔名判斷種類；不支援的格式回傳 null。</summary>
    public static AiChatAttachmentKind? Classify(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (ImageMediaTypes.ContainsKey(Path.GetExtension(fileName)))
        {
            return AiChatAttachmentKind.Image;
        }

        // 文件格式與專案附件的文字擷取共用同一份清單，擷取不了的格式收進來也沒用。
        return AttachmentTextExtractor.IsSupported(fileName) ? AiChatAttachmentKind.Document : null;
    }

    /// <summary>圖片的 MIME 類型（組 data URL 用）。非圖片回傳 null。</summary>
    public static string? GetImageMediaType(string fileName)
        => ImageMediaTypes.TryGetValue(Path.GetExtension(fileName), out var mediaType) ? mediaType : null;

    /// <summary>
    /// 驗證一個附件；可以收下時回傳 null，否則回傳給使用者看的原因。
    /// </summary>
    public static string? Validate(string fileName, long size)
    {
        var kind = Classify(fileName);
        if (kind is null)
        {
            return $"「{fileName}」的格式不支援。可以附上：{SupportedDescription}。";
        }

        if (size <= 0)
        {
            return $"「{fileName}」是空檔案。";
        }

        var limit = kind == AiChatAttachmentKind.Image ? MaxImageBytes : MaxDocumentBytes;
        return size > limit
            ? $"「{fileName}」超過 {limit / 1024 / 1024} MB 上限。"
            : null;
    }
}
