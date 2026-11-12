namespace MeetingRecord.Tests;

/// <summary>
/// 原始碼掃描測試要看的元件來源（1.1.5）：伺服器靜態算繪的 <c>MeetingRecord.Web/Components</c>（登入、登出、錯誤頁），
/// 加上 WASM 前端 <c>MeetingRecord.Web.Client</c>（應用程式的頁面與共用元件）。兩邊都掃，
/// 元件換專案時規則才不會失守、測試卻還是綠的。<c>bin</c>／<c>obj</c> 底下的產生檔不算。
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
