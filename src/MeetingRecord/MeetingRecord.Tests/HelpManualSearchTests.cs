using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MeetingRecord.Web.Components.Views.Helps;

namespace MeetingRecord.Tests;

/// <summary>
/// 使用說明的分類與搜尋（1.0.42）。使用者回報「全部在同一頁很亂，找不到自己遇到的問題」。
/// 前半段用真的說明檔，守住「每章都有分類標記」「跨章節連結還連得到」這類改文件就會悄悄壞掉的事。
/// </summary>
public sealed partial class HelpManualSearchTests
{
    private const string ResourceName = "MeetingRecord.Web.系統使用說明.md";

    private static string ReadManualBody()
    {
        using var stream = typeof(HelpView).Assembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return HelpView.StripDocumentFrontMatter(reader.ReadToEnd());
    }

    private static readonly Lazy<HelpManual> RealManual = new(() => HelpManual.Parse(ReadManualBody()));

    [Fact]
    public void RealManual_ShouldHaveSixCategoriesInOrder_AllNonEmpty()
    {
        var manual = RealManual.Value;

        Assert.Equal(
            ["新手入門", "會議與逐字稿", "專案與待辦", "AI 與費用", "管理員", "常見問題"],
            manual.Categories.Select(x => x.Name));
        Assert.All(manual.Categories, category => Assert.False(string.IsNullOrWhiteSpace(category.Markdown), $"「{category.Name}」是空的。"));
    }

    [Fact]
    public void RealManual_EverySectionShouldHaveValidCategoryMarker()
    {
        // 新增 ## 章節卻忘了加 <!-- 說明分類：XXX -->，畫面不會壞，但章節會跑到「新手入門」。
        Assert.True(
            RealManual.Value.UnknownMarkers.Count == 0,
            $"這些章節沒有合法的分類標記：{string.Join("、", RealManual.Value.UnknownMarkers)}");
    }

    [Fact]
    public void RealManual_RenderedHtmlShouldNotShowMarkers()
    {
        // 渲染管線關掉了 HTML，標記沒剝掉的話會原樣印成文字。
        Assert.All(RealManual.Value.Categories, category => Assert.DoesNotContain("說明分類", category.Html));
        Assert.All(RealManual.Value.Topics, topic => Assert.DoesNotContain("說明分類", topic.Html));
    }

    [Fact]
    public void RealManual_EveryInternalAnchorShouldResolveToACategory()
    {
        // 說明檔裡有跨章節連結；分類之後要先切到目標所在的分類，找不到就是連結壞了（多半是改了標題）。
        var anchors = AnchorLinkPattern().Matches(ReadManualBody()).Select(m => m.Groups["id"].Value).Distinct().ToList();

        Assert.NotEmpty(anchors);
        Assert.All(anchors, id => Assert.NotNull(RealManual.Value.FindCategoryOfAnchor(id)));
    }

    [Fact]
    public void RealManual_SearchTranscriptionFailure_ShouldFindFaqQuestion()
    {
        var results = RealManual.Value.Search("轉錄失敗");

        Assert.Contains(results, topic => topic.Category == "常見問題" && topic.Title.Contains("轉錄失敗了怎麼辦"));
    }

    [Fact]
    public void BuildAssistantContext_NonAdmin_ShouldExcludeAdminCategory()
    {
        // 1.0.43：一般使用者問 AI 時不送「管理員」那一類（使用者決定）。
        var forUser = RealManual.Value.BuildAssistantContext(includeAdmin: false);
        var forAdmin = RealManual.Value.BuildAssistantContext(includeAdmin: true);

        Assert.DoesNotContain("## 監控與診斷（管理員）", forUser);
        Assert.Contains("## 監控與診斷（管理員）", forAdmin);
        Assert.Contains("常見問題", forUser);
        Assert.True(forUser.Length < forAdmin.Length);
        Assert.DoesNotContain("說明分類", forUser);
        Assert.DoesNotContain("說明分類", forAdmin);
    }

    private const string Sample = """
        <!-- 說明分類：新手入門 -->
        ## 開始

        開頭說明。

        ### 第一次登入

        先改密碼。

        ---

        <!-- 說明分類：常見問題 -->
        ## 常見問題 FAQ

        ### 轉錄

        **Q：轉錄失敗了怎麼辦？**

        A：按重新轉錄。

        **Q：可以下載 Excel 嗎？**

        A：目前不行。

        ```text
        ## 這行在程式碼區塊裡，不是章節
        ```
        """;

    [Fact]
    public void Parse_ShouldSplitByCategoryAndSkipHeadingsInsideFence()
    {
        var manual = HelpManual.Parse(Sample);

        Assert.Contains("## 開始", manual.Categories[0].Markdown);
        Assert.Contains("## 常見問題 FAQ", manual.Categories[^1].Markdown);
        Assert.Contains("## 這行在程式碼區塊裡", manual.Categories[^1].Markdown);
        Assert.Empty(manual.UnknownMarkers);
        Assert.DoesNotContain(manual.Topics, topic => topic.Section.Contains("程式碼區塊"));
    }

    [Fact]
    public void Parse_ShouldDropTrailingSeparatorBetweenSections()
        => Assert.False(HelpManual.Parse(Sample).Categories[0].Markdown.TrimEnd().EndsWith("---"));

    [Fact]
    public void Parse_FaqQuestionsShouldBecomeOwnTopics()
    {
        var titles = HelpManual.Parse(Sample).Topics.Select(x => x.Title).ToList();

        Assert.Contains("Q：轉錄失敗了怎麼辦？", titles);
        Assert.Contains("Q：可以下載 Excel 嗎？", titles);
    }

    [Fact]
    public void Search_ShouldRequireAllTermsAndIgnoreCase()
    {
        var manual = HelpManual.Parse(Sample);

        Assert.Equal(["Q：可以下載 Excel 嗎？"], manual.Search("excel 下載").Select(x => x.Title));
        Assert.Empty(manual.Search("excel 轉錄失敗"));
    }

    [Fact]
    public void Search_TitleMatchesShouldComeFirst()
    {
        var results = HelpManual.Parse(Sample).Search("轉錄");

        Assert.Contains("轉錄", results[0].Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Search_BlankQueryShouldReturnNothing(string? query)
        => Assert.Empty(HelpManual.Parse(Sample).Search(query));

    [Fact]
    public void Breadcrumb_ShouldNotRepeatSectionTitle()
    {
        var topics = HelpManual.Parse(Sample).Topics;

        Assert.Contains(topics, x => x.Breadcrumb == "新手入門 › 開始");
        Assert.Contains(topics, x => x.Breadcrumb == "新手入門 › 開始 › 第一次登入");
    }

    [Fact]
    public void Parse_UnknownMarker_ShouldFallBackToFirstCategoryAndBeReported()
    {
        var manual = HelpManual.Parse("<!-- 說明分類：不存在 -->\n## 孤兒章節\n\n內容");

        Assert.Equal(["孤兒章節"], manual.UnknownMarkers);
        Assert.Contains("孤兒章節", manual.Categories[0].Markdown);
    }

    [GeneratedRegex(@"\]\(#(?<id>[^)]+)\)")]
    private static partial Regex AnchorLinkPattern();
}
