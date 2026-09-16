using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.TodoExtraction;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Web.Components.Views.Projects;

/// <summary>
/// 「AI 抽出待辦」的確認視窗。
///
/// <para>
/// 抽出來的東西**一律要人看過才寫入**——模型會把人名聽錯、把討論當成行動項目，
/// 直接落庫的話使用者得一條條進待辦頁去刪，比自己打還累。
/// </para>
/// </summary>
public partial class TodoExtractionModal : ComponentBase
{
    [Inject]
    private TodoExtractionService ExtractionService { get; set; } = default!;

    [Inject]
    private TodoService TodoService { get; set; } = default!;

    [Inject]
    private ILogger<TodoExtractionModal> Logger { get; set; } = default!;

    [Parameter]
    public bool Visible { get; set; }

    [Parameter]
    public EventCallback<bool> VisibleChanged { get; set; }

    /// <summary>來源會議。</summary>
    [Parameter]
    public int MeetingId { get; set; }

    /// <summary>抽出來的待辦要掛在哪個專案底下。</summary>
    [Parameter]
    public int ProjectId { get; set; }

    [Parameter]
    public string MeetingTitle { get; set; } = string.Empty;

    /// <summary>畫面上一列候選：候選內容本身可改，外加一個勾選狀態。</summary>
    private sealed class Candidate
    {
        public bool Selected { get; set; } = true;

        /// <summary>
        /// 這條的標題與本會議「先前已加入的待辦」重複。
        ///
        /// <para>
        /// 只影響<b>預設</b>勾選狀態與畫面上的標示，<b>不阻止</b>使用者勾回去儲存——
        /// 開完後續會議再追蹤同一件事是合理的。判定是在載入候選時做一次的快照，
        /// 使用者之後改了標題也不會重算（改過就不再是「同一條」，維持原標示反而誤導，
        /// 但重算需要每次輸入都查一次 DB，不值得）。
        /// </para>
        /// </summary>
        public bool AlreadyAdded { get; init; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? Owner { get; set; }

        public DateTime? DueDate { get; set; }

        public string Priority { get; set; } = TodoAdapterModel.PriorityOptions[1];
    }

    private readonly List<Candidate> candidates = [];

    private static IReadOnlyList<string> PriorityOptions => TodoAdapterModel.PriorityOptions;

    private bool isExtracting;
    private bool isSaving;
    private string? errorMessage;
    private string? existingNotice;

    /// <summary>已經抽過的會議，避免同一個對象每次重繪都再打一次 API。</summary>
    private int? loadedMeetingId;

    private int SelectedCount => candidates.Count(x => x.Selected);

    private bool AllSelected => candidates.Count > 0 && candidates.All(x => x.Selected);

    protected override async Task OnParametersSetAsync()
    {
        if (!Visible || MeetingId <= 0)
        {
            return;
        }

        if (loadedMeetingId == MeetingId)
        {
            return;
        }

        loadedMeetingId = MeetingId;
        await ExtractAsync();
    }

    private async Task ExtractAsync()
    {
        candidates.Clear();
        errorMessage = null;
        existingNotice = null;
        isExtracting = true;
        StateHasChanged();

        try
        {
            var existingTitles = await ExtractionService.GetExistingTitlesAsync(MeetingId);

            var extracted = await ExtractionService.ExtractAsync(MeetingId);

            foreach (var item in extracted)
            {
                // 預設不勾選重複項，但仍然列出來——使用者要再追蹤同一件事就自己勾回去。
                var alreadyAdded = existingTitles.Contains(item.Title.Trim());

                candidates.Add(new Candidate
                {
                    AlreadyAdded = alreadyAdded,
                    Selected = !alreadyAdded,
                    Title = item.Title,
                    Description = item.Description,
                    Owner = item.Owner,
                    DueDate = item.DueDate,
                    Priority = item.Priority,
                });
            }

            if (existingTitles.Count > 0)
            {
                var duplicateCount = candidates.Count(x => x.AlreadyAdded);

                // 只講總數在第二次抽出時沒有幫助，要講得出「這次有幾條重複」。
                existingNotice = duplicateCount > 0
                    ? $"這場會議先前已加入 {existingTitles.Count} 條待辦，其中 {duplicateCount} 條與這次抽出的重複，已標示「已加入過」並預設不勾選。"
                    : $"這場會議先前已加入 {existingTitles.Count} 條待辦，這次抽出的內容沒有重複。";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to extract todos. MeetingId={MeetingId}", MeetingId);
            errorMessage = ex.Message;
        }
        finally
        {
            isExtracting = false;
            StateHasChanged();
        }
    }

    private void ToggleAll(bool selected)
    {
        foreach (var candidate in candidates)
        {
            candidate.Selected = selected;
        }
    }

    /// <summary>
    /// Enter 送出。走的是與按鈕相同的 <see cref="OnConfirmAsync"/>，
    /// 所以「一條都沒勾」時照樣不會有動作（那顆按鈕本來就是 Disabled）。
    /// </summary>
    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (FormKeyboardHelper.IsSubmit(args))
        {
            await OnConfirmAsync();
        }
    }

    private async Task OnConfirmAsync()
    {
        var selected = candidates.Where(x => x.Selected).ToList();
        if (selected.Count == 0 || isSaving)
        {
            return;
        }

        isSaving = true;
        errorMessage = null;

        var added = 0;
        var failures = new List<string>();

        try
        {
            foreach (var candidate in selected)
            {
                if (string.IsNullOrWhiteSpace(candidate.Title))
                {
                    failures.Add("有一條待辦沒有標題，已略過。");
                    continue;
                }

                var model = TodoExtractionService.ToAdapterModel(
                    new ExtractedTodo(
                        candidate.Title.Trim(),
                        candidate.Description,
                        string.IsNullOrWhiteSpace(candidate.Owner) ? null : candidate.Owner.Trim(),
                        candidate.DueDate,
                        candidate.Priority),
                    ProjectId,
                    MeetingId);

                var check = await TodoService.BeforeAddCheckAsync(model);
                if (!check.Success)
                {
                    failures.Add($"「{model.Title}」：{check.Message}");
                    continue;
                }

                var result = await TodoService.AddAsync(model);
                if (result.Success)
                {
                    added++;
                }
                else
                {
                    failures.Add($"「{model.Title}」：{result.Message}");
                }
            }

            Logger.LogInformation(
                "Todos created from meeting draft. MeetingId={MeetingId}, ProjectId={ProjectId}, Added={Added}, Failed={Failed}",
                MeetingId,
                ProjectId,
                added,
                failures.Count);

            if (failures.Count > 0)
            {
                // 部分成功要講清楚成功幾條、哪幾條沒進去，不要只丟一句「失敗」。
                errorMessage = $"已加入 {added} 條，其餘未加入：{string.Join("；", failures)}";
                return;
            }

            await CloseAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task OnCancelAsync() => await CloseAsync();

    private async Task CloseAsync()
    {
        candidates.Clear();
        errorMessage = null;
        existingNotice = null;
        // 清掉才能讓同一場會議下次開啟時重新抽一次（開完後續會議可能有新決議）。
        loadedMeetingId = null;

        Visible = false;
        await VisibleChanged.InvokeAsync(false);
    }
}
