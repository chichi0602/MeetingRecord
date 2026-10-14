using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Other;

/// <summary>
/// 系統例外紀錄的堆疊檔案存放。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>三條刪除路徑（刪單列、清空全部、清除舊紀錄）
/// 都必須經過這裡，不可有人自己去 <c>File.Delete</c>。
///
/// 沿革：專案附件曾因「資料表紀錄由 EF Cascade 處理、實體檔案卻要 Service 層自己記得刪」
/// 而留下孤兒檔（見 docs/features/檔案上傳機制.md）。本類別刻意把出入口收斂成一處，
/// 就是為了不重蹈覆轍。
///
/// 相對路徑格式為 <c>{yyyyMM}/{簽章前 16 碼}.txt</c>，年月目錄避免單一目錄檔案過多，
/// 與專案附件的既有慣例一致。
/// </summary>
public class ExceptionStackFileStore
{
    private readonly IOptions<SystemSettings> systemSettingsOptions;

    public ExceptionStackFileStore(IOptions<SystemSettings> systemSettingsOptions)
    {
        this.systemSettingsOptions = systemSettingsOptions;
    }

    private string RootPath => systemSettingsOptions.Value.ExternalFileSystem.ExceptionPath;

    /// <summary>
    /// 由簽章與首次發生時間組出相對路徑。純函式，不碰檔案系統，方便測試與事前計算。
    /// </summary>
    public static string BuildRelativePath(string signature, DateTime occurredAt)
    {
        var shortSignature = signature.Length > 16 ? signature[..16] : signature;
        return $"{occurredAt:yyyyMM}/{shortSignature}.txt";
    }

    /// <summary>
    /// 寫入堆疊全文。只在「首次建立資料列」時呼叫一次，重複發生不重寫。
    /// </summary>
    /// <returns>成功時回傳相對路徑；失敗時回傳 null（<b>不得</b>讓資料列因此建不起來）。</returns>
    public async Task<string?> WriteAsync(string signature, DateTime occurredAt, string content)
    {
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            return null;
        }

        var relativePath = BuildRelativePath(signature, occurredAt);

        try
        {
            var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) == false)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(fullPath, content);
            return relativePath;
        }
        catch (Exception)
        {
            // ⚠️ 這裡刻意不記日誌：本類別位在例外記錄管線之內，
            // 走 ILogger 會讓「寫檔失敗」再觸發一次記錄，形成遞迴。
            // 寫檔失敗只是損失堆疊全文，資料列仍必須建立。
            return null;
        }
    }

    /// <summary>讀取堆疊全文。檔案不存在或讀取失敗一律回傳 null，由呼叫端顯示友善訊息。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath) == false)
            {
                return null;
            }

            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>刪除單一堆疊檔。檔案不存在視為成功。</summary>
    public void Delete(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        try
        {
            var fullPath = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception)
        {
            // 同 WriteAsync：本類別在例外記錄管線內，不得走 ILogger。
        }
    }

    /// <summary>
    /// 清空整個堆疊目錄（供「清空全部」使用）。
    /// 刻意刪內容而非刪目錄本身，因為目錄由 Program.cs 啟動時建立，刪掉就不會再有人補。
    /// </summary>
    public void DeleteAll()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || Directory.Exists(RootPath) == false)
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
        catch (Exception)
        {
            // 同上。
        }
    }
}
