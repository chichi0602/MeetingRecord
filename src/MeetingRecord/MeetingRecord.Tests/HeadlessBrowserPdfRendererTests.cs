using MeetingRecord.Business.Services.Export;

namespace MeetingRecord.Tests;

/// <summary>
/// 無頭瀏覽器 PDF 產生器的單元測試。只測組命令列參數的純函式——
/// 實際啟動瀏覽器屬於整合行為，不放在單元測試裡。
/// </summary>
public sealed class HeadlessBrowserPdfRendererTests
{
    private const string Input = @"C:\temp\work\document.html";
    private const string Output = @"C:\temp\work\document.pdf";
    private const string Profile = @"C:\temp\work\profile";

    [Fact]
    public void BuildArguments_ShouldRunHeadlessAndPrintToPdf()
    {
        var arguments = HeadlessBrowserPdfRenderer.BuildArguments(Input, Output, Profile);

        Assert.Contains("--headless=new", arguments);
        Assert.Contains($"\"--print-to-pdf={Output}\"", arguments);
    }

    [Fact]
    public void BuildArguments_ShouldIsolateUserDataDirectory()
    {
        // 這個參數不能少：使用者的瀏覽器通常正開著，不隔離 profile 的話
        // 新程序會附掛到既有實例，然後什麼都不印就結束（離開碼還是 0）。
        var arguments = HeadlessBrowserPdfRenderer.BuildArguments(Input, Output, Profile);

        Assert.Contains($"\"--user-data-dir={Profile}\"", arguments);
    }

    [Fact]
    public void BuildArguments_ShouldPassInputAsFileUri()
    {
        var arguments = HeadlessBrowserPdfRenderer.BuildArguments(Input, Output, Profile);

        Assert.Contains("\"file:///C:/temp/work/document.html\"", arguments);
    }

    [Fact]
    public void BuildArguments_ShouldSuppressHeaderFooter()
    {
        // 預設會在每頁印上網址與日期，對會議紀錄來說是雜訊。
        var arguments = HeadlessBrowserPdfRenderer.BuildArguments(Input, Output, Profile);

        Assert.Contains("--no-pdf-header-footer", arguments);
    }

    [Fact]
    public void BuildArguments_ShouldQuotePathsContainingSpaces()
    {
        var arguments = HeadlessBrowserPdfRenderer.BuildArguments(
            @"C:\my temp\document.html",
            @"C:\my temp\document.pdf",
            @"C:\my temp\profile");

        Assert.Contains(@"""--print-to-pdf=C:\my temp\document.pdf""", arguments);
        Assert.Contains(@"""--user-data-dir=C:\my temp\profile""", arguments);
    }
}
