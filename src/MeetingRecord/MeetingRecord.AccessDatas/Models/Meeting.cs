using System.ComponentModel.DataAnnotations;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.AccessDatas.Models;

/// <summary>
/// 會議紀錄。
///
/// 一筆會議只搭配「一個」影音檔，因此媒體欄位直接內嵌，不另開附件子表；
/// 影音檔與逐字稿的實體檔案存放於檔案系統（路徑見 SystemSettings.ExternalFileSystem），
/// 資料表只保存相對路徑等中繼資料。
///
/// 0.4.31 起可歸屬於一個專案項目（ProjectId 可空）：一份逐字稿只能屬於一個專案，
/// 一個專案可以有多份逐字稿。歸屬後才能對它執行「AI 轉會議紀錄」。
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

    #region 專案歸屬（尚未歸屬時為 null）

    /// <summary>
    /// 所屬專案項目。可空 —— 剛上傳、還沒被任何專案取用的逐字稿為 null。
    /// 刪除專案時設為 null（DeleteBehavior.SetNull），逐字稿與草稿本身保留。
    /// </summary>
    public int? ProjectId { get; set; }

    public Project? Project { get; set; }

    #endregion

    #region AI 會議紀錄草稿

    /// <summary>LLM 產生的會議紀錄內容。落庫且可人工編修；重新生成會覆蓋，不保留版本歷程。</summary>
    public string? DraftContent { get; set; }

    public DraftStatus DraftStatus { get; set; } = DraftStatus.NotGenerated;

    /// <summary>生成失敗原因（成功時為 null）</summary>
    public string? DraftError { get; set; }

    /// <summary>本次生成所使用的提示詞範本 Id</summary>
    public int? DraftPromptTemplateId { get; set; }

    /// <summary>生成當下的提示詞名稱快照。範本日後被改名或刪除時，仍看得出當初用了什麼。</summary>
    public string? DraftPromptTemplateName { get; set; }

    public DateTime? DraftStartedAt { get; set; }

    public DateTime? DraftCompletedAt { get; set; }

    /// <summary>由本會議紀錄產生的待辦事項。會議刪除時待辦保留，只是失去來源。</summary>
    public ICollection<Todo> Todos { get; set; } = [];

    #endregion

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
