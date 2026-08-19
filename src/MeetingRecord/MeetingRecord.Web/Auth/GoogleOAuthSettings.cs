namespace MeetingRecord.Web.Auth;

/// <summary>
/// Google OAuth2 第三方登入設定。
/// 正式環境請改用 user-secrets 或環境變數提供 ClientId / ClientSecret。
/// </summary>
public class GoogleOAuthSettings
{
    public const string SectionName = "GoogleOAuthSettings";

    /// <summary>
    /// 是否啟用 Google 登入。未啟用時不會註冊 Google 驗證，登入頁也不顯示按鈕。
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Google Cloud Console 建立的 OAuth 2.0 用戶端 ID。
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Google Cloud Console 建立的 OAuth 2.0 用戶端密鑰。
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// 首次以 Google 登入而自動建立的使用者所套用的預設角色名稱。
    /// </summary>
    public string DefaultRoleName { get; set; } = "預設角色";

    /// <summary>
    /// 是否已提供有效的用戶端設定。
    /// </summary>
    public bool IsConfigured =>
        Enabled
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);
}
