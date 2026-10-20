using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Web.Components.Views.Todos;

/// <summary>
/// 待辦清單的多選刪除（1.0.53）。主待辦與它的子項目可能同時被勾選：刪主待辦時資料庫 Cascade 會連子項目一起刪，
/// 子項目再刪一次就會變成「找不到」的失敗。所以主待辦已勾選時，略過它被勾的子項目。
/// </summary>
public static class TodoBatchSelection
{
    public static IReadOnlyList<TodoAdapterModel> ExcludeChildrenOfSelectedParents(IEnumerable<TodoAdapterModel> selected)
    {
        var items = selected.DistinctBy(x => x.Id).ToList();
        var selectedIds = items.Select(x => x.Id).ToHashSet();
        return [.. items.Where(x => x.ParentTodoId is not { } parentId || !selectedIds.Contains(parentId))];
    }

    /// <summary>這一頁所有列（主待辦＋展開的子項目），勾選依 Id 對回新資料時用。</summary>
    public static IEnumerable<TodoAdapterModel> Flatten(IEnumerable<TodoAdapterModel> rows)
        => rows.SelectMany(x => (x.Children ?? []).Prepend(x));
}
