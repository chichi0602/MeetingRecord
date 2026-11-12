using Microsoft.EntityFrameworkCore;
using MeetingRecord.E2ETests.Infrastructure;

namespace MeetingRecord.E2ETests.Scenarios;

/// <summary>自己的帳號：設定 API 密碼（/Profile，1.1.13）。</summary>
public sealed class AccountTests(E2EAppFixture app) : E2ETestBase(app)
{
    [E2EFact]
    [Trait("Scenario", "ACC-01")]
    [Trait("Title", "在設定 API 密碼頁設定新密碼後，可以用帳號加新密碼呼叫 API 登入")]
    [Trait("Page", "設定 API 密碼")]
    [Trait("Persona", "一般帳號")]
    public async Task Profile_SetApiPassword_ShouldAllowApiLogin()
    {
        // 另建一個帳號：設定 API 密碼會換掉登入密碼，不能動種子帳號。
        var persona = new E2EPersona("acc", Unique("e2e.apipass"), "設定 API 密碼", "E2e-Acc-Pass-1");
        await using (var db = App.CreateDbContext())
        {
            var role = await db.RoleView.FirstAsync(x => x.Name == MeetingRecord.Share.Helpers.MagicObjectHelper.預設角色);
            var user = new MeetingRecord.AccessDatas.Models.MyUser
            {
                Account = persona.Account,
                Name = persona.Name,
                Status = true,
                RoleViewId = role.Id,
                Password = MeetingRecord.Business.Helpers.SecurePasswordHasher.HashPassword(persona.Password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            db.UserRole.Add(new MeetingRecord.AccessDatas.Models.UserRole { MyUserId = user.Id, RoleViewId = role.Id });
            await db.SaveChangesAsync();
        }

        var page = await LoginAsAsync(persona);
        await GotoAsync(page, "/Profile", "設定 API 密碼");
        await page.GetByPlaceholder("請輸入目前密碼").FillAsync(persona.Password);
        await page.GetByPlaceholder("請輸入新密碼").FillAsync("E2e-Api-Pass-2");
        await page.GetByPlaceholder("請再次輸入新密碼").FillAsync("E2e-Api-Pass-2");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲 存" }).Or(page.GetByRole(AriaRole.Button, new() { Name = "儲存" })).First.ClickAsync();
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/meetings$"), new() { Timeout = Timeout });

        var login = await page.APIRequest.PostAsync("/api/Auth/login", new()
        {
            // 這個請求會帶著瀏覽器的登入 Cookie，Cookie 寫入要帶防偽標頭（否則 403）。
            Headers = new Dictionary<string, string> { [MeetingRecord.Share.Helpers.MagicObjectHelper.ClientRequestHeader] = "1" },
            DataObject = new { account = persona.Account, password = "E2e-Api-Pass-2" },
        });
        Assert.Equal(200, login.Status);
    }
}
