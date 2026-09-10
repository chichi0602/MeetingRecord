using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.PromptTemplates;

public partial class PromptTemplateViewView
{
    private readonly ILogger<PromptTemplateViewView> logger;
    private readonly PromptTemplateService promptTemplateService;
    private readonly CategoryService categoryService;
    private readonly TeamService teamService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private ITable? table;

    private List<string> availableCategories = [];
    private List<string> availableTeams = [];
    private string selectedPresetName = string.Empty;
    private bool isSeedingPresets;
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<PromptTemplateAdapterModel> promptTemplateAdapterModels = [];

    private string modalTitle = "提示詞維護";
    private bool modalVisible;
    private PromptTemplateAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private static string KnownVariableDescription => PromptVariableHelper.DescribeKnownVariables();

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public PromptTemplateViewView(
        ILogger<PromptTemplateViewView> logger,
        PromptTemplateService promptTemplateService,
        CategoryService categoryService,
        TeamService teamService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.promptTemplateService = promptTemplateService;
        this.categoryService = categoryService;
        this.teamService = teamService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
    }

    protected override async Task OnInitializedAsync()
    {
        logger.LogInformation("Initializing prompt template management view.");
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            logger.LogWarning("Prompt template view initialization stopped because authentication check failed.");
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_提示詞清單) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            logger.LogWarning("Prompt template view denied because current user has not this role permission.");
            return;
        }

        availableCategories = await categoryService.GetAllEnabledNamesAsync();
        availableTeams = await teamService.GetAllEnabledNamesAsync();

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        logger.LogDebug(
            "Reloading prompt templates. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
            searchText,
            sortField,
            sortDirection,
            _pageIndex,
            _pageSize);

        DataRequestResult<PromptTemplateAdapterModel> dataRequestResult = await promptTemplateService.GetAsync(new DataRequest
        {
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = 0,
        });

        promptTemplateAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;

        // 分頁修好之後（0.4.64）頁碼有可能落在最後一頁之後——例如停在第 2 頁時把該頁
        // 唯一一筆刪掉，Skip 就會跳過全部資料而顯示空白表格。夾回最後一頁重載一次。
        if (promptTemplateAdapterModels.Count == 0 && _total > 0 && _pageIndex > 1)
        {
            _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
            logger.LogDebug("Prompt template page index clamped to the last page. PageIndex={PageIndex}", _pageIndex);
            await ReloadAsync();
            return;
        }

        logger.LogInformation("Prompt template list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
    }

    /// <summary>
    /// 一次建立全部內建範本。冪等（已存在同名者略過），所以可以重複按。
    /// </summary>
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
            logger.LogInformation("Applying prompt template presets from view.");
            var result = await promptTemplateService.AddPresetsAsync();

            if (result.Success)
            {
                NotifySuccess(result.Message);
            }
            else
            {
                NotifyError(result.Message);
            }

            await ReloadAsync();
        }
        finally
        {
            isSeedingPresets = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// 只新增下拉選取的那一個內建範本。走與 Modal 送出相同的 BeforeAddCheck ＋ Add 路徑。
    /// </summary>
    private async Task OnAddSelectedPresetAsync()
    {
        var preset = PromptTemplatePresets.All.FirstOrDefault(x => x.Name == selectedPresetName);
        if (preset is null)
        {
            return;
        }

        var model = new PromptTemplateAdapterModel
        {
            Name = preset.Name,
            Content = preset.Content,
            Description = preset.Description,
            IsEnabled = true,
        };

        var checkResult = await promptTemplateService.BeforeAddCheckAsync(model);
        if (!checkResult.Success)
        {
            // 名稱唯一性是全域的、可見性卻是團隊範圍，所以撞名的那一筆有可能根本
            // 不在這位使用者的清單上。訊息要講清楚，不然會被當成系統壞了。
            logger.LogInformation("Prompt template preset skipped because the name already exists. Name={Name}", preset.Name);
            NotifyError($"「{preset.Name}」已存在，未新增。（同名提示詞可能屬於其他團隊而未顯示在清單上）");
            return;
        }

        var result = await promptTemplateService.AddAsync(model);
        if (!result.Success)
        {
            NotifyError(result.Message);
            return;
        }

        logger.LogInformation("Prompt template preset added. Name={Name}", preset.Name);
        NotifySuccess($"已新增「{preset.Name}」。");
        await ReloadAsync();
    }

    /// <summary>
    /// 清單上直接切換啟用狀態。啟用不是破壞性動作，所以只有停用套紅色確認鈕。
    /// </summary>
    private async Task OnToggleEnabledAsync(PromptTemplateAdapterModel promptTemplateAdapterModel)
    {
        var willEnable = !promptTemplateAdapterModel.IsEnabled;
        logger.LogInformation(
            "Toggle prompt template enabled state requested. PromptTemplateId={PromptTemplateId}, WillEnable={WillEnable}",
            promptTemplateAdapterModel.Id, willEnable);

        var confirmOptions = new ConfirmOptions
        {
            Title = willEnable ? "確認啟用" : "確認停用",
            Content = willEnable
                ? $"確定要啟用「{promptTemplateAdapterModel.Name}」嗎？啟用後會出現在產生會議紀錄時的提示詞選單中。"
                : $"確定要停用「{promptTemplateAdapterModel.Name}」嗎？停用後不會出現在產生會議紀錄時的提示詞選單中，已產生的會議紀錄不受影響。",
            OkText = willEnable ? "啟用" : "停用",
            CancelText = "取消",
            MaskClosable = false
        };

        if (!willEnable)
        {
            confirmOptions.OkButtonProps = new ButtonProps { Danger = true };
        }

        var ok = await modalService.ConfirmAsync(confirmOptions);
        if (!ok)
        {
            logger.LogDebug("Prompt template enabled state toggle cancelled by user. PromptTemplateId={PromptTemplateId}", promptTemplateAdapterModel.Id);
            return;
        }

        var result = await promptTemplateService.SetEnabledAsync(promptTemplateAdapterModel.Id, willEnable);
        if (!result.Success)
        {
            NotifyError(result.Message);
            return;
        }

        NotifySuccess(willEnable ? $"已啟用「{promptTemplateAdapterModel.Name}」。" : $"已停用「{promptTemplateAdapterModel.Name}」。");
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

    private void NotifyError(string description)
    {
        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = description,
            NotificationType = NotificationType.Error,
            Placement = NotificationPlacement.BottomRight,
            Duration = 5
        });
    }

    private void OnRecordCategoriesChanged(IEnumerable<string> values)
    {
        CurrentRecord.Categories = values?.ToList() ?? [];
    }

    private void OnRecordTeamsChanged(IEnumerable<string> values)
    {
        CurrentRecord.Teams = values?.ToList() ?? [];
    }

    private async Task OnTableChange(QueryModel<PromptTemplateAdapterModel> args)
    {
        _pageIndex = args.PageIndex;

        if (args.SortModel?.Any() == true)
        {
            var tableSortModel = GetCurrentSortModel(args.SortModel);
            string sortValue = tableSortModel.SortDirection.ToString() ?? string.Empty;
            string resolvedSortField = ResolveSortFieldName(tableSortModel);
            sortDirection = sortValue;
            sortField = resolvedSortField;
        }
        else
        {
            sortField = string.Empty;
            sortDirection = "None";
        }

        logger.LogDebug("Prompt template table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
        await ReloadAsync();
    }

    private static ITableSortModel GetCurrentSortModel(IEnumerable<ITableSortModel> sortModels)
    {
        return sortModels.FirstOrDefault(model => HasSortDirection(model.SortDirection))
            ?? sortModels.Last();
    }

    private static bool HasSortDirection(SortDirection sortDirection)
    {
        return sortDirection == SortDirection.Ascending || sortDirection == SortDirection.Descending;
    }

    private static string ResolveSortFieldName(ITableSortModel sortModel)
    {
        if (!string.IsNullOrWhiteSpace(sortModel.FieldName))
        {
            return sortModel.FieldName;
        }

        object? column = sortModel.GetType().GetProperty("Column")?.GetValue(sortModel);
        if (column is null)
        {
            return string.Empty;
        }

        string? columnFieldName = column.GetType().GetProperty("FieldName")?.GetValue(column)?.ToString();
        if (!string.IsNullOrWhiteSpace(columnFieldName))
        {
            return columnFieldName;
        }

        object? dataIndex = column.GetType().GetProperty("DataIndex")?.GetValue(column);
        return dataIndex?.ToString() ?? string.Empty;
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        logger.LogInformation("Prompt template search triggered. Search={Search}", searchText);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Prompt template refresh triggered.");
        await ReloadAsync();

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "系統訊息",
            Description = "已更新最新資料",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private Task OnEditAsync(PromptTemplateAdapterModel promptTemplateAdapterModel)
    {
        isNewRecordMode = false;
        modalTitle = "修改提示詞";
        CurrentRecord = promptTemplateAdapterModel.Clone();
        modalVisible = true;
        logger.LogInformation("Opened edit modal for prompt template. PromptTemplateId={PromptTemplateId}, Name={Name}", promptTemplateAdapterModel.Id, promptTemplateAdapterModel.Name);
        return Task.CompletedTask;
    }

    private async Task OnDeleteAsync(PromptTemplateAdapterModel promptTemplateAdapterModel)
    {
        logger.LogInformation("Delete prompt template requested. PromptTemplateId={PromptTemplateId}, Name={Name}", promptTemplateAdapterModel.Id, promptTemplateAdapterModel.Name);

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
            logger.LogDebug("Prompt template delete cancelled by user. PromptTemplateId={PromptTemplateId}", promptTemplateAdapterModel.Id);
            return;
        }

        await promptTemplateService.DeleteAsync(promptTemplateAdapterModel.Id);
        logger.LogInformation("Prompt template delete completed. PromptTemplateId={PromptTemplateId}", promptTemplateAdapterModel.Id);

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
        CurrentRecord = new();
        isNewRecordMode = true;
        modalTitle = "新增提示詞";
        modalVisible = true;
        logger.LogInformation("Opened create modal for prompt template.");
        return Task.CompletedTask;
    }

    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        if (LocalEditContext?.Validate() == false)
        {
            IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
            foreach (var error in allErrors)
            {
                logger.LogWarning("Prompt template form validation failed. Error={Error}", error);
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

        if (isNewRecordMode)
        {
            var beforeAddCheckResult = await promptTemplateService.BeforeAddCheckAsync(CurrentRecord);
            if (!beforeAddCheckResult.Success)
            {
                logger.LogWarning("Prompt template create pre-check failed. Name={Name}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = beforeAddCheckResult.Message,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });

                modalVisible = true;
                return;
            }

            NotifyUnknownVariables();

            CurrentRecord.CreatedAt = DateTime.Now;
            CurrentRecord.UpdatedAt = DateTime.Now;

            await promptTemplateService.AddAsync(CurrentRecord);
            logger.LogInformation("Prompt template create submitted. Name={Name}", CurrentRecord.Name);

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "新增成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

            _ = messageService.SuccessAsync("新增成功");
        }
        else
        {
            var beforeUpdateCheckResult = await promptTemplateService.BeforeUpdateCheckAsync(CurrentRecord);
            if (!beforeUpdateCheckResult.Success)
            {
                logger.LogWarning("Prompt template update pre-check failed. PromptTemplateId={PromptTemplateId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = beforeUpdateCheckResult.Message,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });

                modalVisible = true;
                return;
            }

            NotifyUnknownVariables();

            CurrentRecord.UpdatedAt = DateTime.Now;
            await promptTemplateService.UpdateAsync(CurrentRecord);
            logger.LogInformation("Prompt template update submitted. PromptTemplateId={PromptTemplateId}, Name={Name}", CurrentRecord.Id, CurrentRecord.Name);

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "修改成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });
        }

        await ReloadAsync();
        modalVisible = false;
    }

    /// <summary>
    /// 提示詞內容含不支援的變數時發出警告。刻意「只提醒、不阻擋儲存」——
    /// 範本作者可能先寫下尚未支援的佔位符。
    /// </summary>
    private void NotifyUnknownVariables()
    {
        var unknownVariables = PromptVariableHelper.FindUnknownVariables(CurrentRecord.Content);
        if (unknownVariables.Count == 0)
        {
            return;
        }

        var unknownText = string.Join("、", unknownVariables.Select(x => $"{{{{{x}}}}}"));
        logger.LogInformation(
            "Prompt template contains unsupported variables. Name={Name}, UnknownVariables={UnknownVariables}",
            CurrentRecord.Name,
            unknownText);

        _ = notificationService.Open(new NotificationConfig()
        {
            Message = "提示詞變數提醒",
            Description = $"以下變數不在支援清單中，產生會議紀錄時將原樣輸出：{unknownText}。目前支援的變數：{KnownVariableDescription}",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight,
            Duration = 8
        });
    }

    private Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = false;
        logger.LogDebug("Prompt template modal cancelled.");
        return Task.CompletedTask;
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape" || args.Key == "Esc")
        {
            await OnModalCancelHandleAsync(new MouseEventArgs());
        }
    }

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }
}
