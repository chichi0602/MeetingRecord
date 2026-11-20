using AntDesign;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Meetings;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using Microsoft.AspNetCore.Components;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// 「版本紀錄」視窗（1.1.71）：會議紀錄被重新產生、編修、還原前留下的舊版。
///
/// <para>
/// 跟 <see cref="SpeakerNamingModal"/> 一樣不注入 API：會議頁打 <c>api/v1/Meeting/{id}/draft/versions</c>，
/// 專案頁打 <c>api/v1/Project/{projectId}/meetings/{meetingId}/draft/versions</c>，都由父層傳進來。
/// <see cref="Restore"/> 是 null 時（沒有修改權限、唯讀專案）只能看、不能還原。
/// </para>
/// </summary>
public partial class DraftVersionsModal
{
    /// <summary>畫面上的說明用；權威值是伺服器的 <c>MeetingDraftVersion.MaxPerMeeting</c>。</summary>
    private const int MaxVersions = 20;

    [Inject] private ModalService ModalService { get; set; } = default!;

    [Parameter] public bool Visible { get; set; }

    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }

    [Parameter] public Func<Task<ApiResult<List<MeetingDraftVersionDto>>>>? Load { get; set; }

    [Parameter] public Func<int, Task<ApiResult<MeetingDraftVersionDetailDto>>>? LoadDetail { get; set; }

    /// <summary>還原某一版；null＝只能看。</summary>
    [Parameter] public Func<int, Task<ApiResult<object>>>? Restore { get; set; }

    /// <summary>還原成功：父層重新讀會議紀錄。</summary>
    [Parameter] public EventCallback OnRestored { get; set; }

    private List<MeetingDraftVersionDto> versions = [];
    private MeetingDraftVersionDetailDto? selected;
    private string selectedHtml = string.Empty;
    private bool isLoading;
    private bool isLoadingDetail;
    private bool isRestoring;
    private string? errorMessage;
    private bool wasVisible;

    protected override async Task OnParametersSetAsync()
    {
        // 每次打開都重讀：剛剛可能才重新產生或編修過。
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
        versions = [];
        selected = null;
        selectedHtml = string.Empty;
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

            versions = data;
        }
        finally
        {
            isLoading = false;
        }

        if (versions.Count > 0)
        {
            await SelectAsync(versions[0].Id);
        }
    }

    private async Task SelectAsync(int versionId)
    {
        if (LoadDetail is null || isLoadingDetail || selected?.Id == versionId)
        {
            return;
        }

        isLoadingDetail = true;
        errorMessage = null;
        StateHasChanged();
        try
        {
            var result = await LoadDetail(versionId);
            if (!result.Success || result.Data is not { } data)
            {
                errorMessage = ApiClient.ErrorText(result);
                return;
            }

            selected = data;
            selectedHtml = MarkdownRenderer.ToHtml(data.Content);
        }
        finally
        {
            isLoadingDetail = false;
        }
    }

    private async Task RestoreAsync()
    {
        if (Restore is null || selected is null || isRestoring)
        {
            return;
        }

        var ok = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "還原這一版",
            Content = $"確定要把會議紀錄換回 {selected.SavedAt:yyyy/MM/dd HH:mm}（{selected.ReasonText}）的內容嗎？目前的內容會先留成一版，之後還能再換回來。",
            OkText = "還原",
            CancelText = "取消",
            MaskClosable = false,
        });
        if (!ok)
        {
            return;
        }

        isRestoring = true;
        errorMessage = null;
        try
        {
            var result = await Restore(selected.Id);
            if (!result.Success)
            {
                errorMessage = ApiClient.ErrorText(result);
                return;
            }

            await OnRestored.InvokeAsync();
            await CloseAsync();
        }
        finally
        {
            isRestoring = false;
        }
    }

    private Task CloseAsync() => VisibleChanged.InvokeAsync(false);
}
