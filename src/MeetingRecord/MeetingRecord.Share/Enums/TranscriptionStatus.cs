namespace MeetingRecord.Share.Enums;

/// <summary>
/// 會議影音檔的語音轉文字（STT）處理狀態。
/// 以 int 儲存於資料庫，顯示文字集中在 <see cref="TranscriptionStatusText.Describe"/>。
/// </summary>
public enum TranscriptionStatus
{
    /// <summary>尚未上傳影音檔</summary>
    NotUploaded = 0,

    /// <summary>已上傳，等待背景轉錄</summary>
    Pending = 1,

    /// <summary>背景轉錄進行中</summary>
    Processing = 2,

    /// <summary>轉錄完成，逐字稿已產生</summary>
    Completed = 3,

    /// <summary>轉錄失敗，失敗原因記錄於 TranscriptionError</summary>
    Failed = 4,

    /// <summary>使用者主動取消（排隊中或執行中皆可）。刻意與 Failed 分開——把主動取消顯示成紅色的「失敗」是說謊。</summary>
    Cancelled = 5,
}

/// <summary>轉錄狀態的顯示文字（UI 與日誌共用，避免各處各自翻譯）。</summary>
public static class TranscriptionStatusText
{
    public static string Describe(TranscriptionStatus status) => status switch
    {
        TranscriptionStatus.NotUploaded => "未上傳",
        TranscriptionStatus.Pending => "待處理",
        TranscriptionStatus.Processing => "處理中",
        TranscriptionStatus.Completed => "已完成",
        TranscriptionStatus.Failed => "失敗",
        TranscriptionStatus.Cancelled => "已取消",
        _ => "未知",
    };
}
