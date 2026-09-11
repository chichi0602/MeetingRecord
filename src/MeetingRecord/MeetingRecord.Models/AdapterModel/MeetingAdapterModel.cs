using System.ComponentModel.DataAnnotations;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Models.AdapterModel;

public class MeetingAdapterModel : ICloneable
{
    public int Id { get; set; }

    [Required(ErrorMessage = "會議標題 不可為空白")]
    [StringLength(200, ErrorMessage = "會議標題長度不可超過 200 字元")]
    public string Title { get; set; } = string.Empty;

    public DateTime? MeetingDate { get; set; }

    [StringLength(2000, ErrorMessage = "描述長度不可超過 2000 字元")]
    public string? Description { get; set; }

    public List<string> Categories { get; set; } = [];

    public List<string> Teams { get; set; } = [];

    public string CategoriesText => string.Join("、", Categories);

    public string TeamsText => string.Join("、", Teams);

    #region 影音檔中繼資料

    public string? MediaOriginalFileName { get; set; }

    public string? MediaStoredFileName { get; set; }

    public string? MediaRelativePath { get; set; }

    public string? MediaContentType { get; set; }

    public long? MediaFileSize { get; set; }

    public bool HasMedia => !string.IsNullOrWhiteSpace(MediaRelativePath);

    #endregion

    #region 轉錄結果

    public string? TranscriptRelativePath { get; set; }

    public TranscriptionStatus TranscriptionStatus { get; set; } = TranscriptionStatus.NotUploaded;

    public string? TranscriptionError { get; set; }

    public DateTime? TranscriptionStartedAt { get; set; }

    public DateTime? TranscriptionCompletedAt { get; set; }

    /// <summary>轉錄狀態的顯示文字（清單欄位用）</summary>
    public string TranscriptionStatusText => Share.Enums.TranscriptionStatusText.Describe(TranscriptionStatus);

    /// <summary>逐字稿已產生，可供預覽</summary>
    public bool CanPreviewTranscript =>
        TranscriptionStatus == TranscriptionStatus.Completed
        && !string.IsNullOrWhiteSpace(TranscriptRelativePath);

    /// <summary>
    /// 可重新送出轉錄。用「排除」而不是「列舉」，與 <see cref="CanGenerateDraft"/> 對稱：
    ///
    /// <para>
    /// <see cref="TranscriptionStatus.Pending"/> 代表已入列但還沒開工，再按一次會重複入列、
    /// 被單一 worker 依序跑兩趟並**重複計費**（進度面板以 Kind-MeetingId 為 key，兩筆會收成
    /// 一列，畫面上看不出來）。重啟殘留的 Pending 由 Program.cs 的啟動修復改成 Failed，
    /// 不靠這裡放行。
    /// </para>
    ///
    /// <para>
    /// <see cref="TranscriptionStatus.Cancelled"/> 必須放行——取消後沒有進度可接續，只能整個
    /// 重跑。0.4.65 之前這裡是列舉式且漏了 Cancelled，取消過的紀錄根本不會出現重新轉錄鈕
    /// （服務層其實允許），唯一的出路是重新上傳檔案。
    /// </para>
    /// </summary>
    public bool CanRetryTranscription =>
        HasMedia
        && TranscriptionStatus is not (TranscriptionStatus.Pending or TranscriptionStatus.Processing);

    #endregion

    #region 專案歸屬

    public int? ProjectId { get; set; }

    /// <summary>所屬專案名稱。跨物件欄位，由服務層另外填入（AutoMapper 的同名慣例對應不到）。</summary>
    public string? ProjectTitle { get; set; }

    /// <summary>尚未被任何專案取用</summary>
    public bool IsUnassigned => ProjectId is null;

    /// <summary>歸屬狀態的顯示文字（清單欄位用）</summary>
    public string ProjectTitleText => ProjectTitle ?? "— 未歸屬";

    #endregion

    #region AI 會議紀錄草稿

    public string? DraftContent { get; set; }

    public DraftStatus DraftStatus { get; set; } = DraftStatus.NotGenerated;

    public string? DraftError { get; set; }

    public int? DraftPromptTemplateId { get; set; }

    public string? DraftPromptTemplateName { get; set; }

    public DateTime? DraftStartedAt { get; set; }

    public DateTime? DraftCompletedAt { get; set; }

    /// <summary>草稿狀態的顯示文字（清單欄位用）</summary>
    public string DraftStatusText => Share.Enums.DraftStatusText.Describe(DraftStatus);

    /// <summary>草稿已產生，可供檢視與編修</summary>
    public bool HasDraft =>
        DraftStatus == DraftStatus.Completed
        && !string.IsNullOrWhiteSpace(DraftContent);

    /// <summary>可送出生成（逐字稿已完成，且目前不在生成中）</summary>
    public bool CanGenerateDraft =>
        CanPreviewTranscript
        && DraftStatus != DraftStatus.Processing
        && DraftStatus != DraftStatus.Pending;

    #endregion

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public MeetingAdapterModel Clone()
    {
        var cloned = (MeetingAdapterModel)((ICloneable)this).Clone();
        // MemberwiseClone 為淺複製，標籤清單需另建新實例，避免編輯中的修改回寫到清單資料列。
        cloned.Categories = [.. Categories];
        cloned.Teams = [.. Teams];
        return cloned;
    }

    object ICloneable.Clone()
    {
        return MemberwiseClone();
    }
}
