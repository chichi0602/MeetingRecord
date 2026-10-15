using System.Reflection;
using System.Text.RegularExpressions;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 稽核動作代碼的慣例（1.0.32）：
/// <list type="bullet">
/// <item>每個代碼都有中文名稱，畫面才不會冒出英文代碼。</item>
/// <item>寫稽核一律用 <see cref="AuditActions"/> 的常數，不寫字串常值——常值打錯字不會編譯失敗，只會多出一種查不到的動作。</item>
/// </list>
/// </summary>
public sealed partial class AuditActionsConventionTests
{
    [Fact]
    public void EveryActionConstant_ShouldHaveADisplayName_AndUseNounDotVerb()
    {
        var codes = typeof(AuditActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(x => x.IsLiteral && x.FieldType == typeof(string))
            .Select(x => (string)x.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code =>
        {
            Assert.Matches(ActionCodePattern(), code);
            Assert.True(AuditActions.DisplayNames.ContainsKey(code), $"AuditActions.DisplayNames 缺少「{code}」的中文名稱。");
        });
        Assert.Equal(codes.Order(StringComparer.Ordinal), AuditActions.DisplayNames.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AuditWriters_ShouldNotPassStringLiteralActionCodes()
    {
        var root = FindSourceRoot();
        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsExcluded(path))
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(x => LiteralWritePattern().IsMatch(x.line))
            .Select(x => $"{Path.GetRelativePath(root, x.path)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "寫稽核請改用 AuditActions 常數：\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void Describe_ShouldFallBackToTheCodeForUnknownActions()
    {
        Assert.Equal("登入成功", AuditActions.Describe(AuditActions.LoginSuccess));
        Assert.Equal("Legacy.Retired", AuditActions.Describe("Legacy.Retired"));
        Assert.Equal(string.Empty, AuditActions.Describe(null));
        Assert.Equal("Login", AuditActions.CategoryOf(AuditActions.LoginSuccess));
        Assert.Equal("NoDot", AuditActions.CategoryOf("NoDot"));
    }

    private static bool IsExcluded(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/") || normalized.Contains("/obj/")
            || normalized.Contains("/MeetingRecord.Tests/") || normalized.Contains("/Migrations/");
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

    [GeneratedRegex(@"^[A-Z][A-Za-z]*\.[A-Z][A-Za-z]*$")]
    private static partial Regex ActionCodePattern();

    /// <summary><c>WriteAsync("X.Y"</c>、<c>WriteAuditAsync("X.Y"</c>、<c>WriteAuditAsync(scope, "X.Y"</c> 這類寫法。</summary>
    [GeneratedRegex(@"\bWrite\w*Async\(\s*(\w+\s*,\s*)?""[A-Za-z]+\.[A-Za-z]+""")]
    private static partial Regex LiteralWritePattern();
}
