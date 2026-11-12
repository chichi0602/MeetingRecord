using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Client.Pages.PromptTemplates;

/// <summary>
/// 會議紀錄提示詞清單。資料走 <see cref="PromptTemplateApi"/>：
/// 名稱重複、內建範本、分類正規化等規則都在伺服器（PromptTemplateService），未知變數的提醒在前端（<see cref="PromptVariableHelper"/>，只提醒、不阻擋）。
/// </summary>
public partial class PromptTemplatePage
{
    [Inject] private ILogger<PromptTemplatePage> logger { get; set; } = default!;
    [Inject] private PromptTemplateApi PromptTemplateApi { get; set; } = default!;
    [Inject] private PermissionView PermissionView { get; set; } = default!;
    [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
    [Inject] private ModalService modalService { get; set; } = default!;
    [Inject] private MessageService messageService { get; set; } = default!;
    [Inject] private NotificationService notificationService { get; set; } = default!;

    private ITable? table;

    private List<string> availableCategories = [];
    private List<string> presetNames = [];
    private string selectedPresetName = string.Empty;
    private bool isSeedingPresets;
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<PromptTemplateDto> promptTemplates = [];

    private string modalTitle = "提示詞維護";
    private bool modalVisible;
    private PromptTemplateDto CurrentRecord = NewRecord();

    /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
    private string? formSnapshot;

    /// <summary>
    /// ⚠️ Esc 會**同時**走兩條路：AntDesign Modal 的 <c>Keyboard="true"</c> 與表單上的
    /// <c>@onkeydown</c>，兩者都會呼叫 <see cref="OnModalCancelHandleAsync"/>。沒有這道旗標會**疊出兩個確認視窗**。
    /// </summary>
    private bool isDiscardConfirming;
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private static string KnownVariableDescription => PromptVariableHelper.DescribeKnownVariables();

    private bool CanCreate => PermissionView.Can(MagicObjectHelper.角色_提示詞清單, PermissionActions.Create);

    protected override async Task OnInitializedAsync()
    {
        // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
        await PermissionView.LoadAsync();
        if (!PermissionView.CanAccessPage(MagicObjectHelper.角色_提示詞清單))
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await PageAccessApi.RecordPageDeniedAsync("/prompttemplates");
            logger.LogWarning("Prompt template page denied because current user has not this role permission.");
            return;
        }

        availableCategories = (await PromptTemplateApi.GetEnabledCategoriesAsync()).Data ?? [];
        if (CanCreate)
        {
            presetNames = (await PromptTemplateApi.GetPresetNamesAsync()).Data ?? [];
        }

        await ReloadAsync();
    }

    private static PromptTemplateDto NewRecord() => new() { Id = 0, IsEnabled = true, CategoryNames = [] };

    public async Task ReloadAsync()
    {
        var result = await PromptTemplateApi.SearchAsync(new PromptTemplateSearchRequestDto
        {
            Keyword = searchText,
            SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
            SortDescending = sortDirection == "Descending",
            PageIndex = _pageIndex,
            PageSize = _pageSize,
        });

        if (!result.Success || result.Data is not { } page)
        {
            ShowError(result);
            return;
        }

        promptTemplates = page.Items;
        _total = page.TotalCount;
        // 全選只算這一頁（1.0.53）：換頁、排序、搜尋、刪除後都會走到這裡，勾選一律清掉。
        selectedRows = [];

        // 頁碼有可能落在最後一頁之後（例如停在第 2 頁時把該頁唯一一筆刪掉）：夾回最後一頁重載一次（0.4.64）。
        if (promptTemplates.Count == 0 && _total > 0 && _pageIndex > 1)
        {
            _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
            await ReloadAsync();
            return;
        }

        StateHasChanged();
    }

    /// <summary>一次建立全部內建範本。冪等（已存在同名者略過），所以可以重複按。</summary>
    private async Task OnAddAllPresetsAsync()
    {
        if (isSeedingPresets)
        {
            return;
        }

        isSeedingPresets = true;
        StateHasChanged();

        try
        {
            var result = await PromptTemplateApi.AddAllPresetsAsync();
            if (result.Success)
            {
                NotifySuccess(result.Message);
            }
            else
            {
                ShowError(result);
            }

            await ReloadAsync();
        }
        finally
        {
            isSeedingPresets = false;
            StateHasChanged();
        }
    }

    /// <summary>只新增下拉選取的那一個內建範本（已存在就不新增，伺服器回原因）。</summary>
    private async Task OnAddSelectedPresetAsync()
    {
        if (string.IsNullOrWhiteSpace(selectedPresetName))
        {
            return;
        }

        var name = selectedPresetName;
        var result = await PromptTemplateApi.AddPresetAsync(name);
        if (!Succeeded(result))
        {
            return;
        }

        NotifySuccess($"已新增「{name}」。");
        await ReloadAsync();
    }

    /// <summary>清單上直接切換啟用狀態。啟用不是破壞性動作，所以只有停用套紅色確認鈕。</summary>
    private async Task OnToggleEnabledAsync(PromptTemplateDto item)
    {
        var willEnable = !item.IsEnabled;

        var confirmOptions = new ConfirmOptions
        {
            Title = willEnable ? "確認啟用" : "確認停用",
            Content = willEnable
                ? $"確定要啟用「{item.Name}」嗎？啟用後會出現在產生會議紀錄時的提示詞選單中。"
                : $"確定要停用「{item.Name}」嗎？停用後不會出現在產生會議紀錄時的提示詞選單中，已產生的會議紀錄不受影響。",
            OkText = willEnable ? "啟用" : "停用",
            CancelText = "取消",
            MaskClosable = false
        };

        if (!willEnable)
        {
            confirmOptions.OkButtonProps = new ButtonProps { Danger = true };
        }

        if (!await modalService.ConfirmAsync(confirmOptions))
        {
            return;
        }

        if (!Succeeded(await PromptTemplateApi.SetEnabledAsync(item.Id ?? 0, willEnable)))
        {
            return;
        }

        NotifySuccess(willEnable ? $"已啟用「{item.Name}」。" : $"已停用「{item.Name}」。");
        await ReloadAsync();
    }

    private void NotifySuccess(string description)
    {
        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private void OnRecordCategoriesChanged(IEnumerable<string> values)
    {
        CurrentRecord.CategoryNames = values?.ToList() ?? [];
    }

    private async Task OnTableChange(QueryModel<PromptTemplateDto> args)
    {
        _pageIndex = args.PageIndex;
        // 明確賦值，不賭 @bind-PageSize 與 AntDesign 內部 callback 的先後順序。
        _pageSize = args.PageSize;

        var sorted = args.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
        if (sorted is not null)
        {
            // 欄位名就是 Column 的 DataIndex（Name、IsEnabled、UpdatedAt），伺服器不分大小寫認得。
            sortField = sorted.FieldName ?? string.Empty;
            sortDirection = sorted.SortDirection.ToString();
        }
        else
        {
            sortField = string.Empty;
            sortDirection = "None";
        }

        await ReloadAsync();
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await ReloadAsync();

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = "已更新最新資料",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private Task OnEditAsync(PromptTemplateDto item)
    {
        isNewRecordMode = false;
        modalTitle = "修改提示詞";
        CurrentRecord = item.Clone();
        CurrentRecord.CategoryNames ??= [];
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
        return Task.CompletedTask;
    }

    #region 多選刪除（1.0.53）

    /// <summary>目前這一頁勾選的列；換頁、排序、搜尋、重新載入時清掉（ReloadAsync）。</summary>
    private IEnumerable<PromptTemplateDto> selectedRows = [];

    private bool isBatchDeleting;

    private bool CanDelete => PermissionView.Can(MagicObjectHelper.角色_提示詞清單, PermissionActions.Delete);

    private async Task OnBatchDeleteAsync()
    {
        var items = selectedRows.ToList();
        if (items.Count == 0 || isBatchDeleting)
        {
            return;
        }

        var ok = await modalService.ConfirmAsync(new ConfirmOptions()
        {
            Title = "確認刪除",
            Content = $"確定要刪除勾選的 {items.Count} 個提示詞嗎？此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });
        if (!ok)
        {
            return;
        }

        isBatchDeleting = true;
        try
        {
            var result = await BatchRunner.RunAsync(items, x => x.Name, async x =>
            {
                var r = await PromptTemplateApi.DeleteAsync(x.Id ?? 0);
                return r.Success ? null : ErrorText(r);
            });
            BatchResultNotifier.Notify(notificationService, result, "刪除");
        }
        finally
        {
            isBatchDeleting = false;
        }

        await ReloadAsync();
    }

    #endregion

    private async Task OnDeleteAsync(PromptTemplateDto item)
    {
        var ok = await modalService.ConfirmAsync(new ConfirmOptions()
        {
            Title = "確認刪除",
            Content = "確定要刪除這筆紀錄嗎？此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            return;
        }

        if (!Succeeded(await PromptTemplateApi.DeleteAsync(item.Id ?? 0)))
        {
            return;
        }

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = "刪除成功",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });

        await ReloadAsync();
    }

    private Task OnAddAsync()
    {
        CurrentRecord = NewRecord();
        isNewRecordMode = true;
        modalTitle = "新增提示詞";
        formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
        modalVisible = true;
        return Task.CompletedTask;
    }

    /// <summary>表單送出閘門（1.0.62）：「確定」與 Enter 共用，等候回寫的那段時間也算送出中，連按不會新增兩筆。</summary>
    private readonly FormSubmitGate submitGate = new();

    private Task OnModalOKHandleAsync(MouseEventArgs args) => submitGate.RunAsync(SaveModalAsync);

    private async Task SaveModalAsync()
    {
        // 1.1.34：視窗已經關了就不再存。存檔成功時「關窗」與「不再存檔中」在同一次重繪才生效，
        // 關窗動畫期間「確定」又按得到，沒有這行同一筆會再存一次（新增兩筆）。
        if (!modalVisible)
        {
            return;
        }

        if (LocalEditContext?.Validate() == false)
        {
            foreach (var error in LocalEditContext.GetValidationMessages())
            {
                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "驗證失敗",
                    Description = error,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight,
                    Duration = 5
                });
            }

            modalVisible = true;
            return;
        }

        // 名稱重複（409）、找不到（404）等都由伺服器判斷；失敗時表單留著，訊息照原本的文字顯示。
        var saved = isNewRecordMode
            ? Succeeded(await PromptTemplateApi.CreateAsync(CurrentRecord))
            : Succeeded(await PromptTemplateApi.UpdateAsync(CurrentRecord));
        if (!saved)
        {
            modalVisible = true;
            return;
        }

        NotifyUnknownVariables();

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = isNewRecordMode ? "新增成功" : "修改成功",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });

        if (isNewRecordMode)
        {
            _ = messageService.SuccessAsync("新增成功");
        }

        await ReloadAsync();
        modalVisible = false;
    }

    /// <summary>
    /// 提示詞內容含不支援的變數時發出警告。刻意「只提醒、不阻擋儲存」——範本作者可能先寫下尚未支援的佔位符。
    /// </summary>
    private void NotifyUnknownVariables()
    {
        var unknownVariables = PromptVariableHelper.FindUnknownVariables(CurrentRecord.Content);
        if (unknownVariables.Count == 0)
        {
            return;
        }

        var unknownText = string.Join("、", unknownVariables.Select(x => $"{{{{{x}}}}}"));
        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "提示詞變數提醒",
            Description = $"以下變數不在支援清單中，產生會議紀錄時將原樣輸出：{unknownText}。目前支援的變數：{KnownVariableDescription}",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
            Duration = 8
        });
    }

    private async Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        if (isDiscardConfirming)
        {
            return;
        }

        if (FormDirtyHelper.IsDirty(formSnapshot, CurrentRecord))
        {
            isDiscardConfirming = true;
            bool discard;
            try
            {
                discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這筆提示詞");
            }
            finally
            {
                isDiscardConfirming = false;
            }

            if (!discard)
            {
                // 開關由本頁決定（1.1.19 起視窗是單向繫結）：按「繼續編修」就維持開著。
                modalVisible = true;
                return;
            }
        }

        modalVisible = false;
        formSnapshot = null;
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        // ⚠️ 一律走 FormKeyboardHelper：它會擋掉中文輸入法組字中的 Enter（選字用的那一下），
        // 也讓 Shift+Enter 落回瀏覽器原生的換行（提示詞內容是多行文字）。直接比對 args.Key 會誤觸。
        if (FormKeyboardHelper.IsSubmit(args))
        {
            // 等候不是可以省的：AntDesign Input 預設 change/blur 才回寫繫結值，Enter 送出時焦點還在欄位裡，不等就會拿到舊值。
            // 等候也算在閘門裡（1.0.62）：等候期間按「確定」不會新增兩筆。
            await submitGate.RunAsync(SaveModalAsync, FormSubmitGate.EnterSettleDelay);
        }
        else if (FormKeyboardHelper.IsCancel(args))
        {
            await OnModalCancelHandleAsync(new MouseEventArgs());
        }
    }

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }

    /// <summary>API 的失敗原因在 <c>ErrorMessage</c>（<c>Message</c> 只是「操作失敗」之類的通稱）。</summary>
    private static string ErrorText<T>(ApiResult<T> result) => result.ErrorMessage ?? result.Message;

    private bool Succeeded<T>(ApiResult<T> result)
    {
        if (!result.Success)
        {
            ShowError(result);
        }

        return result.Success;
    }

    private void ShowError<T>(ApiResult<T> result)
    {
        if (result.StatusCode == 401)
        {
            return; // ApiClient 已導到登入頁。
        }

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = ErrorText(result),
            NotificationType = NotificationType.Error,
            Placement = NotificationPlacement.BottomRight,
            Duration = 5
        });
    }
}
