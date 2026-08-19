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

namespace MeetingRecord.Web.Components.Views.Categories
{
    public partial class CategoryViewView
    {
        private readonly ILogger<CategoryViewView> logger;
        private readonly CategoryService categoryService;
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

        List<CategoryAdapterModel> categoryAdapterModels = new();

        string modalTitle = "分類維護";
        bool modalVisible = false;
        CategoryAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public CategoryViewView(
            ILogger<CategoryViewView> logger,
            CategoryService categoryService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.categoryService = categoryService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            logger.LogInformation("Initializing category management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Category view initialization stopped because authentication check failed.");
                return;
            }

            if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_分類清單) == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Category view denied because current user has not this role permission.");
                return;
            }

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading categories. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                searchText,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            DataRequestResult<CategoryAdapterModel> dataRequestResult = await categoryService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            categoryAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogInformation("Category list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<CategoryAdapterModel> args)
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

            logger.LogDebug("Category table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
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
            logger.LogInformation("Category search triggered. Search={Search}", searchText);
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Category refresh triggered.");
            await ReloadAsync();

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "已更新最新資料",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });
        }

        async Task OnEditAsync(CategoryAdapterModel categoryAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改分類";
            CurrentRecord = categoryAdapterModel.Clone();
            modalVisible = true;
            logger.LogInformation("Opened edit modal for category. CategoryId={CategoryId}, Name={Name}", categoryAdapterModel.Id, categoryAdapterModel.Name);
        }

        async Task OnDeleteAsync(CategoryAdapterModel categoryAdapterModel)
        {
            logger.LogInformation("Delete category requested. CategoryId={CategoryId}, Name={Name}", categoryAdapterModel.Id, categoryAdapterModel.Name);

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
                logger.LogDebug("Category delete cancelled by user. CategoryId={CategoryId}", categoryAdapterModel.Id);
                return;
            }

            await categoryService.DeleteAsync(categoryAdapterModel.Id);
            logger.LogInformation("Category delete completed. CategoryId={CategoryId}", categoryAdapterModel.Id);

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
            modalTitle = "新增分類";
            modalVisible = true;
            logger.LogInformation("Opened create modal for category.");
        }

        private async Task OnModalOKHandleAsync(MouseEventArgs args)
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
                foreach (var error in allErrors)
                {
                    logger.LogWarning("Category form validation failed. Error={Error}", error);
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
                var beforeAddCheckResult = await categoryService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogWarning("Category create pre-check failed. Name={Name}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
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

                await categoryService.AddAsync(CurrentRecord);
                logger.LogInformation("Category create submitted. Name={Name}", CurrentRecord.Name);

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
                var beforeUpdateCheckResult = await categoryService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogWarning("Category update pre-check failed. CategoryId={CategoryId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
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
                await categoryService.UpdateAsync(CurrentRecord);
                logger.LogInformation("Category update submitted. CategoryId={CategoryId}, Name={Name}", CurrentRecord.Id, CurrentRecord.Name);

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
            logger.LogDebug("Category modal cancelled.");
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
