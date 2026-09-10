namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 檔案大小的顯示格式。
///
/// 抽到 Share 是因為專案附件清單與儀表板的「音檔總容量」需要同一套規則——
/// 兩邊各寫一份遲早會出現「同一個數字兩種寫法」。Share 不相依任何專案，兩層都取用得到。
/// </summary>
public static class FileSizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>把位元組數換算成人看得懂的單位；負數視為 0。</summary>
    public static string Describe(long fileSize)
    {
        double size = Math.Max(fileSize, 0);
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < Units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {Units[unitIndex]}";
    }
}
