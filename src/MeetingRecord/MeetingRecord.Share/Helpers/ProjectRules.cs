namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 專案的選項與限制（1.1.26，WASM 遷移 M3）：從 <c>ProjectAdapterModel</c>／<c>ProjectService</c> 抽出來，
/// 伺服器與 WASM 專案頁共用一份。封存規則在 <see cref="ProjectScopeRules"/>。
/// </summary>
public static class ProjectRules
{
    /// <summary>狀態選項。順序即畫面下拉的順序，第一項為新增時的預設值，索引 1（進行中）是「重新開啟」改回的狀態。</summary>
    public static readonly IReadOnlyList<string> StatusOptions =
    [
        "未開始",
        "進行中",
        ProjectScopeRules.ArchivedStatus,
        "暫緩",
        "等待"
    ];

    /// <summary>專案附件單檔上限（1GB）。</summary>
    public const long MaxAttachmentFileSize = 1024L * 1024L * 1024L;
}
