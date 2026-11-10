using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Users;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Client.Pages.Admins
{
    /// <summary>
    /// 使用者管理（1.1.10 從 Blazor Server 的 MyUserView 搬到 WASM），只限管理者。畫面行為不變，資料改走 <see cref="UserApi"/>：
    /// 不能刪除或停用自己與 support、不能拿掉自己的管理者身分、帳號不可重複等規則都在伺服器（MyUserService），這裡只顯示結果。
    /// </summary>
    public partial class MyUserPage
    {
        [Inject] private ILogger<MyUserPage> logger { get; set; } = default!;
        [Inject] private UserApi UserApi { get; set; } = default!;
        [Inject] private PermissionView PermissionView { get; set; } = default!;
        [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
        [Inject] private ModalService modalService { get; set; } = default!;
        [Inject] private MessageService messageService { get; set; } = default!;
        [Inject] private NotificationService notificationService { get; set; } = default!;

        List<TeamOptionDto> teamOptions = new();
        List<string> availableTeams = new();
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<UserDto> users = new();
        List<RoleOptionDto> roleOptions = new();

        /// <summary>清單上勾選的人（批次調整團隊、批次刪除共用，0.4.101／1.0.53）。</summary>
        IEnumerable<UserDto> selectedUsers = [];

        /// <summary>批次操作要套用的團隊名稱。</summary>
        string? batchTeamName;

        string modalTitle = "使用者維護";
        bool modalVisible = false;
        UserDto CurrentRecord = new();

        /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
        private string? formSnapshot;

        /// <summary>
        /// ⚠️ Esc 會**同時**走兩條路：AntDesign Modal 的 <c>Keyboard="true"</c> 與表單上的
        /// <c>@onkeydown</c>，兩者都會呼叫 <see cref="OnModalCancelHandleAsync"/>。沒有這道旗標會**疊出兩個確認視窗**。
        /// </summary>
        private bool isDiscardConfirming;
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;

        protected override async Task OnInitializedAsync()
        {
            // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過；這頁只看是不是管理者（同 Server 頁的 CheckIsAdmin）。
            await PermissionView.LoadAsync();
            if (!PermissionView.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await PageAccessApi.RecordPageDeniedAsync("/myusers");
                logger.LogWarning("User management page denied because current user is not an administrator.");
                return;
            }

            teamOptions = (await UserApi.GetTeamsAsync()).Data ?? [];
            availableTeams = teamOptions.Select(x => x.Name).ToList();

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            var result = await UserApi.SearchAsync(new UserSearchRequestDto
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

            users = page.Items;
            _total = page.TotalCount;
            // 全選只算這一頁（1.0.53）：換頁、排序、搜尋、刪除、批次調整團隊後都會走到這裡，勾選一律清掉。
            selectedUsers = [];
            // 頁碼可能落在最後一頁之後（例如停在第 2 頁時把該頁唯一一筆刪掉）：夾回最後一頁重載一次（1.0.57）。
            // Math.Max(1, ...) 是因為 AntDesign 在 PageIndex < 1 時不觸發 OnChange。
            if (users.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                await ReloadAsync();
                return;
            }

            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<UserDto> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;

            var sorted = args.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
            if (sorted is not null)
            {
                // 欄位名就是 Column 的 DataIndex（Account、Name、Email、RoleText、StatusText、CreateAt、UpdateAt），伺服器不分大小寫認得。
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

        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
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

        async Task OnEditAsync(UserDto user)
        {
            await LoadRolesAsync();

            // 清單沒有帶額外角色，修改前另外取這一筆。
            var current = await UserApi.GetByIdAsync(user.Id);
            if (!Succeeded(current) || current.Data is not { } detail)
            {
                return;
            }

            isNewRecordMode = false;
            modalTitle = "修改使用者";
            CurrentRecord = detail.Clone();
            CurrentRecord.Password = string.Empty;
            CurrentRecord.AdditionalRoleIds ??= [];
            CurrentRecord.TeamNames ??= [];
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
        }

        async Task OnDeleteAsync(UserDto user)
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

            if (!Succeeded(await UserApi.DeleteAsync(user.Id)))
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

        #region 多選刪除（1.0.53）

        private bool isBatchDeleting;

        async Task OnBatchDeleteAsync()
        {
            var items = selectedUsers.ToList();
            if (items.Count == 0 || isBatchDeleting)
            {
                return;
            }

            var ok = await modalService.ConfirmAsync(new ConfirmOptions()
            {
                Title = "確認刪除",
                Content = $"確定要刪除勾選的 {items.Count} 個帳號嗎？此操作無法復原。",
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
                // 自己的帳號、support 由伺服器擋下（「不能刪除自己的帳號。」），原因照樣列進批次結果。
                var result = await BatchRunner.RunAsync(items, x => x.Account, async x =>
                {
                    var r = await UserApi.DeleteAsync(x.Id);
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

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            await LoadRolesAsync();

            CurrentRecord = new UserDto { Password = string.Empty, AdditionalRoleIds = [], TeamNames = [] };
            // 新建帳號預設的角色；沒有就選第一個（同原本做法）。
            var defaultRole = roleOptions.FirstOrDefault(x => x.IsDefault) ?? roleOptions.FirstOrDefault();
            if (defaultRole is not null)
            {
                CurrentRecord.RoleViewId = defaultRole.Id;
            }

            isNewRecordMode = true;
            modalTitle = "新增使用者";
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
        }

        /// <summary>表單送出閘門（1.0.62）：「確定」與 Enter 共用，等候回寫的那段時間也算送出中，連按不會新增兩筆。</summary>
        private readonly FormSubmitGate submitGate = new();

        private Task OnModalOKHandleAsync(MouseEventArgs args) => submitGate.RunAsync(SaveModalAsync);

        private async Task SaveModalAsync()
        {
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

            if (isNewRecordMode && string.IsNullOrWhiteSpace(CurrentRecord.Password))
            {
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

            // 帳號重複（409）、不能停用自己或拿掉自己的管理者身分（400）等都由伺服器判斷；失敗時表單留著，訊息照原文顯示。
            var saved = isNewRecordMode
                ? Succeeded(await UserApi.CreateAsync(CurrentRecord))
                : Succeeded(await UserApi.UpdateAsync(CurrentRecord));
            if (!saved)
            {
                modalVisible = true;
                return;
            }

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
                    // ⚠️ @bind-Visible 是雙向的，AntDesign 在呼叫這個 handler 之前就已經把視窗關掉了，要重新打開。
                    modalVisible = true;
                    return;
                }
            }

            modalVisible = false;
            formSnapshot = null;
        }

        private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
        {
            // ⚠️ 一律走 FormKeyboardHelper：它會擋掉中文輸入法組字中的 Enter（選字用的那一下）。直接比對 args.Key 會誤觸。
            if (FormKeyboardHelper.IsSubmit(args))
            {
                // 等候不是可以省的：AntDesign Input 預設 change/blur 才回寫繫結值，Enter 送出時焦點還在欄位裡，不等就會拿到舊值。
                await submitGate.RunAsync(SaveModalAsync, FormSubmitGate.EnterSettleDelay);
            }
            else if (FormKeyboardHelper.IsCancel(args))
            {
                await OnModalCancelHandleAsync(new MouseEventArgs());
            }
        }

        async Task OnToggleStatusAsync(UserDto item)
        {
            var willEnable = !item.Status;

            var confirmOptions = new ConfirmOptions
            {
                Title = willEnable ? "確認啟用" : "確認停用",
                Content = willEnable
                    ? $"確定要啟用「{item.Account}」嗎？啟用後這個帳號可以登入。"
                    : $"確定要停用「{item.Account}」嗎？停用後這個帳號無法登入。",
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

            var result = await UserApi.SetStatusAsync(item.Id, willEnable);
            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = result.Success
                    ? (willEnable ? $"已啟用「{item.Account}」。" : $"已停用「{item.Account}」。")
                    : ErrorText(result),
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

        private void OnAdditionalRolesChanged(IEnumerable<int> values)
        {
            CurrentRecord.AdditionalRoleIds = values?.ToList() ?? new List<int>();
        }

        private void OnUserTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.TeamNames = values?.ToList() ?? new List<string>();
        }

        private async Task LoadRolesAsync()
        {
            roleOptions = (await UserApi.GetRolesAsync()).Data ?? [];
        }

        private async Task OnBatchTeamAsync(bool add)
        {
            var targets = selectedUsers.ToList();
            if (targets.Count == 0 || string.IsNullOrEmpty(batchTeamName))
            {
                return;
            }

            var team = teamOptions.FirstOrDefault(x => x.Name == batchTeamName);
            if (team is null)
            {
                _ = messageService.ErrorAsync($"找不到團隊「{batchTeamName}」。");
                return;
            }

            var ok = await modalService.ConfirmAsync(new ConfirmOptions()
            {
                Title = add ? "批次加入團隊" : "批次移出團隊",
                Content = add
                    ? $"把勾選的 {targets.Count} 人加入「{batchTeamName}」？他們會看得到這個團隊主責或協作的專案。"
                    : $"把勾選的 {targets.Count} 人移出「{batchTeamName}」？他們將看不到只有這個團隊參與的專案。",
                OkText = add ? "加入" : "移出",
                CancelText = "取消",
                OkButtonProps = new ButtonProps { Danger = !add },
                MaskClosable = false
            });
            if (!ok)
            {
                return;
            }

            var result = await UserApi.BatchTeamAsync(targets.Select(x => x.Id).ToList(), team.Id, add);
            if (!Succeeded(result))
            {
                return;
            }

            _ = messageService.SuccessAsync(add
                ? $"已加入 {result.Data} 人（其餘原本就在團隊裡）"
                : $"已移出 {result.Data} 人");
            selectedUsers = [];
            await ReloadAsync();
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
                Placement = NotificationPlacement.BottomRight
            });
        }
    }
}
