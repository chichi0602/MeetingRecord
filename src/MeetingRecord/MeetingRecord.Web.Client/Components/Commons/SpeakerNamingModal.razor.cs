using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Web.Client.Api;
using Microsoft.AspNetCore.Components;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 「標示講者」視窗（1.1.64）：把逐字稿的「講者 1～4」對到真名。
///
/// <para>
/// 跟 <see cref="MarkdownEditorModal"/> 一樣不注入 API：會議頁打 <c>api/v1/Meeting/{id}/speakers</c>，
/// 專案頁打 <c>api/v1/Project/{projectId}/meetings/{meetingId}/speakers</c>，讀取與存檔都由父層傳進來。
/// </para>
/// </summary>
public partial class SpeakerNamingModal
{
    /// <summary>datalist 的 id。同一頁只會開一個這個視窗，固定字串就好。</summary>
    private const string RosterId = "speaker-naming-roster";

    [Parameter] public bool Visible { get; set; }

    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }

    /// <summary>讀出講者、目前的對照與名冊。</summary>
    [Parameter] public Func<Task<ApiResult<MeetingSpeakersDto>>>? Load { get; set; }

    /// <summary>存檔，鍵是編號、值是名字（空字串＝不對照）。第二個參數 1.1.69：存好後要不要產生會議紀錄。</summary>
    [Parameter] public Func<Dictionary<string, string>, bool, Task<ApiResult<object>>>? Save { get; set; }

    /// <summary>存檔成功：父層重新讀逐字稿，畫面才會換成真名。參數是這次有沒有開始產生會議紀錄（1.1.69）。</summary>
    [Parameter] public EventCallback<bool> OnSaved { get; set; }

    /// <summary>1.1.71：講者代號 → 試聽片段網址（給 &lt;audio src&gt;）。沒給就不顯示播放器。</summary>
    [Parameter] public Func<string, string>? ClipUrl { get; set; }

    private sealed class SpeakerRow
    {
        public string Label { get; init; } = string.Empty;

        public string? Name { get; set; }

        public List<string> Samples { get; init; } = [];

        public bool HasClip { get; init; }
    }

    private List<SpeakerRow> speakers = [];
    private List<string> participants = [];
    private bool hasSegmentLabels;

    /// <summary>1.1.69：轉錄完在等標示講者，存檔後會自動產生會議紀錄。</summary>
    private bool canGenerate;
    private bool isLoading;
    private bool isSaving;
    private string? errorMessage;
    private bool wasVisible;

    protected override async Task OnParametersSetAsync()
    {
        // 每次打開都重讀：名字可能剛在別的分頁改過，逐字稿也可能剛重新轉錄過。
        if (Visible && !wasVisible)
        {
            wasVisible = true;
            await LoadAsync();
        }
        else if (!Visible)
        {
            wasVisible = false;
        }
    }

    private async Task LoadAsync()
    {
        speakers = [];
        participants = [];
        hasSegmentLabels = false;
        canGenerate = false;
        errorMessage = null;

        if (Load is null)
        {
            return;
        }

        isLoading = true;
        StateHasChanged();
        try
        {
            var result = await Load();
            if (!result.Success || result.Data is not { } data)
            {
                errorMessage = ApiClient.ErrorText(result);
                return;
            }

            speakers = data.Speakers
                .Select(x => new SpeakerRow { Label = x.Label, Name = x.Name, Samples = x.Samples, HasClip = x.HasClip })
                .ToList();
            participants = data.Participants;
            hasSegmentLabels = data.HasSegmentLabels;
            canGenerate = data.CanGenerate;
        }
        finally
        {
            isLoading = false;
        }
    }

    private Task SaveAsync() => SubmitAsync(generate: false, skipNames: false);

    /// <summary>1.1.69：存名字並用上傳時選的提示詞產生會議紀錄（只付一次產生費用）。</summary>
    private Task SaveAndGenerateAsync() => SubmitAsync(generate: true, skipNames: false);

    /// <summary>1.1.69：不標名字，直接產生（會議紀錄裡維持「講者 N」）。</summary>
    private Task SkipAndGenerateAsync() => SubmitAsync(generate: true, skipNames: true);

    private async Task SubmitAsync(bool generate, bool skipNames)
    {
        if (Save is null || isSaving)
        {
            return;
        }

        isSaving = true;
        errorMessage = null;
        try
        {
            var names = speakers.ToDictionary(x => x.Label, x => skipNames ? string.Empty : x.Name?.Trim() ?? string.Empty);
            var result = await Save(names, generate);
            if (!result.Success)
            {
                errorMessage = ApiClient.ErrorText(result);
                return;
            }

            await OnSaved.InvokeAsync(generate);
            await CloseAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    private Task CloseAsync() => VisibleChanged.InvokeAsync(false);
}
