using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// AI 對話紀錄的內容檔存放（1.0.29，移植自 NET10-Blazor-Starter）。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>刪單筆、清除此日之前、清空全部、自動過期、刪除會議／專案時的連帶刪除
/// 都必須經過這裡，不可有人自己去 <c>File.Delete</c>（同 <see cref="ExceptionStackFileStore"/> 的理由）。
///
/// 相對路徑為 <c>{yyyyMM}/{CallId:N}.json</c>。全部方法<b>絕不拋出</b>：記錄對話失敗不可以影響 AI 呼叫本身。
/// 日誌只記路徑，絕不記內容。
/// </summary>
public class AiCallLogFileStore
{
    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly ILogger<AiCallLogFileStore> logger;

    public AiCallLogFileStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<AiCallLogFileStore> logger)
    {
        this.systemSettingsOptions = systemSettingsOptions;
        this.logger = logger;
    }

    private string RootPath => systemSettingsOptions.Value.ExternalFileSystem.AiCallLogPath;

    /// <summary>由時間與 CallId 組出相對路徑。純函式。</summary>
    public static string BuildRelativePath(DateTime occurredAt, Guid callId) => $"{occurredAt:yyyyMM}/{callId:N}.json";

    /// <summary>寫入內容檔。</summary>
    /// <returns>成功時回傳相對路徑；失敗回 null（<b>不得</b>讓資料列因此建不起來）。</returns>
    public async Task<string?> WriteAsync(DateTime occurredAt, Guid callId, string content)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrEmpty(content))
        {
            return null;
        }

        var relativePath = BuildRelativePath(occurredAt, callId);

        try
        {
            var fullPath = ToFullPath(relativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content);
            return relativePath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write AI call log file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取內容檔。檔案不存在或讀取失敗一律回 null，由呼叫端顯示友善訊息。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var fullPath = ToFullPath(relativePath);
            return File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read AI call log file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>刪除單一檔案。檔案不存在視為成功。</summary>
    public void Delete(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        try
        {
            var fullPath = ToFullPath(relativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete AI call log file. Path={RelativePath}", relativePath);
        }
    }

    /// <summary>
    /// 清空整個目錄（供「全部清除」使用）。
    /// 刻意刪內容而非刪目錄本身，因為目錄由 Program.cs 啟動時建立，刪掉就不會再有人補。
    /// </summary>
    public void DeleteAll()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                Directory.Delete(directory, recursive: true);
            }

            foreach (var file in Directory.EnumerateFiles(RootPath))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear the AI call log directory.");
        }
    }

    /// <summary>
    /// 刪除早於 <paramref name="threshold"/> 所在月份的整個 <c>{yyyyMM}</c> 目錄。
    ///
    /// 自動過期的收尾：逐筆刪檔後剩下的空月份目錄，以及「檔已寫、資料列沒建起來」留下的孤兒檔。
    /// 門檻所在月份本身不動——那個月可能還有未過期的紀錄。
    /// </summary>
    public void DeleteMonthsBefore(DateTime threshold)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || !Directory.Exists(RootPath))
        {
            return;
        }

        var boundary = new DateTime(threshold.Year, threshold.Month, 1);

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                var name = Path.GetFileName(directory);
                if (DateTime.TryParseExact(name, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                    && month < boundary)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove expired AI call log folders.");
        }
    }

    private string ToFullPath(string relativePath)
        => Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
