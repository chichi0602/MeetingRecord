namespace MeetingRecord.Share.Helpers;

public class MagicObjectHelper
{
    #region 系統層面用到神奇字串
    public const string DefaultSQLiteConnectionStringKey = "SQLiteDefaultConnection";
    public const string SQLiteDatabaseFilename = "BackendDB.db";
    public static string GetSQLiteConnectionString(string databasePath)
    {
        return $"Data Source={Path.Combine(databasePath, SQLiteDatabaseFilename)}";
    }
    public const string CookieScheme = "CookieAuthenticationScheme";
    /// <summary>
    /// OAuth 外部登入流程暫存身分用的 Cookie 配置名稱
    /// </summary>
    public const string ExternalCookieScheme = "ExternalCookieScheme";
    public const string 開發者帳號 = "support";
    public const string 預設角色 = "預設角色";
    public const string NeedChangePassword = "123456";

    public static readonly int PageSize = 8;

    public const string Menu結構定義 = "Datas/Menu.json";
    public const string SignoutUrl = "/auths/logout";
    #endregion

    #region 角色
    public const string 角色_專案管理 = "專案管理功能";
    public const string 角色_專案項目 = "專案項目";
    public const string 角色_待辦事項 = "待辦事項";
    public const string 角色_使用說明 = "使用說明";
    public const string 角色_系統管理 = "系統管理功能";
    public const string 角色_使用者管理 = "使用者管理";
    public const string 角色_角色管理 = "角色管理 ";

    /// <summary>
    /// AI 用量分析（0.4.80）。⚠️ 這個值會直接拿去做字串比對，也是側邊欄的顯示名，
    /// 而且必須與 Menu.json 的 name 一字不差。上面幾個常數尾端有空白是既有資料的
    /// 既成事實（「角色管理 」「登出 」），新常數不要跟著加。
    /// </summary>
    public const string 角色_AI用量分析 = "AI 用量分析";

    /// <summary>
    /// 系統健康度（0.4.93）。同上：與 Menu.json 的 name 一字不差、尾端不加空白。
    /// ⚠️ 開給非管理員時，頁面下方的「原始日誌」仍然只有管理員看得到。
    /// </summary>
    public const string 角色_系統健康度 = "系統健康度";
    public const string 角色_資料定義 = "資料定義管理功能";
    public const string 角色_分類清單 = "分類清單";
    public const string 角色_團隊清單 = "團隊清單";
    public const string 角色_提示詞清單 = "提示詞清單";
    public const string 角色_會議管理 = "會議管理功能";
    public const string 角色_會議紀錄 = "會議紀錄";
    public const string 角色_登出 = "登出 ";
    public const string 使用者角色 = "使用者角色";

    #endregion

    #region 認證與授權
    public const string 你沒有權限存取此頁面 = "你沒有權限存取此頁面";

    #endregion
}
