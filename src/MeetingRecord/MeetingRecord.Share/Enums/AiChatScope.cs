namespace MeetingRecord.Share.Enums;

/// <summary>AI 問答的對象（1.1.22 從 Business 搬來，WASM 頁也用）。</summary>
public enum AiChatScope
{
    /// <summary>整個專案：讀本專案的所有會議紀錄與附件。</summary>
    Project = 0,

    /// <summary>單一會議：讀該會議的會議紀錄與逐字稿。</summary>
    Meeting = 1,
}
