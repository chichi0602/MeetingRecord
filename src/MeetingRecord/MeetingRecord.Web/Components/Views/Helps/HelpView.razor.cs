using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.Components.Views.Helps;

/// <summary>
/// 使用說明頁。
///
/// <para>
/// 內容<b>不寫在這裡</b>，而是 <c>docs/guides/系統使用說明.md</c> 那一份——
/// 它在建置時以內嵌資源的形式被放進組件（見 <c>MeetingRecord.Web.csproj</c>）。
/// 這樣「文件庫的使用說明」與「系統裡的使用說明」永遠是同一份內容，
/// 不會出現改了一邊忘了另一邊的情況。
/// </para>
///
/// <para>
/// ⚠️ 刻意<b>不</b>從檔案系統讀 <c>docs/</c>：那個目錄不會被發佈到輸出目錄，
/// 會變成「開發時看得到、上線後 404」。
/// </para>
/// </summary>
public partial class HelpView : ComponentBase
{
    /// <summary>
    /// 內嵌資源的名稱。與 csproj 的 <c>LogicalName</c> 必須一致——
    /// 改了一邊就會在執行期變成「載入中…」永遠不消失。
    /// </summary>
    private const string ResourceName = "MeetingRecord.Web.系統使用說明.md";

    private string? content;
    private string RoleMessage = string.Empty;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<HelpView> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_使用說明) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Help view denied because current user has not this role permission.");
            return;
        }

        content = StripDocumentFrontMatter(ReadEmbeddedManual());
    }

    /// <summary>
    /// 切掉文件開頭的維護資訊，只留正文。
    ///
    /// <para>
    /// `docs/` 的每份文件都有一段表頭（文件版本／文件狀態／現行系統版本／最後核對日期），
    /// 這一份還多一段「這個檔案同時是系統內說明頁的來源」的提醒。那些是寫給維護者看的，
    /// **對使用者只是雜訊**——所以保留在檔案裡（文件庫的慣例不破），但不顯示在畫面上。
    /// </para>
    ///
    /// <para>
    /// 切法是「第一條水平線之前全部丟掉」。找不到分隔線時原樣回傳，
    /// 寧可多顯示一段表頭，也不要因為格式改了就整頁空白。
    /// </para>
    ///
    /// <remarks>public 是為了讓測試看得到——Web 專案沒有設 InternalsVisibleTo，
    /// 為了一個方法去開放整個組件的內部成員並不划算。</remarks>
    /// </summary>
    public static string StripDocumentFrontMatter(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var lines = markdown.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Trim() == "---")
            {
                return string.Join('\n', lines.Skip(index + 1)).TrimStart('\n', '\r');
            }
        }

        return markdown;
    }

    /// <summary>
    /// 讀出內嵌的使用說明。
    ///
    /// <para>
    /// ⚠️ <b>一定要去掉 BOM。</b><c>docs/*.md</c> 一律是 UTF-8 含 BOM（CI 強制檢查），
    /// 不處理的話字串第一個字元會是 <c>﻿</c>，Markdig 會把第一行的
    /// <c># 標題</c> 當成普通段落，整頁的標題層級就跑掉了。
    /// <c>detectEncodingFromByteOrderMarks</c> 會幫我們吃掉它。
    /// </para>
    /// </summary>
    private string ReadEmbeddedManual()
    {
        var assembly = Assembly.GetExecutingAssembly();

        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // 這是建置設定壞掉，不是使用者的問題——要留下線索，不要只顯示空白頁。
            Logger.LogError(
                "Embedded manual not found. ResourceName={ResourceName}, Available={Available}",
                ResourceName,
                string.Join("、", assembly.GetManifestResourceNames()));

            return "使用說明載入失敗，請聯絡系統管理員。";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
