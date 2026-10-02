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
        List<TeamService.MemberOption> selectableMembers = new();
        bool modalVisible = false;
        TeamAdapterModel CurrentRecord = new();

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
            CurrentRecord.MemberIds = await teamService.GetMemberIdsAsync(teamAdapterModel.Id);
            selectableMembers = await teamService.GetSelectableMembersAsync();
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
            logger.LogInformation("Opened edit modal for team. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);
        }

        async Task OnDeleteAsync(TeamAdapterModel teamAdapterModel)
        {
            logger.LogInformation("Delete team requested. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);

            // 還是某個專案的主責團隊時不能刪（主責必填，0.4.102）；先擋下來，不要讓使用者按了確認才失敗。
            var beforeDeleteCheckResult = await teamService.BeforeDeleteCheckAsync(teamAdapterModel);
            if (!beforeDeleteCheckResult.Success)
            {
                NotifyError(beforeDeleteCheckResult.Message);
                return;
            }

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

            var deleteResult = await teamService.DeleteAsync(teamAdapterModel.Id);
            if (!deleteResult.Success)
            {
                NotifyError(deleteResult.Message);
                return;
            }

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
            selectableMembers = await teamService.GetSelectableMembersAsync();
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
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

                var addResult = await teamService.AddAsync(CurrentRecord);
                if (addResult.Success)
                {
                    await teamService.SyncMembersAsync(CurrentRecord.Id, CurrentRecord.MemberIds);
                }
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
                var updateResult = await teamService.UpdateAsync(CurrentRecord);
                if (updateResult.Success)
                {
                    await teamService.SyncMembersAsync(CurrentRecord.Id, CurrentRecord.MemberIds);
                }
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
                    discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這筆團隊");
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

        void NotifyError(string message)
        {
            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = message,
                NotificationType = NotificationType.Error,
                Placement = NotificationPlacement.BottomRight,
                Duration = 8
            });
        }

        void OnMembersChanged(IEnumerable<int> values)
        {
            CurrentRecord.MemberIds = values?.ToList() ?? new();
        }

        /// <summary>清單上點狀態膠囊直接切換（0.4.105，比照提示詞清單），先跳確認視窗。</summary>
        async Task OnToggleEnabledAsync(TeamAdapterModel item)
        {
            var willEnable = !item.IsEnabled;
            logger.LogInformation("Team enabled toggle requested. Id={Id}, WillEnable={WillEnable}", item.Id, willEnable);

            var confirmOptions = new ConfirmOptions
            {
                Title = willEnable ? "確認啟用" : "確認停用",
                Content = willEnable
                    ? $"確定要啟用「{item.Name}」嗎？啟用後會出現在團隊下拉選單。"
                    : $"確定要停用「{item.Name}」嗎？停用後不會出現在團隊下拉選單；已設定的成員與專案團隊不受影響。",
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

            var result = await teamService.SetEnabledAsync(item.Id, willEnable);
            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = result.Success
                    ? (willEnable ? $"已啟用「{item.Name}」。" : $"已停用「{item.Name}」。")
                    : result.Message,
                NotificationType = result.Success ? NotificationType.Success : NotificationType.Error,
                Placement = NotificationPlacement.BottomRight
            });

            if (result.Success)
            {
                await ReloadAsync();
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}
