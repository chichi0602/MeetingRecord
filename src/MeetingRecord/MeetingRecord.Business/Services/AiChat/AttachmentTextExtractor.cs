using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.AiChat;

/// <summary>
/// 把專案附件的文字內容抽出來，供 AI 問答當作脈絡。
///
/// <para>
/// 只做「取得純文字」這一件事，不負責裁切長度（那是 <see cref="ChatContextBuilder"/> 的事），
/// 也不負責讀檔以外的商業邏輯。
/// </para>
///
/// <para>
/// 刻意不引 iText——它是 AGPL，用在非開源專案會有授權問題。
/// PdfPig 是 Apache-2.0、DocumentFormat.OpenXml 是 MIT。
/// </para>
/// </summary>
public class AttachmentTextExtractor
{
    /// <summary>PDF 每頁之間的接合字串。</summary>
    private const string PageSeparator = "\n\n";

    private readonly string projectFileRootPath;
    private readonly ILogger<AttachmentTextExtractor> logger;

    public AttachmentTextExtractor(
        IOptions<SystemSettings> systemSettings,
        ILogger<AttachmentTextExtractor> logger)
    {
        // 附件根目錄的來源與 ProjectService 相同，路徑解析收在這裡避免兩處各寫一份。
        projectFileRootPath = systemSettings.Value.ExternalFileSystem.ProjectFilePath;
        this.logger = logger;
    }

    /// <summary>
    /// 判斷這個副檔名是否有辦法擷取文字。抽成純函式以便單元測試——
    /// 不必為了測「.xlsx 不支援」而準備一個真的 Excel 檔。
    /// </summary>
    public static bool IsSupported(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" or ".docx" or ".txt" or ".md" or ".csv" => true,
            _ => false,
        };
    }

    /// <summary>
    /// 讀出附件的純文字。無法擷取時回傳 null——呼叫端要把它列進「已跳過」讓使用者看見，
    /// 不能默默當成沒有內容，否則使用者會以為 AI 讀過了卻答不出來。
    ///
    /// <para>
    /// 以下情況都會回 null：副檔名不支援（.doc、圖片、.xlsx…）、檔案不存在、
    /// 解析失敗，以及**掃描檔 PDF**——它有頁面但沒有文字層，擷取結果是空白。
    /// </para>
    /// </summary>
    public string? TryExtract(string relativePath, string originalFileName)
    {
        if (!IsSupported(originalFileName))
        {
            return null;
        }

        var fullPath = ResolveFullPath(relativePath);

        if (!File.Exists(fullPath))
        {
            logger.LogWarning("Attachment text extraction skipped because file is missing. Path={Path}", fullPath);
            return null;
        }

        try
        {
            var text = Path.GetExtension(originalFileName).ToLowerInvariant() switch
            {
                ".pdf" => ExtractPdf(fullPath),
                ".docx" => ExtractDocx(fullPath),
                _ => File.ReadAllText(fullPath, Encoding.UTF8),
            };

            if (string.IsNullOrWhiteSpace(text))
            {
                // 掃描檔 PDF 走到這裡：頁面是圖片，沒有文字層可取。
                logger.LogInformation(
                    "Attachment contains no extractable text (possibly a scanned document). FileName={FileName}",
                    originalFileName);

                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            // 壞掉的檔案不該讓整個問答失敗，記錄後當成「跳過」處理。
            logger.LogWarning(ex, "Attachment text extraction failed. FileName={FileName}", originalFileName);
            return null;
        }
    }

    /// <summary>比照 <c>ProjectService.GetFullPath</c>：分隔符正規化後併到附件根目錄下。</summary>
    private string ResolveFullPath(string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        return Path.Combine(projectFileRootPath, normalized);
    }

    private static string ExtractPdf(string fullPath)
    {
        using var document = PdfDocument.Open(fullPath);

        var pages = document.GetPages().Select(page => page.Text.Trim())
            .Where(pageText => pageText.Length > 0);

        return string.Join(PageSeparator, pages);
    }

    private static string ExtractDocx(string fullPath)
    {
        using var document = WordprocessingDocument.Open(fullPath, isEditable: false);

        // InnerText 會把整份文件的文字接成一串（含表格），足夠當作問答脈絡；
        // 不做段落還原——排版對 LLM 的理解幫助有限，卻要處理大量節點型別。
        return document.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
    }
}
