using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Services;
using Microsoft.AspNetCore.Components;

namespace MeetingRecord.Web.Client.Pages.Meetings;

/// <summary>
/// 1.1.71：上傳視窗的「錄音」——開會時直接用電腦的麥克風錄，錄好就當成選好的影音檔，
/// 按「確定」走原本的費用確認、上傳、轉錄（也能選轉錄完自動產生會議紀錄）。
///
/// <para>
/// 聲音留在 JS 端（<c>media-recorder.js</c>），上傳時由 <c>MeetingUploadManager.StartRecordingAsync</c> 交給分塊上傳。
/// 錄音中關視窗、換頁都會丟掉錄音（關分頁或重新整理由瀏覽器跳「確定要離開嗎？」）。
/// </para>
/// </summary>
public partial class MeetingPage
{
    [Inject] private MediaRecorderInterop recorderInterop { get; set; } = default!;

    private bool isRecording;
    private bool isTogglingRecording;
    private DateTime recordingStartedAt;
    private System.Threading.Timer? recordingTimer;

    /// <summary>錄好、還沒上傳的聲音（本體在 JS 端）。</summary>
    private RecordedAudio? pendingRecording;
    private string? pendingRecordingName;

    /// <summary>錄音計時的秒數：錄音檔的檔頭沒有長度，瀏覽器讀不出來，成本閘門用這個。</summary>
    private double pendingRecordingSeconds;

    /// <summary>有沒有待上傳的影音（選的檔案或錄好的聲音）。</summary>
    private bool HasPendingMedia => pendingMediaFile is not null || pendingRecording is not null;

    private string RecordingElapsedText => FormatRecordingSeconds((DateTime.Now - recordingStartedAt).TotalSeconds);

    private static string FormatRecordingSeconds(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
    }

    private async Task StartRecordingAsync()
    {
        if (isRecording || isTogglingRecording)
        {
            return;
        }

        isTogglingRecording = true;
        try
        {
            // 錄音和選檔互斥：開始錄就清掉之前選的檔案與錄音。
            ClearPendingMediaFile();
            DiscardPendingRecording();

            if (await recorderInterop.StartAsync() is { } error)
            {
                NotifyError(error);
                return;
            }

            isRecording = true;
            recordingStartedAt = DateTime.Now;
            recordingTimer = new System.Threading.Timer(_ => InvokeAsync(StateHasChanged), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            logger.LogInformation("Meeting recording started.");
        }
        finally
        {
            isTogglingRecording = false;
        }
    }

    private async Task StopRecordingAsync()
    {
        if (!isRecording || isTogglingRecording)
        {
            return;
        }

        isTogglingRecording = true;
        try
        {
            var seconds = (DateTime.Now - recordingStartedAt).TotalSeconds;
            StopRecordingTimer();
            isRecording = false;

            var audio = await recorderInterop.StopAsync();
            if (audio is null || audio.Size == 0)
            {
                NotifyError("沒有錄到聲音，請確認麥克風後再錄一次。");
                return;
            }

            if (audio.Size > MeetingMediaPolicy.MaxUploadFileSize)
            {
                _ = recorderInterop.DiscardAsync(audio.Key);
                NotifyError($"錄音超過單檔上限 {MeetingMediaPolicy.FormatFileSize(MeetingMediaPolicy.MaxUploadFileSize)}。");
                return;
            }

            pendingRecording = audio;
            pendingRecordingName = $"錄音-{recordingStartedAt:yyyyMMdd-HHmm}{audio.Extension}";
            pendingRecordingSeconds = seconds;

            // 標題是必填：還沒填就先帶「錄音 日期 時間」，要改再改。
            if (string.IsNullOrWhiteSpace(CurrentRecord.Title))
            {
                CurrentRecord.Title = $"錄音 {recordingStartedAt:yyyy/MM/dd HH:mm}";
            }

            logger.LogInformation("Meeting recording stopped. Size={Size}, Seconds={Seconds}", audio.Size, seconds);
            await LoadAutoDraftTemplatesAsync();
        }
        finally
        {
            isTogglingRecording = false;
        }
    }

    /// <summary>重錄：丟掉這一份再開始。</summary>
    private async Task RerecordAsync()
    {
        DiscardPendingRecording();
        await StartRecordingAsync();
    }

    private void RemovePendingRecording()
    {
        DiscardPendingRecording();
        autoDraftPromptTemplateId = 0;
    }

    /// <summary>不要這份錄音了（JS 端那份也丟掉）。上傳已經開始時 JS 早就取走了，丟一把不存在的鑰匙沒關係。</summary>
    private void DiscardPendingRecording()
    {
        if (pendingRecording is { } audio)
        {
            _ = recorderInterop.DiscardAsync(audio.Key);
        }

        pendingRecording = null;
        pendingRecordingName = null;
        pendingRecordingSeconds = 0;
    }

    /// <summary>關視窗、換頁：還在錄就停掉並丟掉，錄好的也丟掉。</summary>
    private void AbandonRecording()
    {
        StopRecordingTimer();
        if (isRecording)
        {
            isRecording = false;
            _ = recorderInterop.CancelAsync();
        }

        DiscardPendingRecording();
    }

    private void StopRecordingTimer()
    {
        recordingTimer?.Dispose();
        recordingTimer = null;
    }
}
