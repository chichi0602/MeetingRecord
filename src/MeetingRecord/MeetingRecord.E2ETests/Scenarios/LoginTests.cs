using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

public sealed class LoginTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2EFact]
    [Trait("Scenario", "LGN-01")]
    [Trait("Title", "帳密登入成功並進入系統")]
    [Trait("Page", "登入")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Login_WithValidCredentials_ShouldEnterTheApp()
    {
        var page = await LoginAsAsync(Seed.Owner);

        await Expect(page).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Auths/"));
        await Expect(page.GetByText(Seed.Owner.Name).First).ToBeVisibleAsync();
    }

    [E2EFact]
    [Trait("Scenario", "LGN-02")]
    [Trait("Title", "密碼錯誤時顯示錯誤、停在登入頁")]
    [Trait("Page", "登入")]
    [Trait("Persona", "鎖定測試帳號")]
    public async Task Login_WithWrongPassword_ShouldShowErrorAndStay()
    {
        var page = await NewPageAsync();

        await SubmitLoginAsync(page, Seed.LockTarget.Account, "wrong-password");

        await Expect(page.Locator("p.error-message[role=alert]")).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Auths/Login"));
    }

    [E2EFact]
    [Trait("Scenario", "LGN-03")]
    [Trait("Title", "停用帳號在登入頁被擋下")]
    [Trait("Page", "登入")]
    [Trait("Persona", "已停用帳號")]
    public async Task Login_DisabledAccount_ShouldBeBlocked()
    {
        var page = await NewPageAsync();

        await SubmitLoginAsync(page, Seed.Disabled.Account, Seed.Disabled.Password);

        await Expect(page.Locator("p.error-message[role=alert]")).ToBeVisibleAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/Auths/"));
    }

    [E2EFact]
    [Trait("Scenario", "LGN-04")]
    [Trait("Title", "首次登入須改密碼時導向變更密碼頁")]
    [Trait("Page", "登入")]
    [Trait("Persona", "須改密碼帳號")]
    public async Task Login_MustChangePassword_ShouldRedirectToChangePassword()
    {
        var page = await NewPageAsync();

        await SubmitLoginAsync(page, Seed.MustChangePassword.Account, Seed.MustChangePassword.Password);

        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("ChangePassword", System.Text.RegularExpressions.RegexOptions.IgnoreCase), new() { Timeout = Timeout });
    }

    [E2EFact]
    [Trait("Scenario", "LGN-08")]
    [Trait("Title", "須改密碼的帳號在變更密碼頁改完密碼，就能進系統使用")]
    [Trait("Page", "變更密碼")]
    [Trait("Persona", "須改密碼帳號")]
    public async Task MustChangePassword_AfterChanging_ShouldEnterTheApp()
    {
        // 1.1.13（變更密碼頁搬到 WASM）：WASM 版面對「須改密碼」會導向這頁，這頁本身不能再導向自己。
        // 另建一個帳號，不動種子帳號（LGN-04、WASM-04 要用它的「須改密碼」狀態）。
        var account = Unique("e2e.firstlogin");
        const string initial = "E2e-First-Pass-1";
        await using (var db = App.CreateDbContext())
        {
            var role = await db.RoleView.FirstAsync(x => x.Name == MeetingRecord.Share.Helpers.MagicObjectHelper.預設角色);
            var user = new MeetingRecord.AccessDatas.Models.MyUser
            {
                Account = account,
                Name = "第一次登入",
                Status = true,
                RoleViewId = role.Id,
                MustChangePassword = true,
                Password = MeetingRecord.Business.Helpers.SecurePasswordHasher.HashPassword(initial),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            db.UserRole.Add(new MeetingRecord.AccessDatas.Models.UserRole { MyUserId = user.Id, RoleViewId = role.Id });
            await db.SaveChangesAsync();
        }

        var page = await NewPageAsync();
        await SubmitLoginAsync(page, account, initial);
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/ChangePassword", System.Text.RegularExpressions.RegexOptions.IgnoreCase), new() { Timeout = Timeout });

        await page.GetByPlaceholder("請輸入目前密碼").FillAsync(initial);
        await page.GetByPlaceholder("請輸入新密碼").FillAsync("E2e-New-Pass-2");
        await page.GetByPlaceholder("請再次輸入新密碼").FillAsync("E2e-New-Pass-2");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲 存" }).Or(page.GetByRole(AriaRole.Button, new() { Name = "儲存" })).First.ClickAsync();

        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/meetings$"), new() { Timeout = Timeout });
        await Expect(page.Locator(".sidebar").First).ToBeVisibleAsync();
        await Expect(page).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex("ChangePassword", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        await using (var db = App.CreateDbContext())
        {
            Assert.False((await db.MyUser.SingleAsync(x => x.Account == account)).MustChangePassword);
        }
    }

    [E2EFact]
    [Trait("Scenario", "LGN-05")]
    [Trait("Title", "登出後回到登入頁，再進受保護頁面會被導回登入")]
    [Trait("Page", "登入")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Logout_ShouldEndTheSession()
    {
        var page = await LoginAsAsync(Seed.Owner);

        await page.GotoAsync("/Auths/Logout");
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/Auths/"), new() { Timeout = Timeout });
        await page.GotoAsync("/projects");

        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/Auths/Login"), new() { Timeout = Timeout });
    }

    [E2EFact]
    [Trait("Scenario", "LGN-06")]
    [Trait("Title", "勾選記住我時登入 Cookie 有長效期")]
    [Trait("Page", "登入")]
    [Trait("Persona", "主責團隊成員")]
    public async Task Login_RememberMe_ShouldIssuePersistentCookie()
    {
        var page = await LoginAsAsync(Seed.Owner, rememberMe: true);

        var cookies = await page.Context.CookiesAsync();
        var auth = Assert.Single(cookies, c => c.Name == ".MeetingRecord.Auth");

        // 記住我是 30 天；沒勾的是工作階段 Cookie（Expires = -1）。這裡只要求至少超過一週。
        Assert.True(auth.Expires > DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds(), $"Cookie 到期時間 {auth.Expires}");
    }

    [E2EFact]
    [Trait("Scenario", "LGN-07")]
    [Trait("Title", "未登入直接開內頁會被導向登入頁")]
    [Trait("Page", "登入")]
    [Trait("Persona", "未登入")]
    public async Task Anonymous_ProtectedPage_ShouldRedirectToLogin()
    {
        var page = await NewPageAsync();

        await page.GotoAsync("/meetings");

        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/Auths/Login"), new() { Timeout = Timeout });
    }
}
