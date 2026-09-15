using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 共用 Markdown 渲染器的單元測試。
///
/// 這裡有一半是安全測試：渲染結果會塞進 <c>MarkupString</c>（畫面）與交給無頭瀏覽器
/// 以 file:// 載入（PDF），兩邊都會把 HTML 當真。內容來源是 LLM 輸出與使用者
/// 可自由編修的草稿，都不可信。
/// </summary>
public sealed class MarkdownRendererTests
{
    #region 基本渲染

    [Fact]
    public void ToHtml_ShouldRenderHeadings()
    {
        var html = MarkdownRenderer.ToHtml("## 會議決議");

        Assert.Contains("<h2", html);
        Assert.Contains("會議決議", html);
    }

    [Fact]
    public void ToHtml_ShouldRenderPipeTables()
    {
        // 表格是這條管線最容易被砍掉又最有感的擴充：提示詞範本由使用者自行撰寫，
        // LLM 很愛用表格列決議／負責人／期限。
        var markdown = """
            | 決議 | 負責人 |
            | --- | --- |
            | 改版 | 王小明 |
            """;

        var html = MarkdownRenderer.ToHtml(markdown);

        Assert.Contains("<table", html);
        Assert.Contains("<td>改版</td>", html);
    }

    [Fact]
    public void ToHtml_ShouldRenderTaskLists()
    {
        var html = MarkdownRenderer.ToHtml("- [x] 已完成");

        Assert.Contains("type=\"checkbox\"", html);
    }

    [Fact]
    public void ToHtml_ShouldRenderEmphasisAdjacentToChineseText()
    {
        // CommonMark 的 emphasis 規則對中文不友善：結尾的 ** 後面直接接中文字時不算
        // right-flanking，整段會原樣印出來。實跑既有對話時就踩到這個——UseCjkFriendlyEmphasis 才修掉。
        var html = MarkdownRenderer.ToHtml("**備註：**兩份紀錄對篩選碼的寫法不同");

        Assert.Contains("<strong>備註：</strong>", html);
        Assert.DoesNotContain("**", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ToHtml_ShouldReturnEmptyForNoContent(string? markdown)
    {
        Assert.Equal(string.Empty, MarkdownRenderer.ToHtml(markdown));
    }

    #endregion

    #region 安全：原始 HTML

    [Fact]
    public void ToHtml_ShouldNotEmitRawScriptTag()
    {
        var html = MarkdownRenderer.ToHtml("<script>alert(1)</script>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_ShouldEscapeRawHtmlInsteadOfExecutingIt()
    {
        // DisableHtml() 的行為是**逸出**而不是丟棄：使用者會在畫面上看到標籤的字面文字，
        // 但瀏覽器不會把它當成元素。所以斷言要看「有沒有產生元素」，不是「文字裡有沒有那串字」。
        var html = MarkdownRenderer.ToHtml("<img src=x onerror=\"alert(1)\">");

        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;img", html);
    }

    [Fact]
    public void ToHtml_ShouldNotEmitGenericAttributes()
    {
        // ⚠️ 這是 UseAdvancedExtensions() 會開的 GenericAttributes 擴充留下的洞：
        // DisableHtml() 擋不到它，因為那是屬性不是 HTML 區塊。本管線刻意不啟用，
        // 所以 {...} 會原樣留在標題文字裡，而不是變成 <h1 onclick="...">。
        var html = MarkdownRenderer.ToHtml("# 標題 {onclick=\"alert(1)\"}");

        Assert.DoesNotContain(" onclick=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{onclick=", html);
    }

    #endregion

    #region 安全：連結通訊協定

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///C:/Windows/System32")]
    public void ToHtml_ShouldBlockDangerousLinkSchemes(string url)
    {
        var html = MarkdownRenderer.ToHtml($"[點我]({url})");

        Assert.Contains("href=\"#\"", html);
        Assert.Contains("點我", html);
    }

    [Fact]
    public void ToHtml_ShouldBlockDangerousAutolinkSchemes()
    {
        // <javascript:…> 是合法的 CommonMark autolink，走的是 AutolinkInline
        // 而不是 LinkInline——只處理後者會漏掉這條路。
        var html = MarkdownRenderer.ToHtml("<javascript:alert(1)>");

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToHtml_ShouldBlockDangerousImageSchemes()
    {
        var html = MarkdownRenderer.ToHtml("![圖](javascript:alert(1))");

        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://example.com")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("/projects/3")]
    [InlineData("#fn-1")]
    [InlineData("../a/b")]
    public void ToHtml_ShouldKeepSafeUrls(string url)
    {
        var html = MarkdownRenderer.ToHtml($"[連結]({url})");

        Assert.Contains($"href=\"{url}\"", html);
    }

    [Fact]
    public void ToHtml_ShouldKeepFootnoteAnchors()
    {
        // 錨點若被當成「不明通訊協定」擋掉，footnote 與自動產生的標題 id 會全部斷掉。
        var markdown = """
            內文[^1]

            [^1]: 註解
            """;

        var html = MarkdownRenderer.ToHtml(markdown);

        Assert.Contains("href=\"#", html);
        Assert.DoesNotContain("href=\"#\"", html);
    }

    #endregion

    #region SanitizeUrl 純函式

    [Theory]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("  javascript:alert(1)  ")]
    [InlineData("\u0000javascript:alert(1)")]
    public void SanitizeUrl_ShouldStripControlCharactersBeforeCheckingScheme(string url)
    {
        // 瀏覽器在判斷通訊協定前會丟掉控制字元，所以夾一個 tab 就能繞過的白名單等於沒有。
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeUrl_ShouldBlockEmptyUrls(string? url)
    {
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl(url));
    }

    [Fact]
    public void SanitizeUrl_ShouldTreatColonAfterPathSeparatorAsRelative()
    {
        // "/a/b:c" 的冒號屬於路徑，不是通訊協定。
        Assert.Equal("/a/b:c", MarkdownRenderer.SanitizeUrl("/a/b:c"));
        Assert.Equal("#a:b", MarkdownRenderer.SanitizeUrl("#a:b"));
    }

    [Fact]
    public void SanitizeUrl_ShouldBlockMalformedScheme()
    {
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl("1abc:payload"));
    }

    #endregion
}
