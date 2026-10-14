using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MeetingRecord.Web.Diagnostics;

public interface ILogQueryService
{
    Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// 讀取 NLog 檔案目標的內容，依時間區段、最低等級與關鍵字篩選後取最新 N 筆。
///
/// 讀取策略為「依時間升冪逐檔前向串流」，邊讀邊解析、邊篩選，通過者推入容量為 N 的佇列。
/// 時間為 O(檔案大小)、記憶體為 O(N)。之所以不從檔尾反向讀，是因為續行（堆疊追蹤）屬於其上方的
/// 紀錄，反向讀需處理跨緩衝區的續行歸屬與 UTF-8 多位元組邊界，複雜度與風險都遠高於收益。
///
/// 0.4.121 移植自 NET10-Blazor-Starter（「監控與診斷 → 日誌檢視」）。
/// </summary>
public sealed class LogQueryService : ILogQueryService
{
    /// <summary>${longdate} 的實際寬度：yyyy-MM-dd HH:mm:ss.ffff。</summary>
    private const int TimestampLength = 24;

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffff";

    /// <summary>layout 的欄位數：longdate|traceId|level|threadId|logger|message|exception。</summary>
    private const int FieldCount = 7;

    private readonly INLogFilePathResolver pathResolver;
    private readonly ILogger<LogQueryService> logger;

    public LogQueryService(INLogFilePathResolver pathResolver, ILogger<LogQueryService> logger)
    {
        this.pathResolver = pathResolver;
        this.logger = logger;
    }

    public async Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken cancellationToken = default)
    {
        var result = new LogQueryResult();
        var stopwatch = Stopwatch.StartNew();

        var (start, end, take) = Normalize(request, result);
        result.AppliedStartTime = start;
        result.AppliedEndTime = end;

        var directory = pathResolver.GetLogDirectory();
        if (string.IsNullOrEmpty(directory))
        {
            result.Message = "NLog:BasePath 未設定，無法定位日誌檔案。";
            return result;
        }

        if (Directory.Exists(directory) == false)
        {
            result.Message = $"日誌目錄不存在：{directory}";
            return result;
        }

        var files = pathResolver.GetExistingFilesInRange(
            DateOnly.FromDateTime(start),
            DateOnly.FromDateTime(end));

        if (files.Count == 0)
        {
            result.Message = "指定區間內沒有日誌檔案。";
            return result;
        }

        var collected = new Queue<LogEntry>();
        var scannedFiles = 0;
        var scannedLines = 0;
        var sequence = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // ⚠️ 這裡刻意**不**用 File.GetLastWriteTime 做「整檔跳過」的優化。
            //
            // nlog.config 設了 keepFileOpen="true"，NTFS 對持續開啟的檔案會延遲更新目錄項的
            // last-write time，取到的值可能落後實際寫入近一小時；而日誌檢視頁的預設查詢區間
            // 正是「最近 1 小時」。一旦誤判，**正在寫入的當天日誌檔會被整個跳過**，
            // 畫面顯示「查無日誌紀錄」，但檔案裡其實有資料。
            //
            // 何況它幾乎沒有效益：GetExistingFilesInRange 已依檔名日期過濾，區間外的檔案
            // 本來就不會出現在這裡；只有「起始日當天、且已停止寫入」的檔案才省得到一次讀取。
            //
            // 逐檔的提前結束改由 ReadFileAsync 依**內容時間戳**判斷（下方的 ReachedEnd），
            // 那個判斷不受檔案系統中繼資料影響。
            try
            {
                var readResult = await ReadFileAsync(
                    file, start, end, take, request.MinimumLevel, request.Keyword, request.TraceId.Trim(),
                    collected, sequence, cancellationToken);

                sequence = readResult.Sequence;
                scannedLines += readResult.LineCount;
                scannedFiles++;

                if (readResult.ReachedEnd)
                {
                    // 紀錄本身時間遞增，後續檔案只會更新，不需再讀。
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 單一檔案失敗不應毀掉整次查詢。
                logger.LogWarning(ex, "Failed to read log file {LogFile}.", file);
                result.Warnings.Add($"日誌檔讀取失敗，已略過：{Path.GetFileName(file)}（{ex.GetType().Name}）");
            }
        }

        var entries = collected.ToList();

        // Sequence 於讀取時遞增，此處重編為 1..N，讓畫面顯示連續。
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].Sequence = index + 1;
        }

        result.Entries = entries;
        result.ScannedFileCount = scannedFiles;
        result.ScannedLineCount = scannedLines;

        if (entries.Count == 0 && string.IsNullOrEmpty(result.Message))
        {
            result.Message = "指定條件下查無日誌紀錄。";
        }

        stopwatch.Stop();
        logger.LogInformation(
            "Log query completed. Elapsed={ElapsedMs}ms, Files={Files}, Lines={Lines}, Returned={Returned}",
            stopwatch.ElapsedMilliseconds, scannedFiles, scannedLines, entries.Count);

        return result;
    }

    /// <summary>
    /// 夾住不合理的查詢條件，並把調整內容寫進 Warnings 讓使用者看得到。
    /// </summary>
    private static (DateTime Start, DateTime End, int Take) Normalize(LogQueryRequest request, LogQueryResult result)
    {
        var start = request.StartTime;
        var end = request.EndTime;

        if (end < start)
        {
            (start, end) = (end, start);
            result.Warnings.Add("起始時間晚於結束時間，已自動對調。");
        }

        if (end - start > LogQueryRequest.MaxSpan)
        {
            start = end - LogQueryRequest.MaxSpan;
            result.Warnings.Add($"查詢區間上限為 {LogQueryRequest.MaxSpan.TotalDays:0} 天，起始時間已調整為 {start:yyyy-MM-dd HH:mm:ss}。");
        }

        var take = request.Take;
        if (take < 1)
        {
            take = 1;
            result.Warnings.Add("查詢筆數至少為 1 筆。");
        }
        else if (take > LogQueryRequest.MaxTake)
        {
            take = LogQueryRequest.MaxTake;
            result.Warnings.Add($"查詢筆數上限為 {LogQueryRequest.MaxTake} 筆，已自動調整。");
        }

        return (start, end, take);
    }

    private readonly record struct FileReadResult(bool ReachedEnd, int Sequence, int LineCount);

    /// <summary>
    /// 串流讀取單一日誌檔。
    /// </summary>
    private async Task<FileReadResult> ReadFileAsync(
        string filePath,
        DateTime start,
        DateTime end,
        int take,
        LogLevelRank minimumLevel,
        string keyword,
        string traceId,
        Queue<LogEntry> collected,
        int sequence,
        CancellationToken cancellationToken)
    {
        // FileShare.ReadWrite 為必要：nlog.config 設了 keepFileOpen="true"，檔案握把不會釋放。
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);

        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

        var buffer = new StringBuilder();
        DateTime pendingTimestamp = default;
        var hasPending = false;
        var reachedEnd = false;
        var lineCount = 0;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineCount++;

            if (TryParseTimestamp(line, out var timestamp))
            {
                if (hasPending
                    && Flush(buffer, pendingTimestamp, start, end, take, minimumLevel, keyword, traceId, collected, ref sequence))
                {
                    reachedEnd = true;
                    break;
                }

                buffer.Clear();
                buffer.Append(line);
                pendingTimestamp = timestamp;
                hasPending = true;
                continue;
            }

            if (hasPending)
            {
                // 續行：屬於上一筆紀錄的堆疊追蹤。
                buffer.Append('\n').Append(line);
                continue;
            }

            // 檔首的孤兒續行：沒有時間戳就無法排序也無法做時間篩選，只能捨棄。
            logger.LogDebug("Discarded orphan continuation line at head of {LogFile}.", filePath);
        }

        if (reachedEnd == false
            && hasPending
            && Flush(buffer, pendingTimestamp, start, end, take, minimumLevel, keyword, traceId, collected, ref sequence))
        {
            reachedEnd = true;
        }

        return new FileReadResult(reachedEnd, sequence, lineCount);
    }

    /// <summary>
    /// 將緩衝中的完整紀錄套用篩選並入列。
    /// 必須等整筆（含所有續行）湊齊才能做，因為關鍵字比對的是完整原始文字。
    /// </summary>
    /// <returns>true 代表這筆已超過結束時間，可停止讀取。</returns>
    private static bool Flush(
        StringBuilder buffer,
        DateTime timestamp,
        DateTime start,
        DateTime end,
        int take,
        LogLevelRank minimumLevel,
        string keyword,
        string traceId,
        Queue<LogEntry> collected,
        ref int sequence)
    {
        if (timestamp > end)
        {
            return true;
        }

        if (timestamp < start)
        {
            return false;
        }

        var raw = buffer.ToString();
        var entry = Parse(raw, timestamp);

        // 無法解析等級者一律保留，不因等級篩選而隱藏資料。
        if (minimumLevel != LogLevelRank.Any
            && entry.Rank != LogLevelRank.Unknown
            && entry.Rank < minimumLevel)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(keyword) == false
            && raw.Contains(keyword, StringComparison.OrdinalIgnoreCase) == false)
        {
            return false;
        }

        if (traceId.Length > 0
            && string.Equals(entry.TraceId, traceId, StringComparison.OrdinalIgnoreCase) == false)
        {
            return false;
        }

        entry.Sequence = ++sequence;
        collected.Enqueue(entry);

        while (collected.Count > take)
        {
            collected.Dequeue();
        }

        return false;
    }

    /// <summary>
    /// 判斷一行是否為新紀錄的開頭，並順帶取出時間戳。
    /// 以字元位置檢查取代正規式：此判斷每個實體行都要跑一次，可能達數百 MB。
    /// </summary>
    private static bool TryParseTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;

        return line.Length > TimestampLength
            && line[TimestampLength] == '|'
            && DateTime.TryParseExact(
                line.AsSpan(0, TimestampLength),
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp);
    }

    private static LogEntry Parse(string raw, DateTime timestamp)
    {
        var entry = new LogEntry
        {
            Timestamp = timestamp,
            Raw = raw,
        };

        // 只取第一個實體行做欄位切分，續行屬於例外內容。
        var newLineIndex = raw.IndexOf('\n');
        var headLine = newLineIndex >= 0 ? raw[..newLineIndex] : raw;

        // 前五個欄位結構上不可能含 '|'，由左往右切是安全的；
        // 切成 6 段可讓最後一段（message|exception）保持原樣。
        var parts = headLine.Split('|', FieldCount - 1);
        if (parts.Length < FieldCount - 1)
        {
            // 格式不符仍保留該筆，不隱藏資料。
            entry.Message = headLine.Length > TimestampLength ? headLine[(TimestampLength + 1)..] : headLine;
            entry.HasException = newLineIndex >= 0;
            return entry;
        }

        entry.TraceId = parts[1];
        entry.Level = parts[2];
        entry.Rank = ToRank(parts[2]);
        entry.ThreadId = parts[3];
        entry.Logger = parts[4];

        // 剩下的是 message|exception，但 message 本身可能含 '|'（layout 未逸出），
        // 只能以最後一個 '|' 當分界。切錯時僅影響「訊息」欄顯示，展開列的 Raw 永遠正確。
        var rest = parts[5];
        var lastPipe = rest.LastIndexOf('|');
        if (lastPipe >= 0)
        {
            entry.Message = rest[..lastPipe];
            entry.HasException = lastPipe < rest.Length - 1 || newLineIndex >= 0;
        }
        else
        {
            entry.Message = rest;
            entry.HasException = newLineIndex >= 0;
        }

        return entry;
    }

    // 解析日誌檔時，無法辨識的等級回 Unknown —— 該筆仍保留，不因等級篩選而隱藏。
    private static LogLevelRank ToRank(string level)
        => LogLevelRankHelper.FromLevelText(level, LogLevelRank.Unknown);
}
