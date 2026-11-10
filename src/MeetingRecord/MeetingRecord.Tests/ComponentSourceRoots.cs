namespace MeetingRecord.Tests;

/// <summary>
/// 原始碼掃描測試要看的元件來源（1.1.5，WASM 遷移）：Blazor Server 的 <c>MeetingRecord.Web/Components</c>，
/// 加上 WASM 前端 <c>MeetingRecord.Web.Client</c>（共用元件與遷移後的頁面搬到這裡）。只掃一邊的話，
/// 元件一搬家規則就失守，測試還是綠的。<c>bin</c>／<c>obj</c> 底下的產生檔不算。
/// </summary>
internal static class ComponentSourceRoots
{
    public static string SolutionRoot { get; } = FindSolutionRoot();

    public static string WebComponents => Path.Combine(SolutionRoot, "MeetingRecord.Web", "Components");

    public static string WebClient => Path.Combine(SolutionRoot, "MeetingRecord.Web.Client");

    public static IReadOnlyList<string> All => [WebComponents, WebClient];

    public static IEnumerable<string> EnumerateFiles(string searchPattern)
        => All.SelectMany(root => Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(path));

    private static bool IsBuildOutput(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeetingRecord.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 MeetingRecord.slnx。");
    }
}
