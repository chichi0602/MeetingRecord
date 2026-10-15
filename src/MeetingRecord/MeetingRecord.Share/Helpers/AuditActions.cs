namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 稽核紀錄的動作代碼（1.0.32，比照 NET10-Blazor-Starter 的 AuditActions）。格式 <c>名詞.動作</c>。
///
/// <para>⚠️ <b>代碼一經寫入資料庫就不能改名</b>：舊資料存的是字串，改名後舊紀錄會變成另一種動作，
/// 篩選也對不上。要換說法只改 <see cref="DisplayNames"/> 的中文。</para>
/// <para>⚠️ 呼叫 <c>IAuditLogService.WriteAsync</c> 一律傳這裡的常數，不要寫字串常值（<c>AuditActionsConventionTests</c> 會擋）；
/// 新增常數時同時在 <see cref="DisplayNames"/> 補中文。</para>
/// </summary>
public static class AuditActions
{
    #region 登入
    public const string LoginSuccess = "Login.Success";
    public const string LoginFailed = "Login.Failed";
    public const string LoginLockedOut = "Login.LockedOut";
    public const string LoginDisabled = "Login.Disabled";
    #endregion

    #region 使用者與角色
    public const string UserCreate = "User.Create";
    public const string UserUpdate = "User.Update";
    public const string UserDelete = "User.Delete";
    public const string RoleCreate = "Role.Create";
    public const string RoleUpdate = "Role.Update";
    public const string RoleDelete = "Role.Delete";
    #endregion

    #region AI 用量上限、AI 對話紀錄、Token 用量
    public const string AiQuotaBlocked = "Ai.QuotaBlocked";
    public const string AiCallLogView = "AiCallLog.View";
    public const string AiCallLogDownloadJson = "AiCallLog.DownloadJson";
    public const string AiCallLogExportPdf = "AiCallLog.ExportPdf";
    public const string AiCallLogExport = "AiCallLog.Export";
    public const string AiCallLogDelete = "AiCallLog.Delete";
    public const string AiCallLogPurge = "AiCallLog.Purge";
    public const string AiCallLogClearAll = "AiCallLog.ClearAll";
    public const string AiCallLogAutoPurge = "AiCallLog.AutoPurge";
    public const string AiUsageExport = "AiUsage.Export";
    public const string AiUsageExportPdf = "AiUsage.ExportPdf";
    public const string AiUsageDelete = "AiUsage.Delete";
    public const string AiUsagePurge = "AiUsage.Purge";
    public const string AiUsageClearAll = "AiUsage.ClearAll";
    public const string AiUsageAutoPurge = "AiUsage.AutoPurge";
    #endregion

    #region 監控與診斷
    public const string ExceptionLogExport = "ExceptionLog.Export";
    public const string ExceptionLogDelete = "ExceptionLog.Delete";
    public const string ExceptionLogPurge = "ExceptionLog.Purge";
    public const string ExceptionLogClearAll = "ExceptionLog.ClearAll";
    public const string ExceptionLogAutoPurge = "ExceptionLog.AutoPurge";
    public const string ExceptionLogAiAnalyze = "ExceptionLog.AiAnalyze";
    public const string ExceptionLogAiAnalyzeExportPdf = "ExceptionLog.AiAnalyzeExportPdf";
    public const string LogLevelApply = "LogLevel.Apply";
    public const string LogLevelRestore = "LogLevel.Restore";
    public const string LogViewerExport = "LogViewer.Export";
    public const string LogViewerAiAnalyze = "LogViewer.AiAnalyze";
    public const string LogViewerAiAnalyzeExportPdf = "LogViewer.AiAnalyzeExportPdf";
    #endregion

    #region 稽核紀錄本身
    public const string AuditExport = "Audit.Export";
    public const string AuditPurge = "Audit.Purge";
    public const string AuditClearAll = "Audit.ClearAll";
    public const string AuditAutoPurge = "Audit.AutoPurge";
    #endregion

    /// <summary>代碼 → 畫面顯示的中文名稱。</summary>
    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LoginSuccess] = "登入成功",
        [LoginFailed] = "登入失敗",
        [LoginLockedOut] = "登入鎖定",
        [LoginDisabled] = "停用帳號嘗試登入",

        [UserCreate] = "新增使用者",
        [UserUpdate] = "修改使用者",
        [UserDelete] = "刪除使用者",
        [RoleCreate] = "新增角色",
        [RoleUpdate] = "修改角色",
        [RoleDelete] = "刪除角色",

        [AiQuotaBlocked] = "AI 用量上限攔截",
        [AiCallLogView] = "查看 AI 對話紀錄",
        [AiCallLogDownloadJson] = "下載 AI 對話 JSON",
        [AiCallLogExportPdf] = "匯出 AI 對話 PDF",
        [AiCallLogExport] = "匯出 AI 對話紀錄",
        [AiCallLogDelete] = "刪除 AI 對話紀錄",
        [AiCallLogPurge] = "清除舊 AI 對話紀錄",
        [AiCallLogClearAll] = "清空 AI 對話紀錄",
        [AiCallLogAutoPurge] = "自動清除 AI 對話紀錄",
        [AiUsageExport] = "匯出 Token 用量",
        [AiUsageExportPdf] = "匯出 Token 用量 PDF",
        [AiUsageDelete] = "刪除 Token 用量",
        [AiUsagePurge] = "清除舊 Token 用量",
        [AiUsageClearAll] = "清空 Token 用量",
        [AiUsageAutoPurge] = "自動清除 Token 用量",

        [ExceptionLogExport] = "匯出系統例外紀錄",
        [ExceptionLogDelete] = "刪除系統例外紀錄",
        [ExceptionLogPurge] = "清除舊系統例外紀錄",
        [ExceptionLogClearAll] = "清空系統例外紀錄",
        [ExceptionLogAutoPurge] = "自動清除系統例外紀錄",
        [ExceptionLogAiAnalyze] = "AI 分析系統例外",
        [ExceptionLogAiAnalyzeExportPdf] = "匯出系統例外 AI 分析 PDF",
        [LogLevelApply] = "套用日誌等級",
        [LogLevelRestore] = "還原日誌等級",
        [LogViewerExport] = "匯出日誌",
        [LogViewerAiAnalyze] = "AI 分析日誌",
        [LogViewerAiAnalyzeExportPdf] = "匯出日誌 AI 分析 PDF",

        [AuditExport] = "匯出稽核紀錄",
        [AuditPurge] = "清除舊稽核紀錄",
        [AuditClearAll] = "清空稽核紀錄",
        [AuditAutoPurge] = "自動清除稽核紀錄",
    };

    /// <summary>代碼的中文名稱；對照表沒有（例如以後不再使用的舊代碼）就直接回傳代碼。</summary>
    public static string Describe(string? action)
        => string.IsNullOrEmpty(action)
            ? string.Empty
            : DisplayNames.TryGetValue(action, out var name) ? name : action;

    /// <summary>代碼的類別（第一個 <c>.</c> 之前），畫面依此上色。</summary>
    public static string CategoryOf(string? action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return string.Empty;
        }

        var dot = action.IndexOf('.');
        return dot < 0 ? action : action[..dot];
    }
}
