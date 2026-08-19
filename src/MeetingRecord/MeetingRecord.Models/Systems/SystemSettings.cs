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
}

public class BootstrapSettings
{
    public string SupportAccount { get; set; } = "support";
    public string SupportName { get; set; } = "support";
    public string SupportEmail { get; set; } = "support";
    public string SupportPassword { get; set; } = "support";
}
