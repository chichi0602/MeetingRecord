namespace MeetingRecord.Dtos.Accounts;

/// <summary>變更自己的密碼（1.1.6，<c>POST /api/v1/account/change-password</c>）。</summary>
public class ChangePasswordRequestDto
{
    public string CurrentPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>
/// 變更密碼頁（<c>/ChangePassword</c>）與設定 API 密碼頁（<c>/Profile</c>）的內容（1.1.13）。
/// 規則照原頁：要確認新密碼、沒有字數下限；設定 API 密碼時，還沒有本地密碼的帳號（例如 Google 帳號）免填目前密碼。
/// </summary>
public class OwnPasswordRequestDto
{
    public string? CurrentPassword { get; set; }

    public string NewPassword { get; set; } = string.Empty;

    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>自己是否已有本地密碼（1.1.13，<c>/Profile</c> 決定要不要顯示「目前密碼」）。</summary>
public class PasswordStatusDto
{
    public bool HasLocalPassword { get; set; }
}

/// <summary>WASM 頁擋下使用者時寫的稽核（1.1.6，<c>POST /api/v1/audit/page-denied</c>）。</summary>
public class PageDeniedRequestDto
{
    /// <summary>被擋下的頁面路由，例如 <c>/categories</c>。</summary>
    public string Route { get; set; } = string.Empty;
}
