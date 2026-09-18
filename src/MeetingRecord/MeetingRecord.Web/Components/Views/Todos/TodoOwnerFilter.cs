using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Web.Components.Views.Todos;

/// <summary>
/// 負責人面板的膠囊篩選條件。
///
/// <para>
/// ⚠️ 刻意用 enum 而不是 <c>string?</c> 存狀態值：<b>「逾期」不是 <c>Status</c> 的值</b>，
/// 它是 <c>DueDate</c> 加上「未完成」算出來的衍生條件。用字串就得塞一個不在
/// <c>TodoAdapterModel.StatusOptions</c> 裡的魔術字串，而 <c>todo.Status == filter</c>
/// 這種比對會**安靜地**永遠不成立——沒有例外、沒有紅字，只有一個永遠空的清單。
/// </para>
/// </summary>
public enum OwnerTodoFilter
{
    /// <summary>不篩，顯示這位負責人的全部待辦。</summary>
    All,

    Pending,

    InProgress,

    Completed,

    /// <summary>逾期。注意這一項與前三項是不同的軸，見 <see cref="OwnerTodoFilter"/> 的說明。</summary>
    Overdue,
}

/// <summary>
/// 負責人面板小清單的檢視層篩選。
///
/// <para>
/// 放在 view 旁邊而不是 <c>Components/Commons</c>：它只有一個消費者，
/// 升格成「共用 helper」是假的（CLAUDE.md §2）。但仍抽成純函式，理由與
/// <c>FormKeyboardHelper</c>／<c>FormDirtyHelper</c> 一樣——<b>本專案沒有 bUnit</b>，
/// 寫進 <c>.razor.cs</c> 的判斷就只能靠眼睛驗。
/// </para>
///
/// <para>
/// ⚠️ 這裡做的是**記憶體篩選**，服務層完全不動。特別是**不可以**把狀態帶進
/// <c>TodoService.GetOwnerSummariesAsync</c>——膠囊自己的數字與完成度進度條
/// 一律要維持未篩選，否則會得到「每個人都 100%」或「都 0%」，
/// 那不是空資料，是看起來合理的錯誤數字（理由見該方法的註解與待辦事項 PRD）。
/// </para>
/// </summary>
public static class TodoOwnerFilter
{
    /// <summary>
    /// 套用篩選。<paramref name="todos"/> 是某一位負責人的全部待辦
    /// （<c>GetByOwnerAsync</c> 刻意不分頁，單人量級不需要），所以在記憶體篩成本可忽略。
    /// </summary>
    public static IReadOnlyList<TodoAdapterModel> Apply(
        IReadOnlyList<TodoAdapterModel> todos,
        OwnerTodoFilter filter)
    {
        ArgumentNullException.ThrowIfNull(todos);

        return filter switch
        {
            OwnerTodoFilter.Pending => Where(todos, x => x.Status == TodoAdapterModel.StatusOptions[0]),
            OwnerTodoFilter.InProgress => Where(todos, x => x.Status == TodoAdapterModel.StatusOptions[1]),

            // 用 IsCompleted 而不是比對字串：完成與否的唯一權威在 model 上。
            OwnerTodoFilter.Completed => Where(todos, x => x.IsCompleted),

            // IsOverdue 已經排除了已完成——已經做完的事不該出現在逾期清單裡，
            // 而膠囊上的數字也是這樣數的，兩邊必須一致。
            OwnerTodoFilter.Overdue => Where(todos, x => x.IsOverdue),

            _ => todos,
        };
    }

    /// <summary>
    /// 把作用中的篩選收斂回合法狀態。
    ///
    /// <para>
    /// 守的是一條不變式：<b>作用中的篩選，畫面上一定要有一顆可以點掉它的膠囊。</b>
    /// 0 筆的膠囊不可點（逾期那顆甚至整個不渲染），篩選若留著就會變成
    /// 「空清單，而且畫面上沒有任何東西可以按掉它」。
    /// </para>
    ///
    /// <para>
    /// 典型情境：篩了逾期，然後把最後一筆逾期的改成已完成。
    /// </para>
    /// </summary>
    public static OwnerTodoFilter Clamp(TodoOwnerSummary? summary, OwnerTodoFilter filter)
    {
        if (filter == OwnerTodoFilter.All)
        {
            return filter;
        }

        if (summary is null)
        {
            return OwnerTodoFilter.All;
        }

        return CountOf(summary, filter) == 0 ? OwnerTodoFilter.All : filter;
    }

    /// <summary>某個篩選條件在統計裡對應的筆數。膠囊上顯示的就是這個數字。</summary>
    public static int CountOf(TodoOwnerSummary summary, OwnerTodoFilter filter)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return filter switch
        {
            OwnerTodoFilter.Pending => summary.Pending,
            OwnerTodoFilter.InProgress => summary.InProgress,
            OwnerTodoFilter.Completed => summary.Completed,
            OwnerTodoFilter.Overdue => summary.Overdue,
            _ => summary.Total,
        };
    }

    /// <summary>篩選條件的顯示文字（篩選列與 aria 說明共用，避免兩處各寫一份）。</summary>
    public static string Describe(OwnerTodoFilter filter) => filter switch
    {
        OwnerTodoFilter.Pending => TodoAdapterModel.StatusOptions[0],
        OwnerTodoFilter.InProgress => TodoAdapterModel.StatusOptions[1],
        OwnerTodoFilter.Completed => TodoAdapterModel.StatusOptions[2],
        OwnerTodoFilter.Overdue => "逾期",
        _ => "全部",
    };

    private static IReadOnlyList<TodoAdapterModel> Where(
        IReadOnlyList<TodoAdapterModel> todos,
        Func<TodoAdapterModel, bool> predicate)
        => todos.Where(predicate).ToList();
}
