using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Web.Components.Views.Todos;

namespace MeetingRecord.Tests;

/// <summary>清單多選批次動作（1.0.53）：逐筆依序、失敗不中斷、結果文字；待辦的主待辦／子項目去重。</summary>
public sealed class BatchDeleteTests
{
    [Fact]
    public async Task RunAsync_RunsSequentially_AndKeepsGoingAfterFailuresAndExceptions()
    {
        var order = new List<int>();
        var running = 0;
        var maxConcurrent = 0;

        var result = await BatchRunner.RunAsync([1, 2, 3, 4], x => $"第{x}筆", async x =>
        {
            running++;
            maxConcurrent = Math.Max(maxConcurrent, running);
            await Task.Yield();
            order.Add(x);
            running--;
            return x switch
            {
                2 => "背景工作進行中",
                3 => throw new InvalidOperationException("爆了"),
                _ => null,
            };
        });

        Assert.Equal([1, 2, 3, 4], order);
        Assert.Equal(1, maxConcurrent);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal([new BatchFailure("第2筆", "背景工作進行中"), new BatchFailure("第3筆", "爆了")], result.Failures);
    }

    [Fact]
    public void Summary_AllSucceeded_PartialFailure_AndTruncation()
    {
        Assert.Equal("已刪除 3 筆。", new BatchResult(3, []).Summary("刪除"));
        Assert.Equal(
            "已刪除 1 筆，1 筆沒有刪除：A（找不到）。",
            new BatchResult(1, [new BatchFailure("A", "找不到")]).Summary("刪除"));

        var many = Enumerable.Range(1, 7).Select(i => new BatchFailure($"F{i}", "x")).ToList();
        var summary = new BatchResult(0, many).Summary("從專案移除");
        Assert.StartsWith("已從專案移除 0 筆，7 筆沒有從專案移除：F1（x）、F2（x）、F3（x）、F4（x）、F5（x）等 7 筆。", summary);
        Assert.DoesNotContain("F6", summary);
    }

    [Fact]
    public void Todo_ExcludeChildrenOfSelectedParents()
    {
        var parent = new TodoAdapterModel { Id = 1, Title = "主" };
        var child = new TodoAdapterModel { Id = 2, Title = "子", ParentTodoId = 1 };
        var orphanChild = new TodoAdapterModel { Id = 3, Title = "別人的子項目", ParentTodoId = 9 };

        var items = TodoBatchSelection.ExcludeChildrenOfSelectedParents([child, parent, orphanChild, parent]);

        Assert.Equal([1, 3], items.Select(x => x.Id).Order().ToList());
    }

    [Fact]
    public void Todo_Flatten_IncludesExpandedChildren()
    {
        var rows = new List<TodoAdapterModel>
        {
            new() { Id = 1, Children = [new TodoAdapterModel { Id = 2, ParentTodoId = 1 }] },
            new() { Id = 3 },
        };

        Assert.Equal([1, 2, 3], TodoBatchSelection.Flatten(rows).Select(x => x.Id).ToList());
    }
}
