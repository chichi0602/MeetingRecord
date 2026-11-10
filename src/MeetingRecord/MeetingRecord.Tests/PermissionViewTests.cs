using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Client.Auth;

namespace MeetingRecord.Tests;

/// <summary>
/// WASM 頁的按鈕與進頁判斷（1.1.7，<see cref="PermissionView"/>）要與伺服器的規則一致：
/// 進頁面同 <see cref="AuthenticationStateHelper.CanAccessPage"/>，動作同 <c>AuthenticationStateHelper.CheckAccessAction</c>。
/// </summary>
public sealed class PermissionViewTests
{
    private const string Page = MagicObjectHelper.角色_分類清單;

    public static TheoryData<string[], bool> KeySets => new()
    {
        { [], false },
        { [], true },
        { [Page], false },
        { [PermissionKey.For(Page, PermissionActions.View)], false },
        { [PermissionKey.For(Page, PermissionActions.Edit)], false },
        { [PermissionKey.For(MagicObjectHelper.角色_團隊清單, PermissionActions.View)], false },
    };

    [Theory]
    [MemberData(nameof(KeySets))]
    public void CanAccessPage_ShouldMatchServerRule(string[] keys, bool isAdmin)
    {
        Assert.Equal(
            AuthenticationStateHelper.CanAccessPage(keys, isAdmin, Page),
            PermissionView.CanAccessPage(keys, isAdmin, Page));
    }

    [Fact]
    public void Can_ShouldAcceptActionKeyOrWholePageKey_AndAdmin()
    {
        string[] editOnly = [PermissionKey.For(Page, PermissionActions.Edit)];

        Assert.True(PermissionView.Can(editOnly, isAdmin: false, Page, PermissionActions.Edit));
        Assert.False(PermissionView.Can(editOnly, isAdmin: false, Page, PermissionActions.Delete));
        Assert.True(PermissionView.Can([Page], isAdmin: false, Page, PermissionActions.Delete));
        Assert.True(PermissionView.Can([], isAdmin: true, Page, PermissionActions.Delete));
        Assert.False(PermissionView.Can([PermissionKey.For(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)], isAdmin: false, Page, PermissionActions.Delete));
    }
}
