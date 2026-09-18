using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Admins;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Components.Views.Admins
{
    public partial class RoleViewView
    {
        private readonly ILogger<RoleViewView> logger;
        private readonly RoleViewService roleViewService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        private readonly RolePermissionService rolePermissionService;
        private readonly TeamService teamService;
        List<string> availableTeams = new();
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<RoleViewAdapterModel> roleViewAdapterModels = new();

        string modalTitle = "角色維護";
        bool modalVisible = false;
        RoleViewAdapterModel CurrentRecord = new();

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

        public RoleViewView(
            ILogger<RoleViewView> logger,
            RoleViewService roleViewService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService,
            RolePermissionService rolePermissionService,
            TeamService teamService)
        {
            this.logger = logger;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
            this.rolePermissionService = rolePermissionService;
            this.teamService = teamService;
        }

        protected override async Task OnInitializedAsync()
        {
            logger.LogInformation("Initializing role view management.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Role view initialization stopped because authentication check failed.");
                return;
            }

            if (!AuthenticationStateHelper.CheckIsAdmin())
            {
                RoleMessage = "你沒有權限存取此頁面";
                logger.LogWarning("Role view denied because current user is not an administrator.");
                return;
            }

            availableTeams = await teamService.GetAllEnabledNamesAsync();

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading role views. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                searchText,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            DataRequestResult<RoleViewAdapterModel> dataRequestResult = await roleViewService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            roleViewAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogInformation("Role view list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<RoleViewAdapterModel> args)
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

            logger.LogDebug("Role view table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
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
            logger.LogInformation("Role view search triggered. Search={Search}", searchText);
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Role view refresh triggered.");
            await ReloadAsync();

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "已更新最新資料",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });
        }

        async Task OnEditAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改角色";
            CurrentRecord = roleViewAdapterModel.Clone();
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
            logger.LogInformation("Opened edit modal for role view. RoleViewId={RoleViewId}, Name={RoleName}", roleViewAdapterModel.Id, roleViewAdapterModel.Name);
        }

        async Task OnDeleteAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            logger.LogInformation("Delete role view requested. RoleViewId={RoleViewId}, Name={RoleName}", roleViewAdapterModel.Id, roleViewAdapterModel.Name);

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
                logger.LogDebug("Role view delete cancelled by user. RoleViewId={RoleViewId}", roleViewAdapterModel.Id);
                return;
            }

            await roleViewService.DeleteAsync(roleViewAdapterModel.Id);
            logger.LogInformation("Role view delete completed. RoleViewId={RoleViewId}", roleViewAdapterModel.Id);

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
            CurrentRecord.RolePermission = rolePermissionService.InitializePermissionSetting();

            isNewRecordMode = true;
            modalTitle = "新增角色";
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
            logger.LogInformation("Opened create modal for role view.");
        }

        private async Task OnModalOKHandleAsync(MouseEventArgs args)
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
                foreach (var error in allErrors)
                {
                    logger.LogWarning("Role view form validation failed. Error={Error}", error);
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
                var beforeAddCheckResult = await roleViewService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogWarning("Role view create pre-check failed. Name={RoleName}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
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

                await roleViewService.AddAsync(CurrentRecord);
                logger.LogInformation("Role view create submitted. Name={RoleName}", CurrentRecord.Name);

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
                var beforeUpdateCheckResult = await roleViewService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogWarning("Role view update pre-check failed. RoleViewId={RoleViewId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
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
                await roleViewService.UpdateAsync(CurrentRecord);
                logger.LogInformation("Role view update submitted. RoleViewId={RoleViewId}, Name={RoleName}", CurrentRecord.Id, CurrentRecord.Name);

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
                    discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這個角色");
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

        private void OnRoleTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.DefaultTeams = values?.ToList() ?? [];
        }

        private void OnPermissionGroupChanged(RolePermissionGroup group, bool value)
        {
            group.Enable = value;

            if (value)
            {
                return;
            }

            foreach (var permission in group.Permissions)
            {
                permission.Enable = false;
            }
        }

        private void OnPermissionItemChanged(RolePermissionGroup group, RolePermissionNode role, bool value)
        {
            role.Enable = value;

            if (value)
            {
                group.Enable = true;
            }
        }

        /// <summary>權限矩陣的動作欄（代碼 → 顯示名）。</summary>
        private static readonly (string Key, string Label)[] PermissionActionItems =
        [
            (PermissionActions.View, "檢視"),
            (PermissionActions.Create, "新增"),
            (PermissionActions.Edit, "編輯"),
            (PermissionActions.Delete, "刪除"),
            (PermissionActions.Export, "匯出"),
        ];

        private static bool GetActionChecked(RolePermissionNode node, string action)
            => node.Actions is not null && node.Actions.TryGetValue(action, out var enabled) && enabled;

        private void OnPermissionActionChanged(RolePermissionGroup group, RolePermissionNode node, string action, bool value)
        {
            node.Actions ??= new Dictionary<string, bool>();
            node.Actions[action] = value;

            if (value)
            {
                group.Enable = true;
            }
        }
    }
}
