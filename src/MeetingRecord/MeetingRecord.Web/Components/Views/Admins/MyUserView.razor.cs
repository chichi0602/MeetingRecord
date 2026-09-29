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
        private readonly ProjectService projectService;
        private readonly ProjectMemberService projectMemberService;
        List<ProjectAdapterModel> availableProjects = new();

        /// <summary>表單上勾選的專案（負責人的專案也在裡面，但選項是鎖住的）。</summary>
        List<int> selectedProjectIds = new();

        /// <summary>這個人擔任負責人的專案。存檔時不會被移除，只能在專案頁轉移。</summary>
        HashSet<int> ownedProjectIds = new();
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<MyUserAdapterModel> myUserAdapterModels = new();

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
            ProjectService projectService,
            ProjectMemberService projectMemberService)
        {
            this.logger = logger;
            this.myUserService = myUserService;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
            this.projectService = projectService;
            this.projectMemberService = projectMemberService;
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

            availableProjects = await projectService.GetSelectableAsync();

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
            isNewRecordMode = false;
            modalTitle = "修改使用者";
            CurrentRecord = (await myUserService.GetAsync(myUserAdapterModel.Id)).Clone();
            var (_, teamNames) = await myUserService.GetUserAssignmentsAsync(myUserAdapterModel.Id);
            CurrentRecord.TeamNames = teamNames;
            await ApplyGeneralRoleAsync();
            availableProjects = await projectService.GetSelectableAsync();
            selectedProjectIds = await projectMemberService.GetUserProjectIdsAsync(myUserAdapterModel.Id);
            ownedProjectIds = [.. await projectMemberService.GetUserOwnedProjectIdsAsync(myUserAdapterModel.Id)];
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
            CurrentRecord = new();
            await ApplyGeneralRoleAsync();
            availableProjects = await projectService.GetSelectableAsync();
            selectedProjectIds = new();
            ownedProjectIds = new();

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
                await SaveProjectsAsync(CurrentRecord.Id);
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
                await SaveProjectsAsync(CurrentRecord.Id);
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

        private void OnUserProjectsChanged(IEnumerable<int> values)
        {
            selectedProjectIds = values?.ToList() ?? new List<int>();
        }

        /// <summary>
        /// 把勾選的專案存成協作者身分。帳號本身已經存好了才呼叫——新增時要等 AddAsync 回填 Id。
        /// </summary>
        private async Task SaveProjectsAsync(int userId)
        {
            if (userId == 0)
            {
                return;
            }

            var result = await projectMemberService.SyncUserCollaborationsAsync(userId, selectedProjectIds);
            if (!result.Success)
            {
                logger.LogWarning("Saving user projects failed. UserId={UserId}, Message={Message}", userId, result.Message);
                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = result.Message,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });
            }
        }

        /// <summary>
        /// 角色一律是「一般使用者」（0.4.98 起沒有角色選單）。額外角色清空，
        /// 否則舊帳號編輯後存檔會把殘留的多角色再寫回 UserRole。
        /// </summary>
        private async Task ApplyGeneralRoleAsync()
        {
            CurrentRecord.AdditionalRoleIds = new List<int>();

            try
            {
                var generalRole = await roleViewService.Get預設新建帳號角色Async();
                if (generalRole is not null && generalRole.Id != 0)
                {
                    CurrentRecord.RoleViewId = generalRole.Id;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load general user role for user form.");
            }
        }
    }
}
