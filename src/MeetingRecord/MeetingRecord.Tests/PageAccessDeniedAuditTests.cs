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
        // 頁面全部在 WASM 專案（Web.Client），擋下時呼叫 PageAccessApi.RecordPageDeniedAsync。
        var root = FindSourceRoot();
        var files = ComponentSourceRoots.EnumerateFiles("*.razor.cs")
            .Where(path => path.StartsWith(ComponentSourceRoots.WebClient, StringComparison.Ordinal))
            .ToList();

        var denialFiles = files.Where(path => DenialPattern().IsMatch(File.ReadAllText(path))).ToList();
        var missing = denialFiles
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return !text.Contains("RecordPageDeniedAsync(\"/", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        // 17 是 1.1.46 時全部會擋人的頁面數；少於這個數代表掃描範圍錯了，不是少了頁面。
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
