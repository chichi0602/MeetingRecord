using System.ComponentModel.DataAnnotations;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 會議紀錄（獨立主資料，無外鍵關聯）。
///
/// 一筆會議只搭配「一個」影音檔，因此媒體欄位直接內嵌，不另開附件子表；
/// 影音檔與逐字稿的實體檔案存放於檔案系統（路徑見 SystemSettings.ExternalFileSystem），
/// 資料表只保存相對路徑等中繼資料。
/// </summary>
public class Meeting
{
    public int Id { get; set; }

    [Required(ErrorMessage = "會議標題 不可為空白")]
    public string Title { get; set; } = string.Empty;

    /// <summary>會議日期（可空）</summary>
    public DateTime? MeetingDate { get; set; }

    public string? Description { get; set; }

    /// <summary>分類標籤（多值，以分隔字串儲存，可空）</summary>
    public string? Categories { get; set; }

    /// <summary>團隊標籤（多值，以分隔字串儲存，可空）</summary>
    public string? Teams { get; set; }

    #region 影音檔中繼資料（尚未上傳時全為 null）

    /// <summary>使用者上傳時的原始檔名</summary>
    public string? MediaOriginalFileName { get; set; }

    /// <summary>實際落地的檔名（GUID + 原副檔名）</summary>
    public string? MediaStoredFileName { get; set; }

    /// <summary>相對於 MeetingMediaPath 的路徑，例如 2026/08/xxxx.mp4</summary>
    public string? MediaRelativePath { get; set; }

    public string? MediaContentType { get; set; }

    public long? MediaFileSize { get; set; }

    #endregion

    #region 轉錄結果

    /// <summary>相對於 MeetingTranscriptPath 的逐字稿路徑，例如 2026/08/xxxx.txt</summary>
    public string? TranscriptRelativePath { get; set; }

    public TranscriptionStatus TranscriptionStatus { get; set; } = TranscriptionStatus.NotUploaded;

    /// <summary>轉錄失敗原因（成功時為 null）</summary>
    public string? TranscriptionError { get; set; }

    public DateTime? TranscriptionStartedAt { get; set; }

    public DateTime? TranscriptionCompletedAt { get; set; }

    #endregion

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
