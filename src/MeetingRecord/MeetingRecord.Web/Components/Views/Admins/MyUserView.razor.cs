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
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Components.Views.Admins
{
    public partial class MyUserView
    {
        private readonly ILogger<MyUserView> logger;
        private readonly MyUserService myUserService;
        private readonly RoleViewService roleViewService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        private readonly TeamService teamService;
        List<string> availableTeams = new();
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<MyUserAdapterModel> myUserAdapterModels = new();
        List<RoleViewAdapterModel> roleViewAdapterModels = new();

        string modalTitle = "使用者維護";
        bool modalVisible = false;
        MyUserAdapterModel CurrentRecord = new();

        /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
        private string? formSnapshot;

        /// <summary>
        /// ⚠️ Esc 會**同時**走兩條路：AntDesign Modal 的 <c>Keyboard="true"</c> 與表單上的
        /// <c>@onkeydown</c>，兩者都會呼叫 <see cref="OnModalCancelHandleAsync"/>。0.4.84 之前兩次
        /// 都只是 <c>modalVisible = false</c>，冪等所以沒人發現；加了確認框之後會**疊出兩個確認視窗**。
        /// </summary>
        private bool isDiscardConfirming;
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public MyUserView(
            ILogger<MyUserView> logger,
            MyUserService myUserService,
            RoleViewService roleViewService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService,
            TeamService teamService)
        {
            this.logger = logger;
            this.myUserService = myUserService;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
            this.teamService = teamService;
        }

        protected override async Task OnInitializedAsync()
        {
            logger.LogInformation("Initializing user management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("User management view initialization stopped because authentication check failed.");
                return;
            }

            if (!AuthenticationStateHelper.CheckIsAdmin())
            {
                RoleMessage = "你沒有權限存取此頁面";
                logger.LogWarning("User management view denied because current user is not an administrator.");
                return;
            }

            availableTeams = await teamService.GetAllEnabledNamesAsync();

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading users. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                searchText,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            DataRequestResult<MyUserAdapterModel> dataRequestResult = await myUserService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            myUserAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogInformation("User list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<MyUserAdapterModel> args)
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

            logger.LogDebug("User table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
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
            logger.LogInformation("User search triggered. Search={Search}", searchText);
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("User refresh triggered.");
            await ReloadAsync();

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "已更新最新資料",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });
        }

        async Task OnEditAsync(MyUserAdapterModel myUserAdapterModel)
        {
            await LoadRoleViewsAsync();

            isNewRecordMode = false;
            modalTitle = "修改使用者";
            CurrentRecord = (await myUserService.GetAsync(myUserAdapterModel.Id)).Clone();
            var (additionalRoleIds, teamNames) = await myUserService.GetUserAssignmentsAsync(myUserAdapterModel.Id);
            CurrentRecord.AdditionalRoleIds = additionalRoleIds;
            CurrentRecord.TeamNames = teamNames;
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
            logger.LogInformation("Opened edit modal for user. UserId={UserId}, Account={Account}", myUserAdapterModel.Id, myUserAdapterModel.Account);
        }

        async Task OnDeleteAsync(MyUserAdapterModel myUserAdapterModel)
        {
            logger.LogInformation("Delete user requested. UserId={UserId}, Account={Account}", myUserAdapterModel.Id, myUserAdapterModel.Account);

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
                logger.LogDebug("User delete cancelled by user. UserId={UserId}", myUserAdapterModel.Id);
                return;
            }

            await myUserService.DeleteAsync(myUserAdapterModel.Id);
            logger.LogInformation("User delete completed. UserId={UserId}", myUserAdapterModel.Id);

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "刪除成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

            await ReloadAsync();
        }

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            await LoadRoleViewsAsync();

            CurrentRecord = new();
            RoleViewAdapterModel? defaultRole = null;
            try
            {
                defaultRole = await roleViewService.Get預設新建帳號角色Async();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load default role for new user creation.");
            }

            if (defaultRole is not null && defaultRole.Id != 0)
            {
                CurrentRecord.RoleViewId = defaultRole.Id;
            }
            else if (roleViewAdapterModels.Any())
            {
                CurrentRecord.RoleViewId = roleViewAdapterModels.First().Id;
            }

            isNewRecordMode = true;
            modalTitle = "新增使用者";
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
            logger.LogInformation("Opened create modal for user.");
        }

        private async Task OnModalOKHandleAsync(MouseEventArgs args)
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();

                foreach (var error in allErrors)
                {
                    logger.LogWarning("User form validation failed. Error={Error}", error);
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

            if (isNewRecordMode && string.IsNullOrWhiteSpace(CurrentRecord.Password))
            {
                logger.LogWarning("User create validation failed because password is empty. Account={Account}", CurrentRecord.Account);
                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "驗證失敗",
                    Description = "新增使用者時必須輸入密碼。",
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight,
                    Duration = 5
                });

                modalVisible = true;
                return;
            }

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await myUserService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogWarning("User create pre-check failed. Account={Account}, Message={Message}", CurrentRecord.Account, beforeAddCheckResult.Message);
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

                CurrentRecord.CreateAt = DateTime.Now;
                CurrentRecord.UpdateAt = DateTime.Now;

                await myUserService.AddAsync(CurrentRecord);
                logger.LogInformation("User create submitted. Account={Account}", CurrentRecord.Account);

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
                var beforeUpdateCheckResult = await myUserService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogWarning("User update pre-check failed. UserId={UserId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
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

                CurrentRecord.UpdateAt = DateTime.Now;

                await myUserService.UpdateAsync(CurrentRecord);
                logger.LogInformation("User update submitted. UserId={UserId}, Account={Account}", CurrentRecord.Id, CurrentRecord.Account);

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
                    discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這筆使用者資料");
                }
                finally
                {
                    isDiscardConfirming = false;
                }

                if (!discard)
                {
                    // ⚠️ @bind-Visible 是雙向的，AntDesign 在呼叫這個 handler 之前就把視窗關掉了。
                    //    不重新開啟的話，按「繼續編修」反而會失去整張表單——正好是我們要修的反面。
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
            // 也讓 Shift+Enter 落回瀏覽器原生的換行。直接比對 args.Key 會誤觸。
            if (FormKeyboardHelper.IsSubmit(args))
            {
                // Task.Delay 不是可以省的：AntDesign Input 預設 change/blur 才回寫繫結值，
                // Enter 送出時焦點還在欄位裡，不等就會拿到舊值。
                await Task.Delay(200);
                await OnModalOKHandleAsync(new MouseEventArgs());
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

        private void OnAdditionalRolesChanged(IEnumerable<int> values)
        {
            CurrentRecord.AdditionalRoleIds = values?.ToList() ?? new List<int>();
        }

        private void OnUserTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.TeamNames = values?.ToList() ?? new List<string>();
        }

        private async Task LoadRoleViewsAsync()
        {
            roleViewAdapterModels = await myUserService.GetRoleViewsAsync();
            logger.LogDebug("Loaded role views for user view. Count={Count}", roleViewAdapterModels.Count);
        }
    }
}
