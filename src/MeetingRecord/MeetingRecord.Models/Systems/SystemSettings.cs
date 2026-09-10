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
    /// 所以 0.4.60 起移到檔案系統。底下再分 <c>project/</c> 與 <c>meeting/</c> 兩個子目錄。
    /// </summary>
    public string AiChatPath { get; set; } = string.Empty;
}

public class BootstrapSettings
{
    public string SupportAccount { get; set; } = "support";
    public string SupportName { get; set; } = "support";
    public string SupportEmail { get; set; } = "support";
    public string SupportPassword { get; set; } = "support";
}
