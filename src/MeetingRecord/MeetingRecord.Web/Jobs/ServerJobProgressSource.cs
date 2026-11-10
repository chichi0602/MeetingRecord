using MeetingRecord.Business.Services.Other;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Business.Services.Transcription;
using MeetingRecord.Dtos.Jobs;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Jobs;

/// <summary>
/// Blazor Server 頁的進度面板資料來源（1.1.15）：直接讀兩個 Singleton 通知器（經 <see cref="BackgroundJobBoard"/> 過濾），
/// 通知器一變動就重查，比照原本的面板。
/// </summary>
public sealed class ServerJobProgressSource(
    BackgroundJobBoard board,
    ITranscriptionProgressNotifier transcriptionNotifier,
    IMeetingDraftProgressNotifier draftNotifier,
    AuthenticationStateHelper authenticationStateHelper) : IJobProgressSource
{
    public async Task<IReadOnlyList<BackgroundJobDto>> GetJobsAsync() => await board.GetVisibleAsync();

    /// <summary>
    /// 取消要有會議紀錄的修改權限（0.4.115）。看得到通知只代表看得到那筆會議；
    /// 只有檢視權限的同團隊成員，以前也能中止別人正在跑的轉錄（已送出的段落照樣計費）。
    /// </summary>
    public Task<bool> CanCancelAsync()
        => Task.FromResult(authenticationStateHelper.CheckAccessAction(MagicObjectHelper.角色_會議紀錄, PermissionActions.Edit));

    public async Task CancelAsync(BackgroundJobDto job)
    {
        if (await CanCancelAsync())
        {
            await board.CancelAsync(job.Kind, job.MeetingId);
        }
    }

    public Task DismissAsync(BackgroundJobDto job) => board.DismissAsync(job.Kind, job.MeetingId);

    public Task DismissAllAsync() => board.DismissAllAsync();

    public IDisposable Subscribe(Func<Task> onChanged)
    {
        // 由背景執行緒引發；面板自己會切回 UI 執行緒。
        void Handler() => _ = onChanged();

        transcriptionNotifier.Changed += Handler;
        draftNotifier.Changed += Handler;
        return new Subscription(() =>
        {
            transcriptionNotifier.Changed -= Handler;
            draftNotifier.Changed -= Handler;
        });
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
