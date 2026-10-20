namespace MeetingRecord.Web.Components.Commons;

/// <summary>批次動作的一筆失敗：顯示用名稱與原因。</summary>
public sealed record BatchFailure(string Name, string Reason);

/// <summary>批次動作的結果（1.0.53）。</summary>
public sealed record BatchResult(int Succeeded, IReadOnlyList<BatchFailure> Failures)
{
    /// <summary>通知裡最多列出幾筆失敗，其餘只給數量。</summary>
    public const int MaxListedFailures = 5;

    public bool HasFailures => Failures.Count > 0;

    /// <summary>
    /// 給使用者看的結果，例如「已刪除 3 筆。」或「已刪除 3 筆，2 筆沒有刪除：A（原因）、B（原因）。」。
    /// <paramref name="verb"/> 是動作名稱（「刪除」「從專案移除」）。
    /// </summary>
    public string Summary(string verb)
    {
        var done = $"已{verb} {Succeeded} 筆";
        if (!HasFailures)
        {
            return $"{done}。";
        }

        var listed = string.Join("、", Failures.Take(MaxListedFailures).Select(x => $"{x.Name}（{x.Reason}）"));
        var more = Failures.Count > MaxListedFailures ? $"等 {Failures.Count} 筆" : string.Empty;
        return $"{done}，{Failures.Count} 筆沒有{verb}：{listed}{more}。";
    }
}

/// <summary>
/// 清單的多選批次動作（1.0.53）：一律<b>逐筆依序</b>呼叫既有的單筆動作（單筆刪除各自負責清檔案、對話、
/// AI 紀錄、稽核、子項目異動），不另寫 RemoveRange。
/// <para>
/// ⚠️ 不可平行（<c>Task.WhenAll</c>）：Blazor 一條連線共用同一個 DbContext，同時跑兩個操作會直接丟例外。
/// 某一筆丟例外只記成失敗，不中斷後面的。
/// </para>
/// </summary>
public static class BatchRunner
{
    /// <param name="runOne">回傳 null＝成功；字串＝失敗原因。</param>
    public static async Task<BatchResult> RunAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> describe,
        Func<T, Task<string?>> runOne)
    {
        var succeeded = 0;
        var failures = new List<BatchFailure>();
        foreach (var item in items)
        {
            string? error;
            try
            {
                error = await runOne(item);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error is null)
            {
                succeeded++;
            }
            else
            {
                failures.Add(new BatchFailure(describe(item), error));
            }
        }

        return new BatchResult(succeeded, failures);
    }
}

/// <summary>把批次結果顯示成右下角通知：全部成功用成功樣式，有沒做成的用警告樣式（1.0.53）。</summary>
public static class BatchResultNotifier
{
    public static void Notify(AntDesign.NotificationService notificationService, BatchResult result, string verb)
    {
        _ = notificationService.Open(new AntDesign.NotificationConfig
        {
            Message = "系統訊息",
            Description = result.Summary(verb),
            NotificationType = result.HasFailures ? AntDesign.NotificationType.Warning : AntDesign.NotificationType.Success,
            Placement = AntDesign.NotificationPlacement.BottomRight,
        });
    }
}
