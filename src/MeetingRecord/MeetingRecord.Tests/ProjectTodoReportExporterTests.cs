using MeetingRecord.Business.Services.Export;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Tests;

/// <summary>
/// 專案待辦總覽（1.0.54）的 PDF 來源 HTML。
/// </summary>
public sealed class ProjectTodoReportExporterTests
{
    private static readonly DateTime ExportedAt = new(2026, 10, 22, 14, 5, 0);

    [Fact]
    public void BuildHtml_ShouldContainTitleMetaAndSections()
    {
        var html = ProjectTodoReportExporter.BuildHtml(Request([Todo(1, "寫規格")]));

        Assert.Contains("<h1 class=\"doc-title\">Q3 改版 待辦總覽</h1>", html);
        Assert.Contains("<h2>進度摘要</h2>", html);
        Assert.Contains("<h2>待辦明細</h2>", html);
        Assert.Contains("1.0.54", html);
        Assert.Contains("tester", html);
        Assert.Contains("王小明", html);
        Assert.Contains("2026/09/01～2026/12/31", html);
        Assert.Contains("2026/10/22 14:05", html);
    }

    [Fact]
    public void BuildHtml_ShouldCountStatusesIncludingSubtasks()
    {
        var todos = new List<TodoAdapterModel>
        {
            Todo(1, "一", status: "已完成"),
            Todo(2, "二", status: "進行中"),
            Todo(3, "三", dueDate: DateTime.Today.AddDays(-3)),
            Todo(4, "子", status: "已完成", parentId: 2),
        };

        var html = ProjectTodoReportExporter.BuildHtml(Request(todos));

        Assert.Contains("<td class=\"num\">4</td><td class=\"num\">2</td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">1</td><td class=\"num\">50%</td>", html);
    }

    [Fact]
    public void BuildHtml_CompletionRateShouldTruncate()
    {
        // 與 TodoOwnerSummary.CompletionPercent 一致：199/200 不能顯示成 100%。
        var todos = Enumerable.Range(1, 200)
            .Select(i => Todo(i, $"項目{i}", status: i == 200 ? "待辦" : "已完成"))
            .ToList();

        var html = ProjectTodoReportExporter.BuildHtml(Request(todos));

        Assert.Contains("<td class=\"num\">99%</td>", html);
        Assert.DoesNotContain("<td class=\"num\">100%</td>", html);
    }

    [Fact]
    public void BuildHtml_ShouldListCompletedAndPlaceSubtasksUnderTheirParent()
    {
        // 輸入順序：子項目排在最前面，輸出仍要接在主待辦後面。
        var todos = new List<TodoAdapterModel>
        {
            Todo(3, "子項目甲", parentId: 1, status: "已完成"),
            Todo(1, "主待辦"),
            Todo(2, "另一筆", status: "已完成"),
        };

        var html = ProjectTodoReportExporter.BuildHtml(Request(todos));

        var parentAt = html.IndexOf("主待辦", StringComparison.Ordinal);
        var childAt = html.IndexOf("子項目甲", StringComparison.Ordinal);
        var otherAt = html.IndexOf("另一筆", StringComparison.Ordinal);
        Assert.True(parentAt < childAt && childAt < otherAt);
        Assert.Contains("子項 1/1", html);
        Assert.Contains("class=\"done\"", html);
    }

    [Fact]
    public void BuildHtml_ShouldMarkOverdue()
    {
        var html = ProjectTodoReportExporter.BuildHtml(Request([Todo(1, "遲了", dueDate: DateTime.Today.AddDays(-5))]));

        Assert.Contains("class=\"overdue\"", html);
        Assert.Contains("<td class=\"num\">5 天</td>", html);
    }

    [Fact]
    public void BuildHtml_ShouldEscapeExternalStrings()
    {
        var todo = Todo(1, "<script>alert(1)</script>");
        todo.Owner = "<b>人</b>";
        todo.MeetingTitle = "<img src=x>";

        var html = ProjectTodoReportExporter.BuildHtml(Request([todo]));

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<b>人</b>", html);
        Assert.DoesNotContain("<img src=x>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void BuildHtml_ShouldSaySo_WhenThereAreNoTodos()
    {
        var html = ProjectTodoReportExporter.BuildHtml(Request([]));

        Assert.Contains("這個專案目前沒有待辦", html);
    }

    [Fact]
    public void BuildFileName_ShouldUseProjectTitleAndDate()
        => Assert.Equal("Q3_改版_待辦總覽_20261022.pdf", ProjectTodoReportExporter.BuildFileName(Project(), ExportedAt));

    private static ProjectTodoReportRequest Request(IReadOnlyList<TodoAdapterModel> todos)
        => new("1.0.54", "tester", ExportedAt, Project(), todos);

    private static ProjectAdapterModel Project() => new()
    {
        Id = 7,
        Title = "Q3 改版",
        Owner = "王小明",
        Status = "進行中",
        StartDate = new DateTime(2026, 9, 1),
        EndDate = new DateTime(2026, 12, 31),
        CompletionPercentage = 40,
    };

    private static TodoAdapterModel Todo(int id, string title, string status = "待辦", DateTime? dueDate = null, int? parentId = null) => new()
    {
        Id = id,
        Title = title,
        ProjectId = 7,
        Status = status,
        DueDate = dueDate,
        ParentTodoId = parentId,
    };
}
