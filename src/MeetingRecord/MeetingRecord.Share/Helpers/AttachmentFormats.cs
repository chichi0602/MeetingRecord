namespace MeetingRecord.Share.Helpers;

/// <summary>擷取得到文字的文件格式（1.1.22 從 <c>AttachmentTextExtractor.IsSupported</c> 抽出，WASM 頁的 AI 問答附件檢查也用）。</summary>
public static class AttachmentFormats
{
    public static bool IsTextExtractable(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" or ".docx" or ".txt" or ".md" or ".csv" => true,
            _ => false,
        };
    }
}
