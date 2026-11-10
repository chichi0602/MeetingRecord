using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 每個會顯示「你沒有權限存取此頁面」的頁面都要寫權限拒絕稽核（1.0.33）。
/// 新增頁面時只複製了守門判斷、忘了這一行，編譯與其他測試都不會失敗，所以掃原始碼守門。
/// </summary>
public sealed partial class PageAccessDeniedAuditTests
{
    [Fact]
    public void EveryDenialBranch_ShouldRecordPageAccessDenied()
    {
        // 1.1.6（WASM 遷移）起頁面會陸續搬到 WASM 專案：Server 頁（Views）呼叫 AuthenticationStateHelper.RecordPageAccessDeniedAsync，
        // WASM 頁（Web.Client）呼叫 PageAccessApi.RecordPageDeniedAsync，兩種都算。
        var root = FindSourceRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "MeetingRecord.Web", "Components", "Views"), "*.razor.cs", SearchOption.AllDirectories)
            .Concat(ComponentSourceRoots.EnumerateFiles("*.razor.cs").Where(path => path.StartsWith(ComponentSourceRoots.WebClient, StringComparison.Ordinal)))
            .ToList();

        var denialFiles = files.Where(path => DenialPattern().IsMatch(File.ReadAllText(path))).ToList();
        var missing = denialFiles
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return !text.Contains("RecordPageAccessDeniedAsync(\"/", StringComparison.Ordinal)
                    && !text.Contains("RecordPageDeniedAsync(\"/", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(denialFiles.Count >= 17, $"只找到 {denialFiles.Count} 個拒絕分支，掃描範圍可能錯了。");
        Assert.True(missing.Count == 0, "這些頁面擋下使用者時沒有寫稽核：\n" + string.Join('\n', missing));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MeetingRecord.slnx")))
            {
                return dir.FullName;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "MeetingRecord");
            if (File.Exists(Path.Combine(srcCandidate, "MeetingRecord.slnx")))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MeetingRecord.slnx。");
    }

    [GeneratedRegex(@"RoleMessage\s*=\s*(MagicObjectHelper\.你沒有權限存取此頁面|""你沒有權限存取此頁面"")")]
    private static partial Regex DenialPattern();
}
