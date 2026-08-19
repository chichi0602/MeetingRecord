using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.Teams
{
    public partial class TeamViewView
    {
        private readonly ILogger<TeamViewView> logger;
        private readonly TeamService teamService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<TeamAdapterModel> teamAdapterModels = new();

        string modalTitle = "團隊維護";
        bool modalVisible = false;
        TeamAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public TeamViewView(
            ILogger<TeamViewView> logger,
            TeamService teamService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.teamService = teamService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            logger.LogInformation("Initializing team management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Team view initialization stopped because authentication check failed.");
                return;
            }

            if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_團隊清單) == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Team view denied because current user has not this role permission.");
                return;
            }

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading teams. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                searchText,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            DataRequestResult<TeamAdapterModel> dataRequestResult = await teamService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            teamAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogInformation("Team list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<TeamAdapterModel> args)
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

            logger.LogDebug("Team table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
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

        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Team search triggered. Search={Search}", searchText);
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Team refresh triggered.");
            await ReloadAsync();

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "已更新最新資料",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });
        }

        async Task OnEditAsync(TeamAdapterModel teamAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改團隊";
            CurrentRecord = teamAdapterModel.Clone();
            modalVisible = true;
            logger.LogInformation("Opened edit modal for team. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);
        }

        async Task OnDeleteAsync(TeamAdapterModel teamAdapterModel)
        {
            logger.LogInformation("Delete team requested. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);

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
                logger.LogDebug("Team delete cancelled by user. TeamId={TeamId}", teamAdapterModel.Id);
                return;
            }

            await teamService.DeleteAsync(teamAdapterModel.Id);
            logger.LogInformation("Team delete completed. TeamId={TeamId}", teamAdapterModel.Id);

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "刪除成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

            await ReloadAsync();
        }

        async Task OnAddAsync()
        {
            CurrentRecord = new();
            isNewRecordMode = true;
            modalTitle = "新增團隊";
            modalVisible = true;
            logger.LogInformation("Opened create modal for team.");
        }

        private async Task OnModalOKHandleAsync(MouseEventArgs args)
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
                foreach (var error in allErrors)
                {
                    logger.LogWarning("Team form validation failed. Error={Error}", error);
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
                var beforeAddCheckResult = await teamService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogWarning("Team create pre-check failed. Name={Name}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
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

                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                await teamService.AddAsync(CurrentRecord);
                logger.LogInformation("Team create submitted. Name={Name}", CurrentRecord.Name);

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
                var beforeUpdateCheckResult = await teamService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogWarning("Team update pre-check failed. TeamId={TeamId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
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

                CurrentRecord.UpdatedAt = DateTime.Now;
                await teamService.UpdateAsync(CurrentRecord);
                logger.LogInformation("Team update submitted. TeamId={TeamId}, Name={Name}", CurrentRecord.Id, CurrentRecord.Name);

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

        private Task OnModalCancelHandleAsync(MouseEventArgs args)
        {
            modalVisible = false;
            logger.LogDebug("Team modal cancelled.");
            return Task.CompletedTask;
        }

        private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                await Task.Delay(200);
                await OnModalOKHandleAsync(new MouseEventArgs());
            }
            else if (args.Key == "Escape" || args.Key == "Esc")
            {
                await OnModalCancelHandleAsync(new MouseEventArgs());
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}
