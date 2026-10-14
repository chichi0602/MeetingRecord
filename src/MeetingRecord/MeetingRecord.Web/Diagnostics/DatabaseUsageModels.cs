namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 每張資料表的估算方式。
/// </summary>
public enum SizeEstimateMethod
{
    /// <summary>各欄位內容的位元組加總；不含索引、頁面碎片與閒置頁。</summary>
    ColumnByteSum,

    /// <summary>由 dbstat 虛擬表取得的真實頁數。</summary>
    DbStat,
}

public sealed class TableUsage
{
    public string TableName { get; set; } = string.Empty;

    /// <summary>null 代表該表連筆數都查不到，畫面顯示為破折號。</summary>
    public long? RowCount { get; set; }

    /// <summary>估算位元組數；null 代表該表估算失敗，畫面顯示為破折號。</summary>
    public long? EstimatedBytes { get; set; }

    public int IndexCount { get; set; }

    /// <summary>估算失敗時的原因說明。</summary>
    public string Note { get; set; } = string.Empty;
}

public sealed class DatabaseUsageReport
{
    public string DatabaseFilePath { get; set; } = string.Empty;

    /// <summary>主檔檔名，供卡片副標使用。由連線的 DataSource 推導而非硬編碼。</summary>
    public string DatabaseFileName { get; set; } = string.Empty;

    public long MainDbBytes { get; set; }

    /// <summary>預寫日誌（-wal）。</summary>
    public long WalBytes { get; set; }

    /// <summary>共享記憶體索引（-shm）。</summary>
    public long ShmBytes { get; set; }

    public long TotalOnDiskBytes => MainDbBytes + WalBytes + ShmBytes;

    public bool WalFileExists { get; set; }

    public bool ShmFileExists { get; set; }

    public long PageCount { get; set; }

    public long PageSize { get; set; }

    /// <summary>已配置：page_count × page_size，含尚未併回主檔的頁。</summary>
    public long AllocatedBytes => PageCount * PageSize;

    public long FreelistCount { get; set; }

    /// <summary>可回收：freelist_count × page_size，VACUUM 可釋放。</summary>
    public long ReclaimableBytes => FreelistCount * PageSize;

    public IReadOnlyList<TableUsage> Tables { get; set; } = [];

    public SizeEstimateMethod EstimateMethod { get; set; } = SizeEstimateMethod.ColumnByteSum;

    /// <summary>非致命問題：檔案讀不到、個別資料表估算失敗等。</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>給使用者看的一般狀態訊息（記憶體資料庫、無資料表等）。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>致命錯誤訊息；有值時畫面只顯示此訊息。</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    public long ElapsedMilliseconds { get; set; }
}
