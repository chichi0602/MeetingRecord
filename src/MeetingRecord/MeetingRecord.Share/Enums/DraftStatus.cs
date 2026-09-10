namespace MeetingRecord.Share.Enums;

/// <summary>
/// 以 LLM 把逐字稿整理成會議紀錄草稿的處理狀態。
/// 以 int 儲存於資料庫，顯示文字集中在 <see cref="DraftStatusText.Describe"/>。
/// 與 <see cref="TranscriptionStatus"/> 刻意分開：轉錄完成不代表已產生草稿，
/// 兩段工作各自有成功／失敗，走各自的背景佇列。
/// </summary>
public enum DraftStatus
{
    /// <summary>尚未產生過草稿</summary>
    NotGenerated = 0,

    /// <summary>已排入佇列，等待背景生成</summary>
    Pending = 1,

    /// <summary>背景生成進行中</summary>
    Processing = 2,

    /// <summary>生成完成，草稿內容已寫入 DraftContent</summary>
    Completed = 3,

    /// <summary>生成失敗，失敗原因記錄於 DraftError</summary>
    Failed = 4,

    /// <summary>使用者主動取消（排隊中或執行中皆可）。刻意與 Failed 分開——把主動取消顯示成紅色的「失敗」是說謊。</summary>
    Cancelled = 5,
}

/// <summary>草稿狀態的顯示文字（UI 與日誌共用，避免各處各自翻譯）。</summary>
public static class DraftStatusText
{
    public static string Describe(DraftStatus status) => status switch
    {
        DraftStatus.NotGenerated => "未產生",
        DraftStatus.Pending => "待處理",
        DraftStatus.Processing => "生成中",
        DraftStatus.Completed => "已完成",
        DraftStatus.Failed => "失敗",
        DraftStatus.Cancelled => "已取消",
        _ => "未知",
    };
}
