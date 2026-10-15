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

    /// <summary>
    /// 登入 Cookie 名稱（0.4.119）。必須帶專案名：Cookie 不分連接埠，同一主機名稱部署多個系統時，
    /// 沿用框架預設名就會互相覆蓋、互相登出。⚠️ 改這個值會讓既有登入（連同記住我）全部失效一次。
    /// </summary>
    public const string AuthCookieName = ".MeetingRecord.Auth";

    /// <summary>
    /// Data Protection 的應用程式名稱（0.4.119）。⚠️ 改這個值＝所有人被登出一次。
    /// 不固定時判別子取自 content root 路徑，換一個部署目錄就等於換一組金鑰用途，既有 Cookie 一樣失效。
    /// </summary>
    public const string DataProtectionApplicationName = "MeetingRecord";
    public const string 開發者帳號 = "support";
    /// <summary>
    /// 新帳號的預設角色。0.4.101 起可以多角色、權限取聯集，這只是起點；
    /// <c>DefaultRoleSeeder</c> 啟動時確保它存在，不會動任何人的角色。
    /// </summary>
    public const string 預設角色 = "一般使用者";

    /// <summary>0.4.97 以前的預設角色名稱，只用來在啟動時把舊資料改名。</summary>
    public const string 舊版預設角色 = "預設角色";
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
    /// 「系統管理 → AI 管理」群組與底下的頁面（1.0.30）。⚠️ 與 Menu.json 的 name 一字不差、尾端不加空白
    /// （上面幾個常數尾端有空白是既有資料的既成事實，新常數不要跟著加）。
    /// 兩頁都**只限管理者**、不在權限目錄裡（對話紀錄含會議逐字稿全文；用量含全公司的人名與金額），
    /// 頁面以 <c>CheckIsAdmin()</c> 守門，側邊欄以 <c>SidebarMenuService.AdminOnlyMenuIds</c> 排除非管理者。
    /// </summary>
    public const string 角色_AI管理 = "AI 管理";
    public const string 角色_AI對話紀錄 = "AI 對話紀錄";
    public const string 角色_Token用量 = "Token 用量";

    /// <summary>
    /// 0.4.80～1.0.29 的「AI 用量分析」權限鍵（可勾給角色）。1.0.30 起頁面改名「Token 用量」並只限管理者，
    /// 只用來在啟動時把舊資料庫裡的權限列與角色勾選清掉（<c>RbacBackfillService</c>），新程式不要再引用。
    /// </summary>
    public const string 已退役權限鍵_AI用量分析 = "AI 用量分析";

    /// <summary>
    /// 側邊欄「系統管理」底下的兩個子群組（0.4.120）。群組本身沒有權限鍵、也不在權限目錄：
    /// 底下有任何一項看得到才顯示（見 <c>SidebarMenuService.GroupOnlyMenuIds</c>）。
    /// </summary>
    public const string 角色_帳號與權限 = "帳號與權限";

    /// <summary>
    /// 「帳號與權限」底下的稽核紀錄頁（1.0.32）。**只限管理者**、刻意不放進權限目錄：
    /// 稽核紀錄是追究責任的證據，不能讓被稽核的人自己看或清。頁面以 <c>CheckIsAdmin()</c> 守門，
    /// 側邊欄以 <c>SidebarMenuService.AdminOnlyMenuIds</c> 排除非管理者。與 Menu.json 的 name 一字不差。
    /// </summary>
    public const string 角色_稽核紀錄 = "稽核紀錄";
    public const string 角色_監控與診斷 = "監控與診斷";

    /// <summary>
    /// 「監控與診斷」底下的管理者限定頁面（0.4.120 起；0.4.93～0.4.119 名為「系統健康度」且可開給角色）。
    /// ⚠️ 這些名稱**刻意不放進** <c>RolePermissionService.GetRoleListPermissionAllName()</c>：
    /// 日誌、例外堆疊可能含敏感資訊，只有管理者看得到。頁面一律以 <c>CheckIsAdmin()</c> 守門，
    /// 側邊欄以 <c>SidebarMenuService.AdminOnlyMenuIds</c> 排除非管理者——不靠權限鍵，
    /// 舊資料庫殘留的權限列也開不了門。與 Menu.json 的 name 一字不差、尾端不加空白。
    /// </summary>
    public const string 角色_系統健康監控 = "系統健康監控";
    public const string 角色_日誌檢視 = "日誌檢視";
    public const string 角色_日誌等級設定 = "日誌等級設定";
    public const string 角色_系統例外紀錄 = "系統例外紀錄";
    public const string 角色_資料庫用量 = "資料庫用量";

    /// <summary>
    /// 0.4.93～0.4.119 的「系統健康度」權限鍵。只用來在啟動時把舊資料庫裡的權限列與角色勾選清掉
    /// （<c>RbacBackfillService</c>），新程式不要再引用。
    /// </summary>
    public const string 已退役權限鍵_系統健康度 = "系統健康度";
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
