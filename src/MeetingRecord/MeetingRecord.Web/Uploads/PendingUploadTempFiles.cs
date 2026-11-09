namespace MeetingRecord.Web.Uploads;

/// <summary>
/// 專案附件的伺服器暫存檔（專案頁選附件時先存到這裡，按確定才寫進專案）。
///
/// <para>
/// 啟動時以 <see cref="DeleteStale"/> 清掉沒人要的殘檔（1.0.65）：網站當掉或重啟時正在編輯的暫存檔沒人刪；
/// 1.0.64 以前存檔成功後也會留一份（串流還開著就刪，Windows 上刪不掉）。
/// </para>
/// </summary>
public static class PendingUploadTempFiles
{
    public static string DefaultDirectory
        => Path.Combine(Path.GetTempPath(), "MeetingRecord", "pending-uploads");

    /// <summary>
    /// 刪掉最後寫入時間早於 <paramref name="maxAge"/> 以前的 <c>*.tmp</c>，回傳刪了幾個。個別刪不掉的寫 Warning 後繼續。
    /// </summary>
    /// <remarks>
    /// ⚠️ 只刪舊的、不整個清空：IIS 回收時新舊行程會短暫並存，舊行程的使用者可能正開著編輯視窗、暫存檔還要用。
    /// </remarks>
    public static int DeleteStale(string directory, TimeSpan maxAge, DateTime utcNow, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = utcNow - maxAge;
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete stale pending upload temp file. Path={Path}", path);
            }
        }

        return deleted;
    }
}
