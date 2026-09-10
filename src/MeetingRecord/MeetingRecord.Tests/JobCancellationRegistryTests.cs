using MeetingRecord.Business.Services.Other;

namespace MeetingRecord.Tests;

/// <summary>
/// 背景工作取消登記處的單元測試。
///
/// 重點在兩件錯了會很難查的事：**排隊中的取消標記只能生效一次**（否則同一筆重新入列會被誤殺），
/// 以及 **IsCancelledByUser 要能把「使用者取消」與「應用程式關機」分開**
/// （兩者都只會拋 OperationCanceledException）。
/// </summary>
public sealed class JobCancellationRegistryTests
{
    #region 排隊中的取消

    [Fact]
    public void PendingCancel_ShouldBeConsumedExactlyOnce()
    {
        var registry = new JobCancellationRegistry();

        registry.RequestCancel(BackgroundJobKind.Transcription, 7);

        Assert.True(registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, 7));

        // 第二次必須是 false——否則使用者取消一次之後，這筆重新入列還會再被殺掉。
        Assert.False(registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, 7));
    }

    [Fact]
    public void PendingCancel_ShouldNotLeakAcrossJobKinds()
    {
        var registry = new JobCancellationRegistry();

        registry.RequestCancel(BackgroundJobKind.Transcription, 7);

        // 同一筆會議可能同時有轉錄與生成兩種工作，取消其中一種不該影響另一種。
        Assert.False(registry.TryConsumePendingCancel(BackgroundJobKind.MeetingDraft, 7));
        Assert.True(registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, 7));
    }

    [Fact]
    public void BeginJob_ShouldClearStalePendingCancel()
    {
        var registry = new JobCancellationRegistry();

        registry.RequestCancel(BackgroundJobKind.Transcription, 7);

        // 走到 BeginJob 代表 worker 已經檢查過並決定執行；殘留的標記必須清掉，
        // 否則下一次入列會被上一輪的標記誤殺。
        using var handle = registry.BeginJob(BackgroundJobKind.Transcription, 7, CancellationToken.None);

        Assert.False(registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, 7));
        Assert.False(handle.IsCancelledByUser);
    }

    #endregion

    #region 執行中的取消

    [Fact]
    public void RequestCancel_WhileRunning_ShouldCancelTokenAndFlagUser()
    {
        var registry = new JobCancellationRegistry();

        using var handle = registry.BeginJob(BackgroundJobKind.Transcription, 7, CancellationToken.None);

        Assert.False(handle.Token.IsCancellationRequested);

        registry.RequestCancel(BackgroundJobKind.Transcription, 7);

        Assert.True(handle.Token.IsCancellationRequested);
        Assert.True(handle.IsCancelledByUser);
    }

    [Fact]
    public void ServiceShutdown_ShouldCancelTokenWithoutFlaggingUser()
    {
        // 這是整個設計的關鍵：關機與使用者取消都會讓權杖進入取消狀態，
        // 但只有後者該把工作標成「已取消」，前者要留給啟動復原標成失敗。
        var registry = new JobCancellationRegistry();
        using var serviceSource = new CancellationTokenSource();

        using var handle = registry.BeginJob(BackgroundJobKind.Transcription, 7, serviceSource.Token);

        serviceSource.Cancel();

        Assert.True(handle.Token.IsCancellationRequested);
        Assert.False(handle.IsCancelledByUser);
    }

    [Fact]
    public void Dispose_ShouldUnregisterSoLaterCancelFallsBackToPending()
    {
        var registry = new JobCancellationRegistry();

        using (registry.BeginJob(BackgroundJobKind.Transcription, 7, CancellationToken.None))
        {
        }

        // 已經跑完的工作再被取消時，不該炸掉；標記會留給下一次入列前的檢查。
        registry.RequestCancel(BackgroundJobKind.Transcription, 7);

        Assert.True(registry.TryConsumePendingCancel(BackgroundJobKind.Transcription, 7));
    }

    [Fact]
    public void RequestCancel_ForUnknownJob_ShouldNotThrow()
    {
        var registry = new JobCancellationRegistry();

        // 使用者手快、工作剛好結束時會走到這裡。
        var exception = Record.Exception(() => registry.RequestCancel(BackgroundJobKind.MeetingDraft, 999));

        Assert.Null(exception);
    }

    #endregion
}
