using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 待辦子項目的規則（1.0.45），Web（<see cref="TodoService"/>）與 API（<c>TodoRepository</c>）共用同一份。
///
/// <para>
/// 使用者要「同一個功能的子功能」：子項目是完整的待辦（自己的負責人、期限、狀態），掛在一筆主待辦底下。
/// <b>只分一層</b>——多層樹在清單、篩選、通知上都會變複雜，而會議追蹤的事情很少需要第三層。
/// </para>
/// </summary>
public static class TodoHierarchyRules
{
    /// <summary>
    /// 檢查「把 <paramref name="todoId"/> 掛到 <paramref name="parentId"/> 底下」合不合法。合法回 null，否則回給使用者看的原因。
    /// </summary>
    /// <param name="todoId">要設定的待辦；新增時傳 null。</param>
    /// <param name="projectId">這筆待辦（改完之後）的專案。</param>
    /// <param name="parentId">主待辦；null＝不當子項目，一律合法。</param>
    public static async Task<string?> ValidateAsync(BackendDBContext context, int? todoId, int projectId, int? parentId)
    {
        if (parentId is not { } pid)
        {
            return null;
        }

        if (todoId == pid)
        {
            return "不能把待辦設為自己的子項目。";
        }

        var parent = await context.Todo.AsNoTracking()
            .Where(x => x.Id == pid)
            .Select(x => new { x.ProjectId, x.ParentTodoId })
            .FirstOrDefaultAsync();

        if (parent is null)
        {
            return "找不到指定的主待辦，可能已經被刪除。";
        }

        if (parent.ParentTodoId is not null)
        {
            return "子項目底下不能再有子項目（只分一層），請改選它的主待辦。";
        }

        if (parent.ProjectId != projectId)
        {
            return "子項目必須和主待辦在同一個專案。";
        }

        if (todoId is { } id && await context.Todo.AsNoTracking().AnyAsync(x => x.ParentTodoId == id))
        {
            return "這筆待辦已經有子項目，不能再變成別人的子項目（只分一層）。";
        }

        return null;
    }

    /// <summary>
    /// 主待辦搬到別的專案時，子項目跟著搬（子項目必須和主待辦同專案）。來源會議屬於原專案，一併清掉（同 0.4.115 的規則）。
    /// 回傳搬動的筆數。在主待辦存檔成功之後呼叫。
    /// </summary>
    public static Task<int> MoveChildrenAsync(BackendDBContext context, int parentId, int newProjectId)
        => context.Todo
            .Where(x => x.ParentTodoId == parentId && x.ProjectId != newProjectId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ProjectId, newProjectId)
                .SetProperty(x => x.MeetingId, (int?)null)
                .SetProperty(x => x.UpdatedAt, DateTime.Now));
}
