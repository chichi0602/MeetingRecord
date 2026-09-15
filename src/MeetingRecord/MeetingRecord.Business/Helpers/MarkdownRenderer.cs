using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MeetingRecord.Business.Helpers;

/// <summary>
/// 全站唯一的 Markdown → HTML 轉換點。畫面（AI 問答氣泡、會議紀錄草稿檢視）
/// 與 PDF 匯出<b>共用同一條管線</b>。
///
/// ⚠️ 不要為了「畫面比較危險」而另開一條管線。PDF 那條看似安全其實更危險：
/// <see cref="Services.Export.HeadlessBrowserPdfRenderer"/> 是叫無頭瀏覽器去讀
/// <c>file://</c> 的 HTML，<b>瀏覽器會執行 script</b>，而且是本機檔案來源。
/// 注入到會議紀錄草稿（使用者可自由編修）的內容會在伺服器上跑起來。
/// 兩條管線還一定會走鐘，所以只留一條。
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// 明列擴充而不是 <c>UseAdvancedExtensions()</c>。
    ///
    /// ⚠️ <c>UseAdvancedExtensions()</c> 的定義是「除了 BootStrap、Emoji、SmartyPants
    /// 與軟換行外<b>全部啟用</b>」，也就是<b>包含 GenericAttributes</b>——
    /// <c>## 標題 {onclick="alert(1)"}</c> 會直接產出 onclick 屬性，
    /// 而 <c>DisableHtml()</c> 擋不到它（那是屬性，不是 HTML 區塊）。
    ///
    /// 表格一定要留：提示詞範本由使用者自行撰寫、系統不規範 LLM 的輸出格式，
    /// 實務上很常出現表格（決議／負責人／期限）。少了它會原封不動印成 <c>|---|---|</c>。
    ///
    /// 刻意不含：GenericAttributes（見上）、MediaLinks 與 Diagrams（會產生
    /// iframe 或外連資源，這裡用不到）。
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        // CommonMark 的 emphasis 規則對中文不友善：**備註：**後面直接接中文字時，
        // 結尾的 ** 不算 right-flanking，整段會原樣印出來——正是使用者抱怨「看到 **」的其中一種。
        .UseCjkFriendlyEmphasis()
        .UseListExtras()
        .UseTaskLists()
        .UseFootnotes()
        .UseAutoIdentifiers()
        .UseAutoLinks()
        .UseDefinitionLists()
        .UseAbbreviations()
        .DisableHtml()
        .Build();

    /// <summary>連結允許的通訊協定。其餘一律換成 <c>#</c>。</summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>被擋下來的連結導向這裡——仍然看得到文字，只是點了不會做事。</summary>
    private const string BlockedUrl = "#";

    /// <summary>通訊協定的合法形狀（RFC 3986）：字母開頭，其後為字母、數字、<c>+ - .</c>。</summary>
    private static readonly Regex SchemePattern = new(@"^[a-zA-Z][a-zA-Z0-9+.\-]*$", RegexOptions.Compiled);

    /// <summary>把 Markdown 轉成可安全塞進 <c>MarkupString</c> 或列印用 HTML 的字串。</summary>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown, Pipeline);

        // 走訪 AST 而不是自訂 renderer 或 HtmlRenderer.LinkRewriter：
        // 這樣不必知道 Markdig 內部是哪個 renderer 處理哪種節點，涵蓋範圍可以直接看出來。
        // 兩種節點都要處理——<javascript:alert(1)> 這種 autolink 走的是 AutolinkInline，
        // 不是 LinkInline。
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case LinkInline link:
                    link.Url = SanitizeUrl(link.Url);
                    break;
                case AutolinkInline autolink:
                    autolink.Url = SanitizeUrl(autolink.Url);
                    break;
            }
        }

        return Markdown.ToHtml(document, Pipeline);
    }

    /// <summary>
    /// 只放行 http／https／mailto 與站內相對位址，其餘（javascript:、data:、vbscript:…）
    /// 換成 <see cref="BlockedUrl"/>。
    ///
    /// 抽成 internal static 純函式以便單元測試（本專案的既有慣例）。
    /// </summary>
    internal static string SanitizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BlockedUrl;
        }

        // 瀏覽器在判斷通訊協定前會把控制字元丟掉，所以 "java&#9;script:" 其實會被當成
        // javascript:。這裡先照做，否則白名單可以被夾一個 tab 就繞過。
        var cleaned = new string([.. url.Where(ch => !char.IsControl(ch))]).Trim();
        if (cleaned.Length == 0)
        {
            return BlockedUrl;
        }

        var colon = cleaned.IndexOf(':');
        if (colon <= 0)
        {
            // 沒有冒號就是相對位址；開頭就是冒號則不合法，當相對位址處理即可（點了不會離站）。
            return cleaned;
        }

        // 冒號出現在路徑、查詢或錨點之後，代表它是路徑的一部分而不是通訊協定，
        // 例如 "/a/b:c" 或 "#a:b"。
        var separator = cleaned.AsSpan(0, colon).IndexOfAny('/', '?', '#');
        if (separator >= 0)
        {
            return cleaned;
        }

        var scheme = cleaned[..colon];
        if (!SchemePattern.IsMatch(scheme))
        {
            return BlockedUrl;
        }

        return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase)
            ? cleaned
            : BlockedUrl;
    }
}
