using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.0.68：為修補 Microsoft.OpenApi 的弱點升級 Swashbuckle／Microsoft.AspNetCore.OpenApi 後，API 文件仍要產得出來、Bearer 安全定義還在。
/// 測試環境不開 Swagger UI，直接用產生器驗證。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class SwaggerDocumentTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    [Fact]
    public void SwaggerDocument_ShouldGenerate_WithBearerSecurityScheme()
    {
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        Assert.NotEmpty(document.Paths);
        Assert.Contains(document.Paths.Keys, path => path.Contains("/Project", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(document.Components?.SecuritySchemes);
        Assert.True(document.Components!.SecuritySchemes!.ContainsKey(JwtBearerDefaults.AuthenticationScheme), "Swagger 要保留 Bearer 安全定義");
    }
}
