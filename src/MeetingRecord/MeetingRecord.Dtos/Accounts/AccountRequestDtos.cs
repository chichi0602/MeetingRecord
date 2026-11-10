namespace MeetingRecord.Dtos.Accounts;

/// <summary>變更自己的密碼（1.1.6，<c>POST /api/v1/account/change-password</c>）。</summary>
public class ChangePasswordRequestDto
{
    public string CurrentPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>WASM 頁擋下使用者時寫的稽核（1.1.6，<c>POST /api/v1/audit/page-denied</c>）。</summary>
public class PageDeniedRequestDto
{
    /// <summary>被擋下的頁面路由，例如 <c>/categories</c>。</summary>
    public string Route { get; set; } = string.Empty;
}
