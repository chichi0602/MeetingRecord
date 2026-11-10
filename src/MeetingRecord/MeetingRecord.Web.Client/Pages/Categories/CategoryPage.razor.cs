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

namespace MeetingRecord.Web.Client.Pages.Categories
{
    /// <summary>
    /// 分類清單（1.1.7 從 Blazor Server 的 CategoryViewView 搬到 WASM）。畫面行為不變，資料改走 <see cref="CategoryApi"/>：
    /// 名稱重複、團隊同步等規則都在伺服器（CategoryService），這裡只顯示結果。
    /// </summary>
    public partial class CategoryPage
    {
        [Inject] private ILogger<CategoryPage> logger { get; set; } = default!;
        [Inject] private CategoryApi CategoryApi { get; set; } = default!;
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

        List<CategoryDto> categories = new();

        string modalTitle = "分類維護";
        bool modalVisible = false;
        CategoryDto CurrentRecord = NewRecord();
        List<TeamOptionDto> selectableTeams = new();

        /// <summary>開啟表單當下的快照。null 代表還沒開過（見 <see cref="FormDirtyHelper.IsDirty"/> 的 null 語意）。</summary>
        private string? formSnapshot;

        /// <summary>
        /// ⚠️ Esc 會**同時**走兩條路：AntDesign Modal 的 <c>Keyboard="true"</c> 與表單上的
        /// <c>@onkeydown</c>（<see cref="OnModalKeyDownAsync"/>），兩者都會呼叫
        /// <see cref="OnModalCancelHandleAsync"/>。沒有這道旗標會**疊出兩個確認視窗**。
        /// </summary>
        private bool isDiscardConfirming;
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;

        protected override async Task OnInitializedAsync()
        {
            // 登入狀態（未登入、停用、須改密碼）WasmLayout 已先處理過，走到這裡一定是有效的登入者。
            await PermissionView.LoadAsync();
            if (!PermissionView.CanAccessPage(MagicObjectHelper.角色_分類清單))
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await PageAccessApi.RecordPageDeniedAsync("/categories");
                logger.LogWarning("Category page denied because current user has not this role permission.");
                return;
            }

            await ReloadAsync();
        }

        private static CategoryDto NewRecord() => new() { Id = 0, IsEnabled = true, TeamIds = [] };

        public async Task ReloadAsync()
        {
            var result = await CategoryApi.SearchAsync(new CategorySearchRequestDto
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

            categories = page.Items;
            _total = page.TotalCount;
            // 全選只算這一頁（1.0.53）：換頁、排序、搜尋、刪除後都會走到這裡，勾選一律清掉。
            selectedRows = [];
            // 頁碼可能落在最後一頁之後（例如停在第 2 頁時把該頁唯一一筆刪掉）：夾回最後一頁重載一次（1.0.57）。
            // Math.Max(1, ...) 是因為 AntDesign 在 PageIndex < 1 時不觸發 OnChange。
            if (categories.Count == 0 && _total > 0 && _pageIndex > 1)
            {
                _pageIndex = Math.Max(1, (_total + _pageSize - 1) / _pageSize);
                await ReloadAsync();
                return;
            }

            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<CategoryDto> args)
        {
            _pageIndex = args.PageIndex;
            // 明確賦值，不賭 @bind-PageSize 與 AntDesign 內部 callback 的先後順序（同 TodoViewView）。
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

        async Task OnEditAsync(CategoryDto category)
        {
            isNewRecordMode = false;
            modalTitle = "修改分類";
            CurrentRecord = category.Clone();
            CurrentRecord.TeamIds ??= [];
            await LoadSelectableTeamsAsync();
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
        }

        async Task OnDeleteAsync(CategoryDto category)
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

            var deleteResult = await CategoryApi.DeleteAsync(category.Id ?? 0);
            if (!deleteResult.Success)
            {
                ShowError(deleteResult);
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

        /// <summary>目前這一頁勾選的列；換頁、排序、搜尋、重新載入時清掉（ReloadAsync）。</summary>
        private IEnumerable<CategoryDto> selectedRows = [];

        private bool isBatchDeleting;

        private bool CanDelete => PermissionView.Can(MagicObjectHelper.角色_分類清單, PermissionActions.Delete);

        async Task OnBatchDeleteAsync()
        {
            var items = selectedRows.ToList();
            if (items.Count == 0 || isBatchDeleting)
            {
                return;
            }

            var ok = await modalService.ConfirmAsync(new ConfirmOptions()
            {
                Title = "確認刪除",
                Content = $"確定要刪除勾選的 {items.Count} 個分類嗎？此操作無法復原。",
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
                    var r = await CategoryApi.DeleteAsync(x.Id ?? 0);
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

        async Task OnAddAsync()
        {
            CurrentRecord = NewRecord();
            await LoadSelectableTeamsAsync();
            isNewRecordMode = true;
            modalTitle = "新增分類";
            formSnapshot = FormDirtyHelper.Capture(CurrentRecord);
            modalVisible = true;
        }

        private async Task LoadSelectableTeamsAsync()
        {
            var result = await CategoryApi.GetSelectableTeamsAsync();
            selectableTeams = result.Data ?? [];
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

            // 名稱重複（409）、找不到（404）等都由伺服器判斷；失敗時表單留著，訊息照原本的文字顯示。
            var saved = isNewRecordMode
                ? Succeeded(await CategoryApi.CreateAsync(CurrentRecord))
                : Succeeded(await CategoryApi.UpdateAsync(CurrentRecord));
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
                    discard = await FormDirtyHelper.ConfirmDiscardAsync(modalService, "這筆分類");
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
            // 也讓 Shift+Enter 落回瀏覽器原生的換行。直接比對 args.Key 會誤觸。
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

        void OnTeamsChanged(IEnumerable<int> values)
        {
            CurrentRecord.TeamIds = values?.ToList() ?? new();
        }

        /// <summary>清單上點狀態膠囊直接切換（0.4.105，比照提示詞清單），先跳確認視窗。</summary>
        async Task OnToggleEnabledAsync(CategoryDto item)
        {
            var willEnable = !item.IsEnabled;

            var confirmOptions = new ConfirmOptions
            {
                Title = willEnable ? "確認啟用" : "確認停用",
                Content = willEnable
                    ? $"確定要啟用「{item.Name}」嗎？啟用後可以在專案與會議上選用。"
                    : $"確定要停用「{item.Name}」嗎？停用後不能再選用；已經貼上的資料不受影響。",
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

            var result = await CategoryApi.SetEnabledAsync(item.Id ?? 0, willEnable);
            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = result.Success
                    ? (willEnable ? $"已啟用「{item.Name}」。" : $"已停用「{item.Name}」。")
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
