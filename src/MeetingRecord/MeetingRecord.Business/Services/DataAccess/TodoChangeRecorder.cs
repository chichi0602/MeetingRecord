using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Services.Notifications;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.DataAccess;

/// <summary>
/// 待辦異動的唯一記錄點（1.0.44）：寫異動紀錄、對專案成員發站內通知。
///
/// <para>
/// 使用者問「有人改了期限，一起負責那項專案的人要怎麼知道」。所有會改待辦的路徑
/// （待辦頁修改、勾完成、API、從會議紀錄抽出時更新既有）都要經過這裡——漏接一條，那條路徑的修改就沒人知道。
/// </para>
///
/// <para>
/// 用法：存檔<b>之前</b>呼叫 <see cref="StageAsync"/>（異動與通知加進同一個 DbContext，和待辦一起存、一起失敗），
/// 存檔<b>成功之後</b>呼叫 <see cref="Publish"/> 讓鈴鐺即時更新。
/// </para>
/// </summary>
public class TodoChangeRecorder
{
    private readonly BackendDBContext context;
    private readonly ProjectAccessService projectAccess;
    private readonly IAuditActorProvider actorProvider;
    private readonly IUserNotificationNotifier notifier;

    public TodoChangeRecorder(
        BackendDBContext context,
        ProjectAccessService projectAccess,
        IAuditActorProvider actorProvider,
        IUserNotificationNotifier notifier)
    {
        this.context = context;
        this.projectAccess = projectAccess;
        this.actorProvider = actorProvider;
        this.notifier = notifier;
    }

    /// <summary>
    /// 比對前後兩份待辦，把有變的欄位加進異動紀錄；期限、負責人、狀態有變時，對收件人加通知。
    /// 只加進 DbContext、不存檔。回傳收件人（存檔成功後交給 <see cref="Publish"/>）。
    /// </summary>
    /// <param name="before">修改前的值（呼叫端在覆寫前抓的快照）。</param>
    /// <param name="after">修改後的值。</param>
    /// <param name="via">從哪裡改的（<see cref="AuditVia"/>）。</param>
    public async Task<IReadOnlyList<int>> StageAsync(
        Todo before,
        Todo after,
        string via,
        int? sourceMeetingId = null,
        string? sourceMeetingTitle = null)
    {
        var diffs = Diff(before, after);
        if (diffs.Count == 0)
        {
            return [];
        }

        var (actorId, actorAccount) = actorProvider.GetActor();
        var actorName = await ResolveActorNameAsync(actorId, actorAccount);
        var now = DateTime.Now;

        foreach (var (field, oldValue, newValue) in diffs)
        {
            context.TodoChange.Add(new TodoChange
            {
                TodoId = after.Id,
                ProjectId = after.ProjectId,
                Field = field,
                OldValue = oldValue,
                NewValue = newValue,
                ChangedByUserId = actorId,
                ChangedByName = actorName,
                ChangedAt = now,
                Via = via,
                SourceMeetingId = sourceMeetingId,
                SourceMeetingTitle = sourceMeetingTitle,
            });
        }

        var notifying = diffs.Where(x => TodoChangeFields.Notifying.Contains(x.Field)).ToList();
        if (notifying.Count == 0)
        {
            return [];
        }

        // 移到別的專案時，新舊兩個專案的人都要知道。
        var recipients = (await projectAccess.GetProjectMemberIdsAsync([before.ProjectId, after.ProjectId]))
            .Where(id => id != actorId)
            .ToList();

        if (recipients.Count == 0)
        {
            return [];
        }

        var message = BuildMessage(actorName, after.Title, notifying, via, sourceMeetingTitle);
        foreach (var userId in recipients)
        {
            context.UserNotification.Add(new UserNotification
            {
                UserId = userId,
                TodoId = after.Id,
                ProjectId = after.ProjectId,
                Message = message,
                CreatedAt = now,
            });
        }

        return recipients;
    }

    /// <summary>
    /// 存檔成功之後呼叫：放掉剛寫進去的異動與通知的追蹤（DbContext 是整條 Blazor 連線共用的，
    /// 留著會越積越多），再讓收件人的鈴鐺即時更新。
    /// </summary>
    public void Publish(IReadOnlyList<int> recipients)
    {
        foreach (var entry in context.ChangeTracker.Entries<TodoChange>().ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in context.ChangeTracker.Entries<UserNotification>().ToList())
        {
            entry.State = EntityState.Detached;
        }

        notifier.Notify(recipients);
    }

    /// <summary>有變的欄位與顯示用的新舊值。期限只比日期。</summary>
    public static IReadOnlyList<(string Field, string? Old, string? New)> Diff(Todo before, Todo after)
    {
        var result = new List<(string, string?, string?)>();

        void Compare(string field, string? oldValue, string? newValue)
        {
            if (!string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
            {
                result.Add((field, oldValue, newValue));
            }
        }

        Compare(TodoChangeFields.Title, before.Title?.Trim(), after.Title?.Trim());
        Compare(TodoChangeFields.DueDate, FormatDate(before.DueDate), FormatDate(after.DueDate));
        Compare(TodoChangeFields.Owner, NullIfBlank(before.Owner), NullIfBlank(after.Owner));
        Compare(TodoChangeFields.Status, before.Status, after.Status);
        Compare(TodoChangeFields.Priority, before.Priority, after.Priority);

        return result;
    }

    /// <summary>例：「王小明 把『完成報表』的期限改為 2026/10/31、負責人改為 林大華（來源：從〈週會〉抽出）」。</summary>
    public static string BuildMessage(
        string actorName,
        string? title,
        IReadOnlyList<(string Field, string? Old, string? New)> changes,
        string via,
        string? sourceMeetingTitle)
    {
        var parts = changes.Select(x => $"{TodoChangeFields.Describe(x.Field)}改為 {DisplayValue(x.Field, x.New)}");
        var message = $"{actorName} 把『{title?.Trim()}』的{string.Join("、", parts)}";

        if (via == AuditVia.AiExtract)
        {
            message += $"（來源：{TodoChangeFields.DescribeVia(via, sourceMeetingTitle)}）";
        }

        // 欄位上限 500：標題本身最長 200，正常不會超過，這裡只是保險。
        return message.Length <= 500 ? message : message[..497] + "…";
    }

    /// <summary>空值在畫面上要講清楚是「未設定」而不是一片空白。</summary>
    public static string DisplayValue(string field, string? value)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : field switch
            {
                TodoChangeFields.DueDate => "未設定",
                TodoChangeFields.Owner => "未指定",
                _ => "（空白）",
            };

    public static string? FormatDate(DateTime? date) => date?.ToString("yyyy/MM/dd");

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<string> ResolveActorNameAsync(int? actorId, string? actorAccount)
    {
        if (actorId is { } id)
        {
            var name = await context.MyUser.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => x.Name)
                .FirstOrDefaultAsync();

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(actorAccount) ? "有人" : actorAccount;
    }
}
