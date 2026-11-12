using System.Xml.Linq;

namespace MeetingRecord.Tests;

/// <summary>
/// 瀏覽器端專案（1.1.1） <c>MeetingRecord.Web.Client</c> 只能相依 Dtos／Share／Models。
/// 參考到 Business／AccessDatas／Web 的話，伺服器的程式（含 EF Core、檔案存取、金鑰設定）會被一起打包送到瀏覽器。
/// </summary>
public sealed class WebClientArchitectureTests
{
    private static readonly string[] AllowedProjects = ["MeetingRecord.Dtos", "MeetingRecord.Share", "MeetingRecord.Models"];

    [Fact]
    public void WebClient_ShouldOnlyReferenceSharedProjects()
    {
        var project = LoadProject();

        var references = project.Descendants("ProjectReference")
            .Select(x => Path.GetFileNameWithoutExtension((string?)x.Attribute("Include") ?? string.Empty))
            .ToList();

        Assert.NotEmpty(references);
        var violations = references.Where(x => !AllowedProjects.Contains(x)).ToList();
        Assert.True(violations.Count == 0, "MeetingRecord.Web.Client 只能參考 Dtos／Share／Models，卻參考了：" + string.Join("、", violations));
    }

    [Fact]
    public void WebClient_ShouldNotReferenceServerOnlyPackages()
    {
        var packages = LoadProject().Descendants("PackageReference")
            .Select(x => (string?)x.Attribute("Include") ?? string.Empty)
            .ToList();

        var violations = packages.Where(x => x.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                                             || x.StartsWith("Microsoft.AspNetCore.Authentication", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(violations.Count == 0, "MeetingRecord.Web.Client 不可參考伺服器端套件：" + string.Join("、", violations));
    }

    private static XDocument LoadProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MeetingRecord.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory!.FullName, "MeetingRecord.Web.Client", "MeetingRecord.Web.Client.csproj"));
    }
}
