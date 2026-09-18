namespace MeetingRecord.Tests;

/// <summary>
/// 掃描所有 <c>.razor</c> 的註解，守住一個**編譯得過、但會把開發者註解渲染到使用者畫面上**的缺陷。
///
/// <para>
/// Razor 的註解不能巢狀：它從開始符號往後找到**第一個**結束符號就收工。
/// 所以註解內文一旦出現結束符號（例如想在註解裡引用那組符號來說明用法），
/// 註解會提前關閉，後面的文字變成一般 markup **直接顯示在畫面上**。
/// </para>
///
/// <para>
/// 這不是假想的風險：0.4.82 就這樣讓一段說明文字外洩到會議紀錄頁的底部，
/// 而且那段註解本身正是在警告另一個 Razor 註解的坑——它引用了符號，結果自己把自己關掉了。
/// 使用者是看到畫面才回報的。
/// </para>
///
/// <para>
/// ⚠️ **平衡計數（開幾個、關幾個）抓不到它**——那個案例正好是開 2 關 2。
/// 一定要照 Razor 的規則依序切出註解區段才驗得出來。
/// </para>
/// </summary>
public sealed class RazorMarkupTests
{
    private const string CommentOpen = "@" + "*";
    private const string CommentClose = "*" + "@";

    [Fact]
    public void RazorComments_ShouldNotContainTheirOwnCloser()
    {
        var offenders = new List<string>();

        foreach (var file in EnumerateRazorFiles())
        {
            var text = File.ReadAllText(file);
            var from = 0;

            while (true)
            {
                var open = text.IndexOf(CommentOpen, from, StringComparison.Ordinal);
                if (open < 0)
                {
                    break;
                }

                var close = text.IndexOf(CommentClose, open + CommentOpen.Length, StringComparison.Ordinal);
                if (close < 0)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{LineOf(text, open)}：註解沒有結束");
                    break;
                }

                // 內文又出現開始符號，幾乎一定是「想引用符號本身」而提前把註解關掉了。
                var body = text[(open + CommentOpen.Length)..close];
                if (body.Contains(CommentOpen, StringComparison.Ordinal))
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)}:{LineOf(text, open)}：註解內文含開始符號，"
                        + "代表它在更前面就被關掉了，後面的文字會渲染到畫面上");
                }

                from = close + CommentClose.Length;
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Razor 註解不能巢狀，內文不可以出現註解符號（用文字敘述代替）：\n"
                + string.Join('\n', offenders));
    }

    [Fact]
    public void Scanner_ShouldActuallyFindRazorFiles()
    {
        // ⚠️ 沒有這一條的話，上面那支在「掃描器壞掉、一個檔案都沒讀到」時會全綠——
        // 那是最糟的失敗模式：測試看起來在守，其實什麼都沒守。
        var files = EnumerateRazorFiles().ToList();

        Assert.True(files.Count >= 20, $"只掃到 {files.Count} 個 .razor，掃描器可能壞了。");
    }

    private static int LineOf(string text, int index)
        => text.AsSpan(0, index).Count('\n') + 1;

    private static IEnumerable<string> EnumerateRazorFiles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "MeetingRecord.Web", "Components");
            if (Directory.Exists(candidate))
            {
                return Directory.EnumerateFiles(candidate, "*.razor", SearchOption.AllDirectories);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MeetingRecord.Web/Components。");
    }
}
