using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// MeetingAdapterModel 上兩個「能不能再送一次付費工作」的旗標。
///
/// 這不是外觀邏輯：畫面靠它們決定要不要顯示／啟用「重新轉錄」與「AI 轉會議紀錄」，
/// 而每一次誤放行都是一次真實的 Azure OpenAI 費用。
/// </summary>
public sealed class MeetingAdapterModelTests
{
    [Fact]
    public void CanRetryTranscription_ShouldBeFalse_WhenTranscriptionIsPending()
    {
        // Pending 代表已入列但還沒開工。放行就會入列第二次、跑兩趟並重複計費。
        var model = NewModelWithMedia(TranscriptionStatus.Pending);

        Assert.False(model.CanRetryTranscription);
    }

    [Fact]
    public void CanRetryTranscription_ShouldBeFalse_WhenTranscriptionIsProcessing()
    {
        var model = NewModelWithMedia(TranscriptionStatus.Processing);

        Assert.False(model.CanRetryTranscription);
    }

    [Fact]
    public void CanRetryTranscription_ShouldBeTrue_WhenTranscriptionIsCancelled()
    {
        // 取消後沒有進度可接續，只能整個重跑，所以一定要放行。
        // 0.4.65 之前這裡是列舉式且漏了 Cancelled，取消等於死路（只能重新上傳檔案）。
        var model = NewModelWithMedia(TranscriptionStatus.Cancelled);

        Assert.True(model.CanRetryTranscription);
    }

    [Theory]
    [InlineData(TranscriptionStatus.Failed)]
    [InlineData(TranscriptionStatus.Completed)]
    public void CanRetryTranscription_ShouldBeTrue_WhenPreviousRunFinished(TranscriptionStatus status)
    {
        var model = NewModelWithMedia(status);

        Assert.True(model.CanRetryTranscription);
    }

    [Fact]
    public void CanRetryTranscription_ShouldBeFalse_WhenNoMediaUploaded()
    {
        // 沒有影音檔就沒有東西可以轉錄，不能只看狀態。
        var model = new MeetingAdapterModel
        {
            Title = "沒有影音檔的會議",
            TranscriptionStatus = TranscriptionStatus.Failed,
        };

        Assert.False(model.CanRetryTranscription);
    }

    [Fact]
    public void CanGenerateDraft_ShouldBeFalse_WhenDraftIsPending()
    {
        var model = new MeetingAdapterModel
        {
            Title = "已排入生成的會議",
            TranscriptionStatus = TranscriptionStatus.Completed,
            TranscriptRelativePath = "2026/09/transcript.txt",
            DraftStatus = DraftStatus.Pending,
        };

        Assert.False(model.CanGenerateDraft);
    }

    [Fact]
    public void CanGenerateDraft_ShouldBeTrue_WhenPreviousRunWasCancelled()
    {
        var model = new MeetingAdapterModel
        {
            Title = "生成被取消的會議",
            TranscriptionStatus = TranscriptionStatus.Completed,
            TranscriptRelativePath = "2026/09/transcript.txt",
            DraftStatus = DraftStatus.Cancelled,
        };

        Assert.True(model.CanGenerateDraft);
    }

    private static MeetingAdapterModel NewModelWithMedia(TranscriptionStatus status) => new()
    {
        Title = "有影音檔的會議",
        MediaRelativePath = "2026/09/media.mp3",
        TranscriptionStatus = status,
    };
}
