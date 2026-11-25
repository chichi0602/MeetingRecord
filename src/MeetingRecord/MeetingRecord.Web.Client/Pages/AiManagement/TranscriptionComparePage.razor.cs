using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Diagnostics;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Services;

namespace MeetingRecord.Web.Client.Pages.AiManagement;

/// <summary>
/// 轉錄比較（1.1.72）。**管理者限定**，API 也是 <c>[AdminOnly]</c>。
///
/// <para>為什麼有這頁：有人反映 diarize 模型的逐字稿比會議內容精簡、夾英文，而 diarize 2027/02/26 下線、
/// 官方建議的 gpt-transcribe 不分講者。先拿同一段片段實際比較，再決定正式轉錄怎麼改。</para>
///
/// <para>按「開始比較」先跳費用確認；結果邊跑邊顯示，離開頁面或按「停止」就中斷，還沒跑的組不會花錢。</para>
/// </summary>
public partial class TranscriptionComparePage : ComponentBase, IDisposable
{
    private static readonly int[] LengthOptions = [30, 60, 120, 180, 300];

    [Inject] private MonitoringApi MonitoringApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private MessageService MessageService { get; set; } = default!;
    [Inject] private ClipboardInterop ClipboardInterop { get; set; } = default!;
    [Inject] private ILogger<TranscriptionComparePage> Logger { get; set; } = default!;

    private bool isAccessChecked;
    private string? RoleMessage;
    private string configuredDeployment = string.Empty;

    /// <summary>1.1.73：Azure LLM Speech 設定好了才能選。</summary>
    private bool llmSpeechAvailable;
    private List<TranscriptionCompareMeetingDto> meetings = [];
    private int meetingId;
    private int startMinute;
    private int seconds = TranscriptionCompareRequestDto.MaxSeconds;
    private List<TranscriptionCompareVariantDto> variants = [];

    private bool isRunning;
    private string progressText = string.Empty;
    private string? errorMessage;
    private string? audioDataUrl;
    private double excerptSeconds;
    private readonly List<TranscriptionCompareEventDto> results = [];

    /// <summary>這一趟送出的條件（結果欄標頭用，表單之後改了也不影響）。</summary>
    private List<TranscriptionCompareVariantDto> sentVariants = [];
    private CancellationTokenSource? runCancellation;

    private TranscriptionCompareMeetingDto? SelectedMeeting => meetings.FirstOrDefault(x => x.Id == meetingId);

    protected override async Task OnInitializedAsync()
    {
        await PermissionView.LoadAsync();
        isAccessChecked = true;

        if (!PermissionView.IsAdmin)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/transcription-compare");
            Logger.LogWarning("Transcription compare page denied because the current user is not an administrator.");
            return;
        }

        var meta = await MonitoringApi.GetTranscriptionCompareMetaAsync();
        if (!meta.Success || meta.Data is null)
        {
            errorMessage = meta.ErrorMessage ?? meta.Message;
            return;
        }

        configuredDeployment = meta.Data.ConfiguredDeployment;
        llmSpeechAvailable = meta.Data.LlmSpeechAvailable;
        meetings = meta.Data.Meetings;

        // 預設兩組：目前設定（照正式轉錄送，不帶語言），以及官方建議的 gpt-transcribe 指定中文。
        variants =
        [
            new() { Deployment = configuredDeployment },
            new() { Deployment = "gpt-transcribe", Language = "zh" },
        ];

        // 1.1.73：LLM Speech 設定好了就多帶一組（分講者），三種直接比。
        if (llmSpeechAvailable)
        {
            variants.Add(new() { Engine = 1, Diarize = true, MaxSpeakers = 10 });
        }
    }

    private void AddVariant()
    {
        if (variants.Count < TranscriptionCompareRequestDto.MaxVariants)
        {
            variants.Add(new TranscriptionCompareVariantDto());
        }
    }

    private void RemoveVariant(int index) => variants.RemoveAt(index);

    private bool IsDiarize(TranscriptionCompareVariantDto variant)
        => (string.IsNullOrWhiteSpace(variant.Deployment) ? configuredDeployment : variant.Deployment)
            .Contains("diarize", StringComparison.OrdinalIgnoreCase);

    private async Task OnRunAsync()
    {
        if (meetingId == 0 || isRunning)
        {
            return;
        }

        var request = new TranscriptionCompareRequestDto
        {
            MeetingId = meetingId,
            StartSeconds = Math.Max(0, startMinute) * 60,
            Seconds = seconds,
            Variants = [.. variants.Select(x => new TranscriptionCompareVariantDto
            {
                Engine = x.Engine,
                Diarize = x.Diarize,
                MaxSpeakers = x.Diarize ? x.MaxSpeakers ?? 10 : null,
                Deployment = x.Deployment?.Trim() ?? string.Empty,
                Language = x.Language ?? string.Empty,
                Prompt = IsDiarize(x) ? string.Empty : x.Prompt?.Trim() ?? string.Empty,
            })],
        };

        var estimate = await MonitoringApi.EstimateTranscriptionCompareAsync(request);
        if (!estimate.Success || estimate.Data is null)
        {
            errorMessage = estimate.ErrorMessage ?? estimate.Message;
            return;
        }

        var costLine = estimate.Data.TotalTwd is { } total
            ? $"預估約 NT$ {total:0.##}。"
            : "有部署沒有設定單價或沒有匯率，無法完整預估費用。";
        if (!await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認轉錄比較（會產生費用）",
            Content = $"將把 {DescribeLength(seconds)} 的片段，用 {request.Variants.Count} 組條件各轉錄一次。{costLine}確定要繼續嗎？",
            OkText = "開始比較",
            CancelText = "取消",
            MaskClosable = false,
        }))
        {
            return;
        }

        isRunning = true;
        errorMessage = null;
        audioDataUrl = null;
        results.Clear();
        sentVariants = request.Variants;
        progressText = "正在剪片段…";
        runCancellation = new CancellationTokenSource();
        StateHasChanged();

        try
        {
            await MonitoringApi.RunTranscriptionCompareAsync(request, item => _ = InvokeAsync(() => OnEvent(item, request.Variants.Count)), runCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            errorMessage = "已停止，還沒跑的組沒有送出。";
        }
        catch (InvalidOperationException ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            isRunning = false;
            runCancellation?.Dispose();
            runCancellation = null;
        }
    }

    private void OnEvent(TranscriptionCompareEventDto item, int total)
    {
        if (item.Type == TranscriptionCompareEventDto.Excerpt)
        {
            audioDataUrl = item.AudioDataUrl;
            excerptSeconds = item.ExcerptSeconds ?? 0;
        }
        else if (item.Type == TranscriptionCompareEventDto.Result)
        {
            results.Add(item);
        }

        progressText = results.Count < total ? $"轉錄中（{results.Count}/{total}）…" : "完成";
        StateHasChanged();
    }

    private void Stop() => runCancellation?.Cancel();

    private async Task CopyAsync(string text)
    {
        if (await ClipboardInterop.CopyTextAsync(text))
        {
            _ = MessageService.SuccessAsync("已複製到剪貼簿");
        }
        else
        {
            _ = MessageService.WarningAsync("瀏覽器不允許自動複製，請選取文字後按 Ctrl+C。");
        }
    }

    private string DescribeVariant(int index)
    {
        if (index >= sentVariants.Count)
        {
            return string.Empty;
        }

        var variant = sentVariants[index];
        if (variant.Engine == 1)
        {
            return variant.Diarize ? $"講者上限 {variant.MaxSpeakers} 人、zh-TW、片語清單" : "zh-TW、片語清單";
        }

        var parts = new List<string> { string.IsNullOrWhiteSpace(variant.Language) ? "不指定語言" : $"語言 {variant.Language}" };
        if (!string.IsNullOrWhiteSpace(variant.Prompt))
        {
            parts.Add("有提示詞");
        }

        return string.Join("、", parts);
    }

    private static string DescribeMeeting(TranscriptionCompareMeetingDto meeting)
        => $"{meeting.MeetingDate:yyyy/MM/dd} {meeting.Title}"
            + (meeting.MediaDurationSeconds is { } total ? $"（{FormatClock(total)}）" : string.Empty);

    private static string DescribeLength(int value) => value < 60 ? $"{value} 秒" : $"{value / 60} 分鐘";

    private static string FormatClock(double value)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, value));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    public void Dispose() => runCancellation?.Cancel();
}
