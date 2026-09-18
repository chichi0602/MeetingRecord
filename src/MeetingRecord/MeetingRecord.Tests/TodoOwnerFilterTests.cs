using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Web.Components.Views.Todos;

namespace MeetingRecord.Tests;

/// <summary>
/// 負責人面板的膠囊篩選（0.4.85）。
///
/// <para>
/// 膠囊上的數字與點下去看到的清單是**兩份各自獨立的實作**：
/// 數字來自 <c>TodoService.GetOwnerSummariesAsync</c>（在查詢端數），
/// 清單來自 <see cref="TodoOwnerFilter.Apply"/>（用 <c>TodoAdapterModel.IsOverdue</c>）。
/// 兩邊對不上，使用者就會看到「逾期 3」點下去只有 2 筆——這種錯不會丟例外，
/// 只會讓人覺得數字有問題。所以逾期那條規則在這裡測得特別細。
/// </para>
///
/// <para>
/// ⚠️ 本專案沒有 bUnit，所以判斷邏輯抽成純函式才測得到；留在 <c>.razor.cs</c> 裡就只能靠眼睛驗。
/// </para>
/// </summary>
public sealed class TodoOwnerFilterTests
{
    #region Apply

    [Fact]
    public void Apply_All_ShouldReturnEverything()
    {
        var todos = BuildSample();

        Assert.Equal(todos.Count, TodoOwnerFilter.Apply(todos, OwnerTodoFilter.All).Count);
    }

    [Theory]
    [InlineData(OwnerTodoFilter.Pending, "待辦")]
    [InlineData(OwnerTodoFilter.InProgress, "進行中")]
    [InlineData(OwnerTodoFilter.Completed, "已完成")]
    public void Apply_ShouldReturnOnlyThatStatus(OwnerTodoFilter filter, string expectedStatus)
    {
        var result = TodoOwnerFilter.Apply(BuildSample(), filter);

        Assert.NotEmpty(result);
        Assert.All(result, x => Assert.Equal(expectedStatus, x.Status));
    }

    [Fact]
    public void Apply_Overdue_ShouldExcludeCompleted_EvenWhenDueDateIsPast()
    {
        // ⚠️ 這是整組最重要的一條。「已完成」永遠不算逾期（TodoAdapterModel.IsOverdue 的定義），
        // 否則已經做完的事會一直出現在逾期清單裡，而膠囊上的數字又不含它們——兩邊就對不上了。
        var todos = new List<TodoAdapterModel>
        {
            Todo("做完但早就過期", "已完成", DateTime.Today.AddDays(-10)),
            Todo("真的逾期", "進行中", DateTime.Today.AddDays(-1)),
        };

        var result = TodoOwnerFilter.Apply(todos, OwnerTodoFilter.Overdue);

        Assert.Equal("真的逾期", Assert.Single(result).Title);
    }

    [Fact]
    public void Apply_Overdue_ShouldExcludeDueToday()
    {
        // 判準是 DueDate < Today，今天到期還沒逾期。
        var todos = new List<TodoAdapterModel> { Todo("今天到期", "待辦", DateTime.Today) };

        Assert.Empty(TodoOwnerFilter.Apply(todos, OwnerTodoFilter.Overdue));
    }

    [Fact]
    public void Apply_Overdue_ShouldExcludeNullDueDate()
    {
        var todos = new List<TodoAdapterModel> { Todo("沒有截止日", "待辦", dueDate: null) };

        Assert.Empty(TodoOwnerFilter.Apply(todos, OwnerTodoFilter.Overdue));
    }

    [Fact]
    public void Apply_EmptySource_ShouldNotThrow()
    {
        Assert.Empty(TodoOwnerFilter.Apply([], OwnerTodoFilter.Completed));
    }

    #endregion

    #region Clamp

    [Fact]
    public void Clamp_ShouldResetToAll_WhenSelectedPillIsZero()
    {
        // 不變式：作用中的篩選，畫面上一定要有一顆可以點掉它的膠囊。
        // 0 筆的膠囊不可點（逾期那顆甚至整個不渲染），篩選留著就會變成
        // 「空清單，而且畫面上沒有任何東西可以按掉它」。
        var summary = Summary(completed: 3, inProgress: 0, pending: 2, overdue: 0);

        Assert.Equal(OwnerTodoFilter.All, TodoOwnerFilter.Clamp(summary, OwnerTodoFilter.InProgress));
        Assert.Equal(OwnerTodoFilter.All, TodoOwnerFilter.Clamp(summary, OwnerTodoFilter.Overdue));
    }

    [Fact]
    public void Clamp_ShouldKeepFilter_WhenCountIsPositive()
    {
        var summary = Summary(completed: 3, inProgress: 1, pending: 2, overdue: 1);

        Assert.Equal(OwnerTodoFilter.Completed, TodoOwnerFilter.Clamp(summary, OwnerTodoFilter.Completed));
        Assert.Equal(OwnerTodoFilter.Overdue, TodoOwnerFilter.Clamp(summary, OwnerTodoFilter.Overdue));
    }

    [Fact]
    public void Clamp_ShouldResetToAll_WhenSummaryIsNull()
    {
        // 換專案之後這個範圍裡可能一個負責人都沒有。
        Assert.Equal(OwnerTodoFilter.All, TodoOwnerFilter.Clamp(null, OwnerTodoFilter.Completed));
    }

    [Fact]
    public void Clamp_All_ShouldStayAll()
    {
        Assert.Equal(OwnerTodoFilter.All, TodoOwnerFilter.Clamp(null, OwnerTodoFilter.All));
        Assert.Equal(
            OwnerTodoFilter.All,
            TodoOwnerFilter.Clamp(Summary(0, 0, 0, 0), OwnerTodoFilter.All));
    }

    #endregion

    #region 測試資料

    private static TodoAdapterModel Todo(string title, string status, DateTime? dueDate) => new()
    {
        Title = title,
        Status = status,
        DueDate = dueDate,
    };

    private static List<TodoAdapterModel> BuildSample() =>
    [
        Todo("尚未開始", "待辦", null),
        Todo("做到一半", "進行中", null),
        Todo("已經做完", "已完成", null),
        Todo("逾期未完成", "進行中", DateTime.Today.AddDays(-3)),
    ];

    private static TodoOwnerSummary Summary(int completed, int inProgress, int pending, int overdue)
        => new("王小明", completed + inProgress + pending, completed, inProgress, pending, overdue);

    #endregion
}
