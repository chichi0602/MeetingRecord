namespace MeetingRecord.Share.Helpers;

/// <summary>
/// 目錄實際佔用的位元組數。
///
/// <para>
/// 存在的理由是**逐字稿沒有容量欄位**：<c>Meeting</c> 只記 <c>TranscriptRelativePath</c>，
/// 所以儀表板的儲存空間細分只能實際去量。放在 Share 與 <see cref="FileSizeFormatter"/> 作伴——
/// 量出來的數字幾乎一定接著要格式化，兩者同一個去處。
/// </para>
///
/// <para>
/// ⚠️ <b>量到的是磁碟現況，不是資料庫認得的檔案</b>：資料庫已刪、檔案還在的孤兒檔也會被算進去。
/// 這是刻意的——「儲存空間」問的是磁碟被吃掉多少，孤兒檔佔的也是磁碟。
/// </para>
/// </summary>
public static class DirectorySizeCalculator
{
    /// <summary>
    /// 量一個目錄（含子目錄）的總位元組數。
    ///
    /// <para>
    /// 路徑空白或目錄不存在時回 0——全新環境還沒有任何檔案，那不是錯誤。
    /// 個別檔案讀不到（正在被寫入、權限不足）一律略過而不是往上拋：
    /// 一個讀不到的暫存檔不該讓整個儀表板顯示不出來（比照 <c>AiChatStore.CountQuestions</c>）。
    /// </para>
    /// </summary>
    public static long Measure(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return 0L;
        }

        var total = 0L;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // 正在被寫入或已被刪除的檔案，跳過。
                }
                catch (UnauthorizedAccessException)
                {
                    // 沒有權限讀的檔案，跳過。
                }
            }
        }
        catch (IOException)
        {
            // 列舉途中目錄被移除，已累加的部分照樣回報。
        }
        catch (UnauthorizedAccessException)
        {
            // 子目錄沒有權限，已累加的部分照樣回報。
        }

        return total;
    }
}
