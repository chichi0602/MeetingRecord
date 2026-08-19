namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 動作粒度權限的動作名稱與權限鍵組合規則。
/// 權限鍵有兩種：
/// - 裸頁面鍵（如「專案項目」）＝ 舊制，代表該頁全部動作（向後相容）。
/// - 動作鍵（如「專案項目:edit」）＝ 特定動作。
/// </summary>
public static class PermissionActions
{
    public const string View = "view";
    public const string Create = "create";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Export = "export";
}

public static class PermissionKey
{
    public const char Separator = ':';

    /// <summary>組合頁面 + 動作為權限鍵，例如 For("專案項目","edit") = "專案項目:edit"。</summary>
    public static string For(string page, string action) => $"{page}{Separator}{action}";

    /// <summary>若為動作鍵則回傳其頁面部分，否則回傳 null。</summary>
    public static string? PageOf(string key)
    {
        var index = key.IndexOf(Separator);
        return index > 0 ? key[..index] : null;
    }
}
