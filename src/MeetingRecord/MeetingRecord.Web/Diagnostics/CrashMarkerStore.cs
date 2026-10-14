using System.Text.Json;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Web.Diagnostics;

/// <summary>
/// 「補登檔」：程序即將結束、例外來不及寫進資料庫時，先把它存成檔案，下次成功啟動時再補進系統例外紀錄。
///
/// 為什麼需要：例外紀錄靠背景寫入器（<see cref="ExceptionLogWriter"/>）寫資料庫，而它要到 <c>app.Run()</c> 才啟動。
/// 啟動失敗（migration、種子資料、設定驗證）與程序層級的未處理例外，入列後程序就結束了，紀錄就此遺失。
///
/// 檔案放在 <c>{ExceptionPath}/pending/</c>：沿用既有路徑設定，整合測試的 CreateSettings() 已把它導向暫存目錄。
///
/// ⚠️ <b>本類別絕不可使用 <see cref="ILogger"/>。</b>呼叫它的時候記錄機制可能已經失效（甚至還沒建立），
/// 一律走 NLog 的 InternalLogger，且所有方法都不得拋出。
/// </summary>
public static class CrashMarkerStore
{
    public const string FolderName = "pending";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 寫入一筆補登檔。路徑未設定或寫入失敗時放棄（日誌檔仍有紀錄）。
    /// <paramref name="operation"/> 與一般紀錄一樣是固定樣板，不可帶參數值，否則每次都會變成新簽章。
    /// </summary>
    public static void Write(string? exceptionPath, Exception exception, string source, string operation, string loggerName)
    {
        Write(exceptionPath, new ExceptionLogEntry
        {
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.ToString(),
            Source = source,
            Operation = operation,
            LoggerName = loggerName,
            OccurredAt = DateTime.Now,
            // 會寫補登檔的都是「程序即將結束」等級的事件，補登時視同 Critical（告警會立即通知）。
            IsCritical = true,
        });
    }

    public static void Write(string? exceptionPath, ExceptionLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(exceptionPath))
        {
            return;
        }

        try
        {
            var folder = Path.Combine(exceptionPath, FolderName);
            Directory.CreateDirectory(folder);

            var fileName = $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26] + ".json";
            File.WriteAllText(Path.Combine(folder, fileName), JsonSerializer.Serialize(entry, JsonOptions));
        }
        catch (Exception ex)
        {
            NLog.Common.InternalLogger.Warn(ex, "Failed to write crash marker file.");
        }
    }

    /// <summary>讀出所有補登檔（依檔名＝時間排序）。讀不了的檔案略過，不會中斷其他檔案。</summary>
    public static IReadOnlyList<(string FilePath, ExceptionLogEntry Entry)> ReadAll(string? exceptionPath)
    {
        var results = new List<(string, ExceptionLogEntry)>();
        if (string.IsNullOrWhiteSpace(exceptionPath))
        {
            return results;
        }

        try
        {
            var folder = Path.Combine(exceptionPath, FolderName);
            if (Directory.Exists(folder) == false)
            {
                return results;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ExceptionLogEntry>(File.ReadAllText(file));
                    if (entry is not null)
                    {
                        results.Add((file, entry));
                    }
                }
                catch (Exception ex)
                {
                    NLog.Common.InternalLogger.Warn(ex, "Failed to read crash marker file {0}.", file);
                }
            }
        }
        catch (Exception ex)
        {
            NLog.Common.InternalLogger.Warn(ex, "Failed to enumerate crash marker files.");
        }

        return results;
    }

    public static void Delete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception ex)
        {
            NLog.Common.InternalLogger.Warn(ex, "Failed to delete crash marker file {0}.", filePath);
        }
    }
}
