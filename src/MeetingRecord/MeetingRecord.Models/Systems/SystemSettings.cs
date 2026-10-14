namespace MeetingRecord.Models.Systems;

public class SystemSettings
{
    public ConnectionStrings ConnectionStrings { get; set; } = new();
    public SystemInformation SystemInformation { get; set; } = new();
    public ExternalFileSystem ExternalFileSystem { get; set; } = new();
}

public class ConnectionStrings
{
    public string SQLiteDefaultConnection { get; set; } = string.Empty;

}
public class SystemInformation
{
    public string SystemVersion { get; set; } = string.Empty;
    public string SystemName { get; set; } = string.Empty;
    public string SystemDescription { get; set; } = string.Empty;
}
public class ExternalFileSystem
{
    public string DatabasePath { get; set; } = string.Empty;
    public string DownloadPath { get; set; } = string.Empty;
    public string UploadPath { get; set; } = string.Empty;
    public string ProjectFilePath { get; set; } = string.Empty;

    /// <summary>會議影音檔（原始上傳檔）存放根目錄</summary>
    public string MeetingMediaPath { get; set; } = string.Empty;

    /// <summary>會議逐字稿（.txt）存放根目錄</summary>
    public string MeetingTranscriptPath { get; set; } = string.Empty;

    /// <summary>
    /// AI 問答對話（.jsonl）存放根目錄。
    ///
    /// 對話文字不需要被查詢或索引，只會整段讀出來顯示，放資料庫只會讓它無謂膨脹，
    /// 所以 0.4.60 起移到檔案系統。底下再分 <c>project/</c> 與 <c>meeting/</c> 兩個子目錄；
    /// 0.4.79 起再往下多一層對象 Id 資料夾（<c>project/3/&lt;對話 Id&gt;.jsonl</c>），
    /// 一個專案或會議可以有多段對話。
    /// </summary>
    public string AiChatPath { get; set; } = string.Empty;

    /// <summary>
    /// Data Protection 金鑰環（<c>key-*.xml</c>）存放目錄（0.4.119）。登入 Cookie 用這些金鑰加密，
    /// 存在這裡才撐得過 IIS 回收與重啟。
    /// ⚠️ 換目錄或刪掉裡面的檔案＝所有人被登出一次；不可放在網站目錄內（重新部署會被清掉）。
    /// </summary>
    public string DataProtectionKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// 系統例外紀錄的堆疊檔（<c>yyyyMM/{簽章}.txt</c>）與啟動失敗補登檔（<c>pending/*.json</c>）根目錄（0.4.122）。
    /// 資料庫只存摘要，完整堆疊在這裡；刪除紀錄時一併刪檔（<c>ExceptionStackFileStore</c>）。
    /// </summary>
    public string ExceptionPath { get; set; } = string.Empty;
}

public class BootstrapSettings
{
    public string SupportAccount { get; set; } = "support";
    public string SupportName { get; set; } = "support";
    public string SupportEmail { get; set; } = "support";
    public string SupportPassword { get; set; } = "support";
}
