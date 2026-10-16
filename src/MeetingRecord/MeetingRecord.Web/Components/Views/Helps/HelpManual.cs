using System.Text;
using System.Text.RegularExpressions;
using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Web.Components.Views.Helps;

/// <summary>使用說明的一個分類（左欄的一項）。</summary>
public sealed record HelpCategory(string Name, string Markdown, string Html);

/// <summary>
/// 搜尋的最小單位：一章的開頭段、一個 <c>###</c> 小節，或常見問題裡的一題。
/// </summary>
public sealed record HelpTopic(string Category, string Section, string Title, string Markdown, string Html)
{
    /// <summary>麵包屑：分類 › 章 › 小節（標題與章名相同時不重複）。</summary>
    public string Breadcrumb => Title == Section
        ? $"{Category} › {Section}"
        : $"{Category} › {Section} › {Title}";
}

/// <summary>
/// 把使用說明切成分類與搜尋單位（1.0.42）。
///
/// <para>
/// 內容來源仍是同一份 <c>docs/guides/系統使用說明.md</c>：每個 <c>##</c> 章節前一行有
/// <c>&lt;!-- 說明分類：XXX --&gt;</c> 標記，這裡依標記分組。GitHub 上看不到 HTML 註解，文件照樣好讀；
/// 標記行會在渲染前剝掉——<see cref="MarkdownRenderer"/> 關掉了 HTML，不剝會被原樣印成文字。
/// </para>
///
/// <para>純函式、不碰 IO，方便單元測試；畫面在 <see cref="HelpView"/>。</para>
/// </summary>
public sealed partial class HelpManual
{
    /// <summary>分類固定這 6 個、依這個順序（使用者選定）。說明檔的標記必須是其中之一，測試會擋。</summary>
    public static readonly IReadOnlyList<string> CategoryNames =
        ["新手入門", "會議與逐字稿", "專案與待辦", "AI 與費用", "管理員", "常見問題"];

    private HelpManual(IReadOnlyList<HelpCategory> categories, IReadOnlyList<HelpTopic> topics, IReadOnlyList<string> unknownMarkers)
    {
        Categories = categories;
        Topics = topics;
        UnknownMarkers = unknownMarkers;
    }

    public IReadOnlyList<HelpCategory> Categories { get; }

    public IReadOnlyList<HelpTopic> Topics { get; }

    /// <summary>沒有標記或標記不認得的章節名稱（歸到第一個分類）。正常情況是空的。</summary>
    public IReadOnlyList<string> UnknownMarkers { get; }

    /// <summary>解析已去掉檔頭的說明檔（見 <see cref="HelpView.StripDocumentFrontMatter"/>）。</summary>
    public static HelpManual Parse(string markdown)
    {
        var sections = SplitSections(markdown ?? string.Empty);
        var unknown = new List<string>();

        foreach (var section in sections.Where(x => !CategoryNames.Contains(x.Category)))
        {
            unknown.Add(section.Title);
        }

        var categories = CategoryNames
            .Select(name =>
            {
                var body = string.Join(
                    "\n\n",
                    sections
                        .Where(x => (CategoryNames.Contains(x.Category) ? x.Category : CategoryNames[0]) == name)
                        .Select(x => x.Markdown));

                return new HelpCategory(name, body, MarkdownRenderer.ToHtml(body));
            })
            .ToList();

        var topics = sections
            .SelectMany(section => SplitTopics(
                CategoryNames.Contains(section.Category) ? section.Category : CategoryNames[0],
                section))
            .ToList();

        return new HelpManual(categories, topics, unknown);
    }

    /// <summary>
    /// 搜尋：以空白切成多個關鍵字、不分大小寫、全部都要出現（標題＋內文）。
    /// 標題命中的排前面，其餘照文件順序。空白查詢回空清單（畫面回到分類瀏覽）。
    /// </summary>
    public IReadOnlyList<HelpTopic> Search(string? query)
    {
        var terms = SplitTerms(query);
        if (terms.Count == 0)
        {
            return [];
        }

        return Topics
            .Select((topic, index) => (topic, index))
            .Where(x => terms.All(term =>
                x.topic.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || x.topic.Markdown.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => terms.Any(term => x.topic.Title.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(x => x.index)
            .Select(x => x.topic)
            .ToList();
    }

    /// <summary>「管理員」分類的名稱（一般使用者問 AI 時不送這一類）。</summary>
    public const string AdminCategoryName = "管理員";

    /// <summary>
    /// 給 AI 助理（1.0.43）讀的說明全文：依分類順序串接（已剝掉分類標記）。
    /// 一般使用者不送「管理員」那一類（使用者決定）：本來就用不到那些功能，少送約四分之一也比較便宜，
    /// AI 也就不會回答出他們看不到的功能。
    /// </summary>
    public string BuildAssistantContext(bool includeAdmin)
        => string.Join(
            "\n\n",
            Categories
                .Where(x => includeAdmin || x.Name != AdminCategoryName)
                .Select(x => $"# 分類：{x.Name}\n\n{x.Markdown}"));

    /// <summary>給畫面高亮用的關鍵字清單（與 <see cref="Search"/> 同一套切法）。</summary>
    public static IReadOnlyList<string> SplitTerms(string? query)
        => (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// 錨點（<c>#id</c>）在哪個分類。說明檔裡有跨章節的連結，分類之後目標可能不在目前這一類，
    /// 要先切過去才捲得到。找不到回 null。
    /// </summary>
    public string? FindCategoryOfAnchor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var needle = $"id=\"{id}\"";
        return Categories.FirstOrDefault(x => x.Html.Contains(needle, StringComparison.Ordinal))?.Name;
    }

    private sealed record Section(string Category, string Title, string Markdown);

    /// <summary>依 <c>##</c> 切章，讀前一行的分類標記；圍欄（```）裡的行不算標題。</summary>
    private static List<Section> SplitSections(string markdown)
    {
        var sections = new List<Section>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        string? pendingCategory = null;
        string? currentCategory = null;
        string? currentTitle = null;
        var buffer = new StringBuilder();
        var inFence = false;

        void Flush()
        {
            if (currentTitle is not null)
            {
                sections.Add(new Section(currentCategory ?? string.Empty, currentTitle, TrimSection(buffer.ToString())));
            }

            buffer.Clear();
        }

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence)
            {
                var marker = CategoryMarkerPattern().Match(line);
                if (marker.Success)
                {
                    pendingCategory = marker.Groups["name"].Value.Trim();
                    continue;
                }

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    Flush();
                    currentTitle = line[3..].Trim();
                    currentCategory = pendingCategory ?? string.Empty;
                    pendingCategory = null;
                }
            }

            if (currentTitle is not null)
            {
                buffer.Append(line).Append('\n');
            }
        }

        Flush();
        return sections;
    }

    /// <summary>
    /// 一章切成搜尋單位：開頭段（標題＝章名）、每個 <c>###</c>（含其下的 <c>####</c>）；
    /// 小節裡以 <c>**Q：</c> 開頭的段落再各自切開——常見問題一題一張卡，標題用題目本身。
    /// </summary>
    private static IEnumerable<HelpTopic> SplitTopics(string category, Section section)
    {
        var chunks = new List<(string Title, StringBuilder Body)> { (section.Title, new StringBuilder()) };
        var inFence = false;

        foreach (var line in section.Markdown.Split('\n').Skip(1))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }

            if (!inFence && line.StartsWith("### ", StringComparison.Ordinal))
            {
                chunks.Add((line[4..].Trim(), new StringBuilder()));
            }
            else if (!inFence && line.StartsWith("**Q：", StringComparison.Ordinal))
            {
                chunks.Add((QuestionTitle(line), new StringBuilder()));
            }

            chunks[^1].Body.Append(line).Append('\n');
        }

        foreach (var (title, body) in chunks)
        {
            var text = TrimSection(body.ToString());

            // 章名底下馬上接 ### 的話，開頭段是空的，不要變成一張空卡片。
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // ### 那一行本身留在卡片裡當標題列；開頭段補上章名，卡片才知道在講什麼。
            var cardMarkdown = title == section.Title ? $"### {title}\n\n{text}" : text;
            yield return new HelpTopic(category, section.Title, title, cardMarkdown, MarkdownRenderer.ToHtml(cardMarkdown));
        }
    }

    private static string QuestionTitle(string line)
        => line.Trim().TrimStart('*').TrimEnd('*').Trim();

    /// <summary>去掉頭尾空行與章節之間的分隔線（<c>---</c>），分類串接時不會一章後面掛一條橫線。</summary>
    private static string TrimSection(string text)
    {
        var lines = text.Split('\n').ToList();

        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[^1]) || lines[^1].Trim() == "---"))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"^\s*<!--\s*說明分類：(?<name>[^>]+?)\s*-->\s*$")]
    private static partial Regex CategoryMarkerPattern();
}
