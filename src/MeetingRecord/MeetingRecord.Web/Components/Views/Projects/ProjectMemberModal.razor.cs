using AntDesign;
using Microsoft.AspNetCore.Components;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Web.Components.Views.Projects;

/// <summary>
/// 專案成員對話框（0.4.99）。名單與使用者管理頁的「所屬團隊（專案）」是同一份 <c>ProjectMember</c>。
/// 權限判斷在 <see cref="ProjectMemberService"/> 再做一次，這裡的 <see cref="CanManage"/>／<see cref="IsAdmin"/>
/// 只決定按鈕顯不顯示。
/// </summary>
public partial class ProjectMemberModal : ComponentBase
{
    [Inject]
    private ProjectMemberService MemberService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private MessageService MessageService { get; set; } = default!;

    [Parameter]
    public bool Visible { get; set; }

    [Parameter]
    public EventCallback<bool> VisibleChanged { get; set; }

    [Parameter]
    public int ProjectId { get; set; }

    [Parameter]
    public string ProjectTitle { get; set; } = string.Empty;

    /// <summary>負責人或管理者：可以加減協作者。</summary>
    [Parameter]
    public bool CanManage { get; set; }

    /// <summary>管理者：另外可以轉移負責人。</summary>
    [Parameter]
    public bool IsAdmin { get; set; }

    /// <summary>成員有變動時通知專案頁（負責人換人時摘要列的姓名要跟著更新）。</summary>
    [Parameter]
    public EventCallback OnChanged { get; set; }

    private List<ProjectMemberItem> members = [];
    private List<AssignableUser> candidates = [];
    private int selectedUserId;
    private bool isBusy;

    /// <summary>上一次開啟時載入的專案，避免每次重繪都重查。</summary>
    private int? loadedProjectId;

    protected override async Task OnParametersSetAsync()
    {
        if (!Visible)
        {
            loadedProjectId = null;
            return;
        }

        if (ProjectId <= 0 || loadedProjectId == ProjectId)
        {
            return;
        }

        loadedProjectId = ProjectId;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        members = await MemberService.GetMembersAsync(ProjectId);

        if (CanManage)
        {
            var memberIds = members.Select(x => x.UserId).ToHashSet();
            candidates = [.. (await MemberService.GetAssignableUsersAsync()).Where(x => !memberIds.Contains(x.Id))];
        }

        selectedUserId = 0;
    }

    private async Task AddAsync()
    {
        await RunAsync(() => MemberService.AddCollaboratorAsync(ProjectId, selectedUserId), "已加入協作者");
    }

    private async Task RemoveAsync(ProjectMemberItem member)
    {
        var ok = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "移除協作者",
            Content = $"確定要把「{member.Name}」移出這個專案嗎？移除後他就看不到這個專案與底下的會議紀錄、待辦。",
            OkText = "移除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false,
        });
        if (ok)
        {
            await RunAsync(() => MemberService.RemoveCollaboratorAsync(ProjectId, member.UserId), "已移除協作者");
        }
    }

    private async Task SetOwnerAsync(ProjectMemberItem member)
    {
        var ok = await ModalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "轉移負責人",
            Content = $"確定要把負責人轉給「{member.Name}」嗎？原負責人會改成協作者，仍看得到這個專案。",
            OkText = "轉移",
            CancelText = "取消",
            MaskClosable = false,
        });
        if (ok)
        {
            await RunAsync(() => MemberService.SetOwnerAsync(ProjectId, member.UserId), "已轉移負責人");
        }
    }

    private async Task RunAsync(Func<Task<MeetingRecord.Models.Systems.VerifyRecordResult>> action, string successMessage)
    {
        isBusy = true;
        try
        {
            var result = await action();
            if (!result.Success)
            {
                _ = MessageService.ErrorAsync(result.Message);
                return;
            }

            _ = MessageService.SuccessAsync(successMessage);
            await LoadAsync();
            await OnChanged.InvokeAsync();
        }
        finally
        {
            isBusy = false;
        }
    }

    private Task CloseAsync() => VisibleChanged.InvokeAsync(false);
}
