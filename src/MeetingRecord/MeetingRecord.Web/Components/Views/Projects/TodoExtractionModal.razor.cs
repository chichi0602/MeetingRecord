using Microsoft.AspNetCore.Components;
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
            var existingCount = await ExtractionService.CountExistingTodosAsync(MeetingId);
            if (existingCount > 0)
            {
                // 只提示不阻擋——開完後續會議再抽一次新決議是合理的用法。
                existingNotice = $"這場會議先前已加入 {existingCount} 條待辦，注意不要重複加入。";
            }

            var extracted = await ExtractionService.ExtractAsync(MeetingId);

            foreach (var item in extracted)
            {
                candidates.Add(new Candidate
                {
                    Title = item.Title,
                    Description = item.Description,
                    Owner = item.Owner,
                    DueDate = item.DueDate,
                    Priority = item.Priority,
                });
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
