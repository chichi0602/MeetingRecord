namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 稽核摘要裡的 <c>via=…</c>：同一種動作從哪個入口進來（1.0.33）。
/// ⚠️ 不是動作代碼，所以不放在 <see cref="AuditActions"/>（慣例測試會把那裡的常數都當成動作檢查）。
/// </summary>
public static class AuditVia
{
    public const string Web = "Web";
    public const string Api = "Api";
    public const string Blazor = "Blazor";
}

/// <summary>
/// 被系統強制登出的原因（1.0.33）：<c>AuthenticationStateHelper.Check</c> 導向 <c>/Auths/Logout?reason=…</c>，
/// 登出頁只認這裡列的值，其他一律當成使用者自己登出。
/// </summary>
public static class LogoutReasons
{
    public const string InvalidClaim = "invalid-claim";
    public const string Deleted = "deleted";
    public const string Disabled = "disabled";
    public const string NoRole = "no-role";
    public const string PermissionError = "permission-error";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        InvalidClaim, Deleted, Disabled, NoRole, PermissionError,
    };

    /// <summary>導向登出頁的網址。</summary>
    public static string LogoutUrl(string reason) => $"/Auths/Logout?reason={reason}";
}
