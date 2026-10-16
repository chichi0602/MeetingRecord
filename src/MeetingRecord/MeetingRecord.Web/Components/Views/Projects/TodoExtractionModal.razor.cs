using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Web.Components.Commons;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.TodoExtraction;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Share.Helpers;

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

    [Inject]
    private ModalService ModalService { get; set; } = default!;

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

        /// <summary>
        /// AI 判斷這條就是本專案既有的那筆待辦（1.0.44）。後來的會議又講到同一件事、期限或負責人改了，
        /// 預設更新既有那筆，不另外新增一筆。
        /// </summary>
        public OpenTodoSummary? Matched { get; init; }

        /// <summary>有對應時：true＝更新既有（預設），false＝另外新增。</summary>
        public bool UpdateExisting { get; set; }

        public bool IsUpdating => Matched is not null && UpdateExisting;

        /// <summary>
        /// 更新既有時會改的地方（只看期限與負責人；沒填的不會清掉既有值，所以不算變更）。
        /// 依畫面上目前的值即時算，使用者改了期限或負責人，差異跟著變。
        /// </summary>
        public IReadOnlyList<string> Changes
        {
            get
            {
                if (Matched is null)
                {
                    return [];
                }

                var changes = new List<string>();
                if (DueDate is { } due && Matched.DueDate?.Date != due.Date)
                {
                    changes.Add($"期限 {Matched.DueDate?.ToString("yyyy/MM/dd") ?? "未設定"} → {due:yyyy/MM/dd}");
                }

                var owner = string.IsNullOrWhiteSpace(Owner) ? null : Owner.Trim();
                if (owner is not null && !string.Equals(Matched.Owner?.Trim(), owner, StringComparison.Ordinal))
                {
                    changes.Add($"負責人 {(string.IsNullOrWhiteSpace(Matched.Owner) ? "未指定" : Matched.Owner)} → {owner}");
                }

                return changes;
            }
        }
    }

    private readonly List<Candidate> candidates = [];

    /// <summary>
    /// 抽取完成當下的候選清單快照。null 代表還沒抽出東西（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。
    /// </summary>
    private string? candidateSnapshot;

    /// <summary>少了這道旗標，連按兩次取消會疊出兩個確認視窗。</summary>
    private bool isDiscardConfirming;

    private static IReadOnlyList<string> PriorityOptions => TodoAdapterModel.PriorityOptions;

    private bool isExtracting;
    private bool isSaving;
    private string? errorMessage;
    private string? existingNotice;

    /// <summary>已經抽過的會議，避免同一個對象每次重繪都再打一次 API。</summary>
    private int? loadedMeetingId;

    private int SelectedCount => candidates.Count(x => x.Selected);

    private int SelectedUpdateCount => candidates.Count(x => x.Selected && x.IsUpdating);

    private int SelectedAddCount => SelectedCount - SelectedUpdateCount;

    /// <summary>按鈕文字講清楚這次會新增幾條、更新幾條（1.0.44）。</summary>
    private string ConfirmText => (SelectedAddCount, SelectedUpdateCount) switch
    {
        (_, 0) => $"加入 {SelectedAddCount} 條待辦",
        (0, _) => $"更新 {SelectedUpdateCount} 條待辦",
        _ => $"加入 {SelectedAddCount} 條、更新 {SelectedUpdateCount} 條",
    };

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
        candidateSnapshot = null;
        errorMessage = null;
        existingNotice = null;
        isExtracting = true;
        StateHasChanged();

        try
        {
            var existingTitles = await ExtractionService.GetExistingTitlesAsync(MeetingId);

            // 1.0.44：先查好本專案未完成的待辦，同一份送給 AI 比對、也用在畫面的「對應既有」。
            var openTodos = await ExtractionService.GetOpenTodosAsync(MeetingId);
            var openById = openTodos.ToDictionary(x => x.Id);

            var extracted = await ExtractionService.ExtractAsync(MeetingId, openTodos: openTodos);

            foreach (var item in extracted)
            {
                var matched = item.MatchedTodoId is { } id && openById.TryGetValue(id, out var summary) ? summary : null;

                // 預設不勾選重複項，但仍然列出來——使用者要再追蹤同一件事就自己勾回去。
                var alreadyAdded = matched is null && existingTitles.Contains(item.Title.Trim());

                var candidate = new Candidate
                {
                    AlreadyAdded = alreadyAdded,
                    Title = item.Title,
                    Description = item.Description,
                    Owner = item.Owner,
                    DueDate = item.DueDate,
                    Priority = item.Priority,
                    Matched = matched,
                    UpdateExisting = matched is not null,
                };

                // 對應到既有、但期限負責人都沒變：這次會議只是又提到它，預設不勾（勾了也只是什麼都不改）。
                candidate.Selected = matched is not null ? candidate.Changes.Count > 0 : !alreadyAdded;
                candidates.Add(candidate);
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
            // 已達用量上限是預期中的狀況，記 Warning、不進系統例外紀錄（1.0.29）。
            if (ex is MeetingRecord.Business.Services.AiUsage.AiQuotaExceededException)
            {
                Logger.LogWarning("Todo extraction blocked by AI quota. MeetingId={MeetingId}", MeetingId);
            }
            else
            {
                Logger.LogError(ex, "Failed to extract todos. MeetingId={MeetingId}", MeetingId);
            }

            errorMessage = ex.Message;
        }
        finally
        {
            isExtracting = false;

            // ⚠️ 快照一定要拍在這裡，不能拍在開窗時——開窗的當下 candidates 還是空的，
            //    那樣「什麼都沒改」也會被判成 dirty，每次關閉都要多按一次。
            //    抽取失敗時 candidates 是空的，快照與比對都是 []，照樣不會誤問。
            candidateSnapshot = FormDirtyHelper.Capture(candidates);

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
        var updated = 0;
        var failures = new List<string>();

        try
        {
            foreach (var candidate in selected)
            {
                // 1.0.44：更新既有——只改期限與負責人，異動紀錄記下來源會議並通知專案成員。
                if (candidate.IsUpdating)
                {
                    var update = await TodoService.ApplyExtractedUpdateAsync(
                        candidate.Matched!.Id,
                        candidate.DueDate,
                        string.IsNullOrWhiteSpace(candidate.Owner) ? null : candidate.Owner.Trim(),
                        MeetingId,
                        MeetingTitle);

                    if (update.Success)
                    {
                        updated++;
                        candidates.Remove(candidate);
                    }
                    else
                    {
                        failures.Add($"「{candidate.Matched.Title}」：{update.Message}");
                    }

                    continue;
                }

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

                var result = await TodoService.AddAsync(model, AuditVia.AiExtract);
                if (result.Success)
                {
                    added++;

                    // 成功的就從清單拿掉（0.4.115）：部分失敗時它若還勾著，再按一次「加入」會重複建立。
                    candidates.Remove(candidate);
                }
                else
                {
                    failures.Add($"「{model.Title}」：{result.Message}");
                }
            }

            Logger.LogInformation(
                "Todos created from meeting draft. MeetingId={MeetingId}, ProjectId={ProjectId}, Added={Added}, Updated={Updated}, Failed={Failed}",
                MeetingId,
                ProjectId,
                added,
                updated,
                failures.Count);

            if (failures.Count > 0)
            {
                // 部分成功要講清楚成功幾條、哪幾條沒進去，不要只丟一句「失敗」。
                errorMessage = $"已加入 {added} 條、更新 {updated} 條，其餘未處理：{string.Join("；", failures)}";
                return;
            }

            await CloseAsync();
        }
        finally
        {
            isSaving = false;
        }
    }

    private async Task OnCancelAsync()
    {
        if (isSaving || isDiscardConfirming)
        {
            return;
        }

        if (FormDirtyHelper.IsDirty(candidateSnapshot, candidates))
        {
            isDiscardConfirming = true;
            bool discard;
            try
            {
                // ⚠️ 這裡的額外警語特別有價值：關掉之後重開會**再打一次付費 API**。
                discard = await FormDirtyHelper.ConfirmDiscardAsync(
                    ModalService,
                    "抽出的待辦清單",
                    "關閉之後再開啟會重新抽取一次，並且再計費一次。");
            }
            finally
            {
                isDiscardConfirming = false;
            }

            if (!discard)
            {
                // ⚠️ 這個視窗是**單向** Visible=（不是 @bind-Visible），AntDesign 不會自己關窗，
                //    所以直接 return 就留得住，不需要也不可以寫 Visible = true。
                return;
            }
        }

        await CloseAsync();
    }

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
