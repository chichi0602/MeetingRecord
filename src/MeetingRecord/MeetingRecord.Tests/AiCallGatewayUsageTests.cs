using System.Text.RegularExpressions;

namespace MeetingRecord.Tests;

/// <summary>
/// 守住「付費 AI 呼叫一律經過 <c>AiCallGateway</c>」（1.0.29）。
/// 繞過它直接呼叫供應商的程式不會被用量上限擋、不會進帳本、也不會有 AI 對話紀錄——
/// 而且編譯器與其他測試都不會發現。這裡掃原始碼，新增的呼叫點忘了走 gateway 會在這裡失敗。
/// </summary>
public sealed class AiCallGatewayUsageTests
{
    /// <summary>可以直接呼叫供應商的檔案：gateway 本身與供應商實作。</summary>
    private static readonly string[] AllowedFiles =
    [
        "AiCallGateway.cs",
        "AzureOpenAiTextGenerationProvider.cs",
        "AzureOpenAiTranscriptionProvider.cs",
    ];

    /// <summary>
    /// 「某個供應商變數.GenerateAsync(」或「.TranscribeAsync(」。gateway 的同名方法是
    /// <c>gateway.GenerateAsync(</c>，用變數名稱區分；呼叫端一律把 gateway 欄位命名為 gateway。
    /// </summary>
    private static readonly Regex DirectProviderCall = new(
        @"\b(?!gateway\b)\w+\.(GenerateAsync|TranscribeAsync)\(",
        RegexOptions.Compiled);

    [Fact]
    public void ProviderCalls_ShouldOnlyGoThroughTheGateway()
    {
        var offenders = new List<string>();
        foreach (var file in EnumerateSourceFiles())
        {
            if (AllowedFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (DirectProviderCall.IsMatch(lines[index]))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{index + 1}  {line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "以下程式繞過 AiCallGateway 直接呼叫 AI 供應商（不會被用量上限擋、不會記帳、沒有對話紀錄）：\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Regex_ShouldCatchDirectCallsButNotTheGateway()
    {
        Assert.Matches(DirectProviderCall, "var r = await provider.GenerateAsync(system, user, null, ct);");
        Assert.Matches(DirectProviderCall, "await transcriptionProvider.TranscribeAsync(stream, name, ct);");
        Assert.DoesNotMatch(DirectProviderCall, "var r = await gateway.GenerateAsync(provider, ctx, s, u, null, ct);");
        Assert.DoesNotMatch(DirectProviderCall, "await gateway.TranscribeAsync(provider, ctx, stream, name, 1, false, 1, 1, ct);");
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var business = Path.Combine(directory.FullName, "MeetingRecord.Business");
            var web = Path.Combine(directory.FullName, "MeetingRecord.Web");
            if (Directory.Exists(business) && Directory.Exists(web))
            {
                return new[] { business, web }
                    .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    .ToList();
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("找不到 MeetingRecord.Business／MeetingRecord.Web。");
    }
}
