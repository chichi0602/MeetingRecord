using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Roles;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Api;
using MeetingRecord.Web.Client.Auth;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Client.Pages.Admins
{
    /// <summary>
    /// 角色管理（1.1.11 從 Blazor Server 的 RoleViewView 搬到 WASM），只限管理者。畫面與權限矩陣的勾選連動不變；
    /// 資料改走 <see cref="RoleApi"/>，權限樹轉權限鍵、目錄檢查、刪除時的重新指派都在伺服器。
    /// </summary>
    public partial class RoleViewPage
    {
        [Inject] private ILogger<RoleViewPage> logger { get; set; } = default!;
        [Inject] private RoleApi RoleApi { get; set; } = default!;
        [Inject] private PermissionView PermissionView { get; set; } = default!;
        [Inject] private PageAccessApi PageAccessApi { get; set; } = default!;
        [Inject] private ModalService modalService { get; set; } = default!;
        [Inject] private MessageService messageService { get; set; } = default!;
        [Inject] private NotificationService notificationService { get; set; } = default!;

        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<RoleDto> roles = new();

        string modalTitle = "角色維護";
        bool modalVisible = false;
        RoleDto CurrentRecord = new() { RolePermission = new() };

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

        /// <summary>矩陣畫面用：表單一定有權限樹（新增取範本、修改取單筆）。</summary>
        private RolePermissionDto Permission => CurrentRecord.RolePermission ??= new();

        protected override async Task OnInitializedAsync()
        {
            // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過；這頁只看是不是管理者（同 Server 頁的 CheckIsAdmin）。
            await PermissionView.LoadAsync();
            if (!PermissionView.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await PageAccessApi.RecordPageDeniedAsync("/roleviews");
                logger.LogWarning("Role management page denied because current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            var result = await RoleApi.SearchAsync(new RoleSearchRequestDto
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

            roles = page.Items;
            _total = page.TotalCount;
            // 全選只算這一頁（1.0.53）：換頁、排序、搜尋、刪除後都會走到這裡，勾選一律清掉。
            selectedRows = [];
            // 頁碼可能落在最後一頁之後（例如停在第 2 頁時把該頁唯一一筆刪掉）：夾回最後一頁重載一次（1.0.57）。
            if (roles.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                await ReloadAsync();
                return;
            }

            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<RoleDto> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;

            var sorted = args.SortModel?.FirstOrDefault(x => x.SortDirection == SortDirection.Ascending || x.SortDirection == SortDirection.Descending);
            if (sorted is not null)
            {
                // 欄位名就是 Column 的 DataIndex（Name、CreateAt、UpdateAt），伺服器不分大小寫認得。
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

        async Task OnEditAsync(RoleDto role)
        {
            // 清單不帶權限樹，修改前另外取這一筆。
            var current = await RoleApi.GetByIdAsync(role.Id);
            if (!Succeeded(current) || current.Data is not { } detail)
            {
                return;
            }

            isNewRecordMode = false;
            modalTitle = "修改角色";
            CurrentRecord = detail.Clone();
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
        }

        async Task OnDeleteAsync(RoleDto role)
        {
            var userCount = (await RoleApi.GetUserCountAsync(role.Id)).Data;
            var ok = await modalService.ConfirmAsync(new ConfirmOptions()
            {
                Title = "確認刪除",
                Content = userCount == 0
                    ? $"確定要刪除「{role.Name}」嗎？此操作無法復原。"
                    : $"目前有 {userCount} 位使用者掛著「{role.Name}」。刪除後，以它為主要角色的人會改用他們的其他角色，沒有其他角色的改用「{MagicObjectHelper.預設角色}」。此操作無法復原，確定要刪除嗎？",
                OkText = "刪除",
                CancelText = "取消",
                OkButtonProps = new ButtonProps { Danger = true },
                MaskClosable = false
            });

            if (!ok)
            {
                return;
            }

            var result = await RoleApi.DeleteAsync(role.Id);
            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = result.Success ? "刪除成功" : ErrorText(result),
                NotificationType = result.Success ? NotificationType.Warning : NotificationType.Error,
                Placement = NotificationPlacement.BottomRight
            });

            await ReloadAsync();
        }

        #region 多選刪除（1.0.53）

        /// <summary>目前這一頁勾選的列；換頁、排序、搜尋、重新載入時清掉（ReloadAsync）。</summary>
        private IEnumerable<RoleDto> selectedRows = [];

        private bool isBatchDeleting;

        async Task OnBatchDeleteAsync()
        {
            var items = selectedRows.ToList();
            if (items.Count == 0 || isBatchDeleting)
            {
                return;
            }

            // 確認框要說清楚影響多少人：逐一問使用人數再加總（同原本做法）。
            var userCount = 0;
            foreach (var item in items)
            {
                userCount += (await RoleApi.GetUserCountAsync(item.Id)).Data;
            }

            var ok = await modalService.ConfirmAsync(new ConfirmOptions()
            {
                Title = "確認刪除",
                Content = userCount == 0
                    ? $"確定要刪除勾選的 {items.Count} 個角色嗎？此操作無法復原。"
                    : $"確定要刪除勾選的 {items.Count} 個角色嗎？目前共有 {userCount} 位使用者掛著這些角色。刪除後，以它們為主要角色的人會改用他們的其他角色，沒有其他角色的改用「{MagicObjectHelper.預設角色}」。此操作無法復原。",
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
                    var r = await RoleApi.DeleteAsync(x.Id);
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

        bool isSeedingPresets;

        /// <summary>一次建好預設角色（0.4.108）。已有同名的略過、不覆蓋（可能已經調過權限），可以重複按。</summary>
        async Task OnAddPresetsAsync()
        {
            if (isSeedingPresets)
            {
                return;
            }

            var ok = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "建立預設角色",
                // 1.1.11：以前寫「四個角色」，1.0.55 加了甲方之後實際是五個。
                Content = "會建立管理者、一般使用者、檢視者、主管、甲方五個角色並設定好權限；已經有同名角色的會略過，不會覆蓋。要繼續嗎？",
                OkText = "建立",
                CancelText = "取消",
                MaskClosable = false
            });
            if (!ok)
            {
                return;
            }

            isSeedingPresets = true;
            try
            {
                var result = await RoleApi.AddPresetsAsync();
                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = result.Success ? result.Message : ErrorText(result),
                    NotificationType = result.Success ? NotificationType.Success : NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });
                await ReloadAsync();
            }
            finally
            {
                isSeedingPresets = false;
            }
        }

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            var template = await RoleApi.GetPermissionTemplateAsync();
            if (!Succeeded(template) || template.Data is not { } tree)
            {
                return;
            }

            CurrentRecord = new RoleDto { RolePermission = tree };
            isNewRecordMode = true;
            modalTitle = "新增角色";
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
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

            // 名稱重複（409）、權限項目不在目錄（400）等都由伺服器判斷；失敗時表單留著，訊息照原文顯示。
            var saved = isNewRecordMode
                ? Succeeded(await RoleApi.CreateAsync(CurrentRecord))
                : Succeeded(await RoleApi.UpdateAsync(CurrentRecord));
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
                    discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這個角色");
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

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }

        private void OnPermissionGroupChanged(RolePermissionGroupDto group, bool value)
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

        private void OnPermissionItemChanged(RolePermissionGroupDto group, RolePermissionNodeDto role, bool value)
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

        private static bool GetActionChecked(RolePermissionNodeDto node, string action)
            => node.Actions.TryGetValue(action, out var enabled) && enabled;

        private void OnPermissionActionChanged(RolePermissionGroupDto group, RolePermissionNodeDto node, string action, bool value)
        {
            node.Actions[action] = value;
            if (value)
            {
                group.Enable = true;
            }
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
