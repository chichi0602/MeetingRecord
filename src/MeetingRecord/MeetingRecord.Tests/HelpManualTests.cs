using System.Reflection;
using System.Text;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Web.Components.Views.Helps;

namespace MeetingRecord.Tests;

/// <summary>
/// 使用說明的內嵌資源測試。
///
/// <para>
/// 守的是「一份原始檔、兩個出口」這個設計本身：<c>docs/guides/系統使用說明.md</c> 在建置時
/// 被嵌進 Web 組件，系統內的說明頁再把它渲染出來。csproj 的 <c>LogicalName</c> 與
/// <c>HelpView</c> 的資源名稱只要有一邊打錯，**畫面上只會永遠停在「載入中…」而不報錯**——
/// 這種靜默失效正是需要測試的。
/// </para>
/// </summary>
public sealed class HelpManualTests
{
    /// <summary>與 csproj 的 LogicalName、HelpView.ResourceName 三者必須一致。</summary>
    private const string ResourceName = "MeetingRecord.Web.系統使用說明.md";

    private static Assembly WebAssembly => typeof(HelpView).Assembly;

    private static string ReadManual()
    {
        using var stream = WebAssembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Manual_ShouldBeEmbeddedInWebAssembly()
    {
        var names = WebAssembly.GetManifestResourceNames();

        Assert.True(
            names.Contains(ResourceName),
            $"找不到內嵌的使用說明：{ResourceName}。" +
            $"請確認 MeetingRecord.Web.csproj 的 EmbeddedResource LogicalName 是否一致。" +
            $"目前的內嵌資源：{string.Join("、", names)}");
    }

    [Fact]
    public void Manual_ShouldNotStartWithByteOrderMark()
    {
        // ⚠️ docs/*.md 一律含 BOM（CI 強制）。沒吃掉的話第一個字元是 ﻿，
        // Markdig 會把第一行的「# 標題」當成普通段落，整頁標題層級就跑掉了。
        var manual = ReadManual();

        Assert.False(manual.StartsWith('﻿'), "使用說明的 BOM 沒有被去掉。");
    }

    [Fact]
    public void Manual_RenderedHtml_ShouldStartWithHeading()
    {
        // 這一筆是 BOM 那件事的實質驗收：BOM 還在的話這裡會是 <p> 而不是 <h1>。
        var html = MarkdownRenderer.ToHtml(ReadManual());

        Assert.StartsWith("<h1", html.TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_ShouldCoverThePaidActions()
    {
        // 使用說明最該講清楚的就是「哪些動作會花錢」。這裡不比對逐字內容
        // （那會讓文件每次潤稿都要改測試），只確認五條付費路徑都被提到。
        var manual = ReadManual();

        foreach (var keyword in new[] { "重新轉錄", "AI 轉會議紀錄", "抽出待辦", "AI 問答", "費用" })
        {
            Assert.True(manual.Contains(keyword, StringComparison.Ordinal), $"使用說明沒有提到「{keyword}」。");
        }
    }

    [Fact]
    public void StripFrontMatter_ShouldRemoveMaintainerHeader()
    {
        // 表頭（文件版本／最後核對日期）與那段「這個檔案也是說明頁的來源」是寫給維護者的，
        // 使用者看了只會困惑，所以畫面上要切掉——但檔案裡要留著，文件庫的慣例不破。
        var shown = HelpView.StripDocumentFrontMatter(ReadManual());

        Assert.DoesNotContain("文件版本", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("最後核對日期", shown, StringComparison.Ordinal);
        Assert.StartsWith("## ", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void StripFrontMatter_WithoutSeparator_ShouldReturnAsIs()
    {
        // 格式改了就整頁空白是最糟的失敗方式。找不到分隔線時寧可多顯示一段表頭。
        const string noSeparator = "# 標題\n\n內文";

        Assert.Equal(noSeparator, HelpView.StripDocumentFrontMatter(noSeparator));
    }

    [Fact]
    public void Manual_RenderedHtml_ShouldContainTables()
    {
        // 說明裡用表格列付費動作與快捷鍵。Markdig 的表格擴充沒開的話會變成一堆直線，
        // 而 MarkdownRenderer 是全站共用的管線——這裡順帶守住它。
        var html = MarkdownRenderer.ToHtml(ReadManual());

        Assert.Contains("<table>", html, StringComparison.Ordinal);
    }
}
