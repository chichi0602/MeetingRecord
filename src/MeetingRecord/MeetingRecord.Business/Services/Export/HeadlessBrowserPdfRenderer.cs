using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Business.Services.Export;

/// <summary>把一份 HTML 算成 PDF。</summary>
public interface IPdfRenderer
{
    /// <summary>回傳 PDF 位元組；失敗時擲出 <see cref="InvalidOperationException"/>。</summary>
    Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default);
}

/// <summary>
/// 以系統既有的 Edge／Chrome 無頭列印產生 PDF，結構比照
/// <see cref="Transcription.FfmpegMediaConverter"/>（暫存工作目錄 → 跑外部程序 → 檢查結果 → 清理）。
///
/// <para>
/// 為什麼不用純 .NET 的 PDF 套件：中文 PDF 必須嵌入 CJK 字型（TrueType subsetting、CID font、
/// CMap），要嘛把 10MB 以上的字型檔放進版控，要嘛買商業授權，而且複雜的 Markdown（尤其是表格）
/// 還得自己寫排版。交給瀏覽器則三者全免——中文用系統字型、表格與分頁由排版引擎處理。
/// 代價是依賴機器上裝有 Edge 或 Chrome，這在本專案的 Windows 單機部署假設下成立。
/// </para>
/// </summary>
public class HeadlessBrowserPdfRenderer : IPdfRenderer
{
    /// <summary>單次列印的逾時上限。只渲染一份單頁文件，不需要像轉檔那麼寬鬆。</summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(2);

    private readonly string? configuredBrowserPath;
    private readonly ILogger<HeadlessBrowserPdfRenderer> logger;

    public HeadlessBrowserPdfRenderer(
        IOptions<ExportSettings> exportSettings,
        ILogger<HeadlessBrowserPdfRenderer> logger)
    {
        configuredBrowserPath = exportSettings.Value.BrowserPath;
        this.logger = logger;
    }

    public async Task<byte[]> RenderAsync(string html, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);

        var browserPath = BrowserPathResolver.Resolve(configuredBrowserPath)
            ?? throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(configuredBrowserPath)
                    ? "找不到可用來產生 PDF 的瀏覽器。請安裝 Microsoft Edge 或 Google Chrome，"
                      + $"或於 {ExportSettings.SectionName}:BrowserPath 指定執行檔路徑。"
                    : $"{ExportSettings.SectionName}:BrowserPath 指向的瀏覽器執行檔不存在（{configuredBrowserPath}）。");

        var workingDirectory = Path.Combine(Path.GetTempPath(), "MeetingRecord", "pdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var inputPath = Path.Combine(workingDirectory, "document.html");
            var outputPath = Path.Combine(workingDirectory, "document.pdf");
            var profileDirectory = Path.Combine(workingDirectory, "profile");

            // 瀏覽器讀檔時不看 BOM 也沒關係——HTML 內已宣告 charset=utf-8。
            await File.WriteAllTextAsync(inputPath, html, new UTF8Encoding(false), cancellationToken);

            var arguments = BuildArguments(inputPath, outputPath, profileDirectory);
            await RunBrowserAsync(browserPath, arguments, cancellationToken);

            // 離開碼 0 不代表真的有輸出：瀏覽器在某些情況會直接結束而不列印，
            // 所以一定要確認檔案存在且非空。
            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    "瀏覽器沒有輸出 PDF 檔。若機器上已開著同一個瀏覽器，請確認 --user-data-dir 有生效。");
            }

            var pdf = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (pdf.Length == 0)
            {
                throw new InvalidOperationException("瀏覽器輸出的 PDF 是空檔案。");
            }

            logger.LogInformation("Rendered PDF via headless browser. Browser={Browser}, Bytes={Bytes}", browserPath, pdf.Length);
            return pdf;
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// 組出無頭列印的命令列參數。抽成純函式以便單元測試。
    /// </summary>
    /// <param name="profileDirectory">
    /// 專屬的 user data 目錄。<b>這個參數是必要的</b>——使用者的瀏覽器通常正開著，
    /// 不隔離 profile 的話新程序會直接附掛到既有實例，然後什麼都不印就結束（離開碼還是 0）。
    /// </param>
    internal static string BuildArguments(string inputPath, string outputPath, string profileDirectory)
    {
        return string.Join(' ',
            "--headless=new",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            Quote($"--user-data-dir={profileDirectory}"),
            "--no-pdf-header-footer",
            Quote($"--print-to-pdf={outputPath}"),
            Quote(new Uri(inputPath).AbsoluteUri));
    }

    private static string Quote(string value) => $"\"{value}\"";

    private async Task RunBrowserAsync(string browserPath, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"無法啟動瀏覽器產生 PDF（{browserPath}）。", ex);
        }

        var standardError = new StringBuilder();
        var errorTask = ReadAllAsync(process.StandardError, standardError, cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RenderTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await Task.WhenAll(errorTask, outputTask);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"瀏覽器產生 PDF 失敗（結束代碼 {process.ExitCode}）：{standardError.ToString().Trim()}");
        }
    }

    private static async Task ReadAllAsync(StreamReader reader, StringBuilder target, CancellationToken cancellationToken)
    {
        var text = await reader.ReadToEndAsync(cancellationToken);
        target.Append(text);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 清理失敗不該蓋掉原本的逾時例外。
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up PDF working directory. Directory={Directory}", directory);
        }
    }
}
