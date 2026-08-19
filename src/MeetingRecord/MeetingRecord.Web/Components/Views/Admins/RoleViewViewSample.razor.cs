using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http.HttpResults;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Models.AdapterModel;
using MeetingRecord.Models.Others;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.Admins
{
    public partial class RoleViewViewSample
    {
        private readonly ILogger<RoleViewView> logger;
        private readonly RoleViewService roleViewService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;

        List<RoleViewAdapterModel> roleViewAdapterModels = new();

        string modalTitle = "角色列表";
        bool modalVisible = false;
        RecordVm CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }

        public RoleViewViewSample(ILogger<RoleViewView> logger,
            RoleViewService roleViewService,
            ModalService modalService, MessageService messageService, NotificationService notificationService)
        {
            this.logger = logger;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
        }

        public async Task ReloadAsync()
        {
            DataRequestResult<RoleViewAdapterModel> dataRequestResult = await roleViewService.GetAsync(new DataRequest
            {
                Search = string.Empty,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            roleViewAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;

            StateHasChanged();
        }

        async Task OnTableChange(AntDesign.TableModels.QueryModel<RoleViewAdapterModel> args)
        {
            _pageIndex = args.PageIndex;
            await ReloadAsync();
        }

        async Task OnEditAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
        }

        async Task OnDeleteAsync(RoleViewAdapterModel roleViewAdapterModel)
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

            if (ok)
            {
                await roleViewService.DeleteAsync(roleViewAdapterModel.Id);

                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = "刪除成功",
                    NotificationType = NotificationType.Warning,
                    Placement = NotificationPlacement.BottomRight
                });

                await ReloadAsync();
            }
            else
            {
                // 使用者按「取消」或關閉
            }
            //patientData.FromJson(patientAdapterModel.JsonData);
            //string subjectNo = patientData.臨床資訊.SubjectNo;
            //string FIGO = patientData.臨床資訊.FIGOStaging;
            //// 使用 ConfirmMessageBox 來確認刪除操作
            //var task = ConfirmMessageBox.ShowAsync("400px", "200px",
            //"刪除受測者資料", $"確定要刪除受測者 {patientData.臨床資訊.SubjectNo} 的資料嗎？",
            //ConfirmMessageBox.HiddenAsync);
            //StateHasChanged(); // 更新 UI
            //var confirmDelete = await task;
            //if (confirmDelete == true)
            //{
            //    // 刪除受測者資料的邏輯
            //    var deleteResult = await PatientService.DeleteAsync(patientAdapterModel.Id);
            //    if (deleteResult.Success)
            //    {

            //        RandomParameterMode randomParameterModeAfter = new RandomParameterMode()
            //        {
            //            SubjectNo = subjectNo,
            //            FIGO = FIGO,
            //        };
            //        randomParameterModeAfter.Parse();
            //        await RandomListService.RemoveAsync(randomParameterModeAfter);


            //        // 刪除成功，顯示提示訊息
            //        // await MessageBox.ShowAsync("400px", "200px", "成功", "受測者資料已成功刪除。", MessageBox.HiddenAsync);
            //        await ReloadAsync();
            //    }
            //    else
            //    {
            //        // 刪除失敗，顯示錯誤訊息
            //        var task3 = MessageBox.ShowAsync("400px", "200px", "錯誤", deleteResult.Message, MessageBox.HiddenAsync);
            //        StateHasChanged(); // 更新 UI
            //        await task3;
            //    }
            //    StateHasChanged(); // 更新 UI
            //}
            //else
            //{
            //    // 使用者取消刪除操作
            //}
        }

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            await roleViewService.AddAsync(new RoleViewAdapterModel
            {
                Name = "新角色 " + Guid.NewGuid(),
                RolePermission = new()
            });

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "新增成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

            _ = messageService.SuccessAsync("新增成功");

            await modalService.InfoAsync(new ConfirmOptions()
            {
                Title = "系統訊息",
                Content = "新增成功",
            });

            await ReloadAsync();

            modalVisible = true;
        }

        private async Task OnModalOKHandleAsync(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            #region 進行 Form Validation 檢查驗證作業
            if (LocalEditContext?.Validate() == false)
            {
                // 取得所有驗證失敗的錯誤訊息
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();

                // 取得指定欄位的錯誤訊息
                // IEnumerable<string> fieldErrors = LocalEditContext
                //     .GetValidationMessages(LocalEditContext.Field(nameof(CurrentRecord.Name)));

                //string errorDescription = string.Join(Environment.NewLine, allErrors);

                foreach (var error in allErrors)
                {
                    _ = notificationService.Open(new NotificationConfig()
                    {
                        Message = "驗證失敗，請修正以下錯誤",
                        Description = error,
                        NotificationType = NotificationType.Error,
                        Placement = NotificationPlacement.BottomRight,
                        Duration = 5
                    });
                }

                modalVisible = true;
                return;
            }
            #endregion

            modalVisible = false;
        }

        private async Task OnModalCancelHandleAsync(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            modalVisible = false;
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}
