using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Tests;

/// <summary>
/// 表單送出閘門（1.0.62）：Enter 與「確定」共用；按 Enter 時等候回寫的那 200ms 也算在送出中。
/// 1.0.61 以前等候不在防護裡，等候期間再按「確定」（或長按 Enter）會新增兩筆。
/// </summary>
public sealed class FormSubmitGateTests
{
    [Fact]
    public async Task SecondSubmit_WhileFirstIsWaitingForBinding_ShouldBeIgnored()
    {
        var gate = new FormSubmitGate();
        var saves = 0;

        // Enter：先等回寫，再存。等候期間按「確定」。
        var enter = gate.RunAsync(() => { saves++; return Task.CompletedTask; }, TimeSpan.FromMilliseconds(200));
        Assert.True(gate.IsBusy);
        await gate.RunAsync(() => { saves++; return Task.CompletedTask; });
        await enter;

        Assert.Equal(1, saves);
        Assert.False(gate.IsBusy);
    }

    [Fact]
    public async Task SecondSubmit_WhileFirstIsSaving_ShouldBeIgnored()
    {
        var gate = new FormSubmitGate();
        var release = new TaskCompletionSource();
        var saves = 0;

        var first = gate.RunAsync(async () => { saves++; await release.Task; });
        await gate.RunAsync(() => { saves++; return Task.CompletedTask; });
        release.SetResult();
        await first;

        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task Submit_AfterPreviousFinished_ShouldRunAgain()
    {
        var gate = new FormSubmitGate();
        var saves = 0;

        await gate.RunAsync(() => { saves++; return Task.CompletedTask; });
        await gate.RunAsync(() => { saves++; return Task.CompletedTask; });

        Assert.Equal(2, saves);
    }

    [Fact]
    public async Task FailedSubmit_ShouldReleaseTheGate()
    {
        var gate = new FormSubmitGate();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(() => throw new InvalidOperationException("存檔失敗")));

        Assert.False(gate.IsBusy);
        var ran = false;
        await gate.RunAsync(() => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
    }
}
