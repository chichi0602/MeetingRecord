using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Business.Helpers;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Models;
using MeetingRecord.Models.Systems;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 提示詞 API（1.1.9）走畫面用的 <c>PromptTemplateService</c>，規則與畫面一致。
/// 釘住外部呼叫端看得到的行為變更：名稱重複不分大小寫與前後空白、分類（新欄位 categoryNames 與舊的儲存格式）、
/// 內建範本（全部、單一）、啟用切換與稽核、啟用分類清單、搜尋篩選與排序。
/// 內建範本會動到全表（同名略過），所以這組測試只檢查自己造的名稱或「第二次全部略過」。
/// </summary>
[Collection(nameof(ApiHostCollection))]
public sealed class PromptTemplateApiTests(ApiTestApplicationFactory factory) : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "prompt-api-pass";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Create_NameDifferingOnlyByCaseOrSpaces_ShouldConflict()
    {
        using var client = await LoginAsync();
        var name = $"Minutes-{Guid.NewGuid():N}";
        await CreateAsync(client, new PromptTemplateCreateUpdateDto { Id = 0, Name = name, Content = "請整理會議紀錄。" });

        using var duplicate = await client.PostAsJsonAsync("/api/v1/PromptTemplate", new PromptTemplateCreateUpdateDto { Id = 0, Name = $" {name.ToUpperInvariant()} ", Content = "x" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("提示詞名稱已存在，無法新增。", (await ReadAsync<PromptTemplateDto>(duplicate)).ErrorMessage);
    }

    [Fact]
    public async Task Categories_ShouldAcceptNamesOrStoredFormat_AndBeFilterable()
    {
        using var client = await LoginAsync();
        var tag = $"分類-{Guid.NewGuid():N}";

        // 新欄位：去空白、去重複（不分大小寫）後存成標準格式；回應兩個欄位都有。
        var byNames = await CreateAsync(client, new PromptTemplateCreateUpdateDto { Id = 0, Name = $"新欄位-{Guid.NewGuid():N}", Content = "x", CategoryNames = [$" {tag} ", tag, "其他"] });
        Assert.Equal([tag, "其他"], byNames.CategoryNames);
        Assert.Equal($"\n{tag}\n其他\n", byNames.Categories);

        // 舊欄位（儲存格式）照樣認得。
        var byStored = await CreateAsync(client, new PromptTemplateCreateUpdateDto { Id = 0, Name = $"舊欄位-{Guid.NewGuid():N}", Content = "x", Categories = $"\n{tag}\n" });
        Assert.Equal([tag], byStored.CategoryNames);

        // 存成標準格式之後，分類篩選對得到（服務層的 CategoryFilters）。
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<PromptTemplateService>();
        var filtered = await service.GetAsync(new DataRequest { CategoryFilters = [tag], PageSize = 50 });
        Assert.Equal([byNames.Id!.Value, byStored.Id!.Value], filtered.Result.Select(x => x.Id).Order());

        // 修改時帶 categoryNames 會取代舊的分類。
        using (var update = await client.PutAsJsonAsync($"/api/v1/PromptTemplate/{byStored.Id}", new PromptTemplateCreateUpdateDto { Id = byStored.Id, Name = byStored.Name, Content = "x", CategoryNames = ["改過"] }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        }

        var reloaded = (await ReadAsync<PromptTemplateDto>(await client.GetAsync($"/api/v1/PromptTemplate/{byStored.Id}"))).Data!;
        Assert.Equal(["改過"], reloaded.CategoryNames);
    }

    [Fact]
    public async Task Presets_All_ShouldBeIdempotent_AndSingle_ShouldConflictOrNotFound()
    {
        using var client = await LoginAsync();

        var names = (await ReadAsync<List<string>>(await client.GetAsync("/api/v1/PromptTemplate/presets"))).Data!;
        Assert.Equal(PromptTemplatePresets.All.Select(x => x.Name), names);

        using (var first = await client.PostAsync("/api/v1/PromptTemplate/presets", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // 第二次全部略過。
        using (var second = await client.PostAsync("/api/v1/PromptTemplate/presets", content: null))
        {
            Assert.Equal($"已新增 0 筆內建範本，略過 {PromptTemplatePresets.All.Count} 筆（已有同名提示詞）。", (await ReadAsync<object>(second)).Message);
        }

        var preset = PromptTemplatePresets.All[0];
        using (var exists = await client.PostAsJsonAsync("/api/v1/PromptTemplate/presets/single", new PresetNameRequestDto { Name = preset.Name }))
        {
            Assert.Equal(HttpStatusCode.Conflict, exists.StatusCode);
            Assert.Equal($"「{preset.Name}」已存在，未新增。", (await ReadAsync<PromptTemplateDto>(exists)).ErrorMessage);
        }

        using (var unknown = await client.PostAsJsonAsync("/api/v1/PromptTemplate/presets/single", new PresetNameRequestDto { Name = "不存在的範本" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        // 刪掉之後再加單一範本：成功回新建的資料。
        await DeleteByNameAsync(preset.Name);
        using var added = await client.PostAsJsonAsync("/api/v1/PromptTemplate/presets/single", new PresetNameRequestDto { Name = preset.Name });
        var dto = (await ReadAsync<PromptTemplateDto>(added)).Data!;
        Assert.Equal(preset.Name, dto.Name);
        Assert.Equal(preset.Content, dto.Content);
        Assert.True(dto.IsEnabled);
    }

    [Fact]
    public async Task SetEnabled_ShouldToggleAndAudit()
    {
        var (account, userId) = await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_提示詞清單]);
        using var client = await LoginAsync(account);
        var template = await CreateAsync(client, new PromptTemplateCreateUpdateDto { Id = 0, Name = $"切換-{Guid.NewGuid():N}", Content = "x" });

        using (var response = await client.PutAsJsonAsync($"/api/v1/PromptTemplate/{template.Id}/enabled", new SetEnabledRequestDto { IsEnabled = false }))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        Assert.False((await db.PromptTemplate.AsNoTracking().SingleAsync(x => x.Id == template.Id)).IsEnabled);
        Assert.Equal(userId, (await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PromptTemplateDisable && x.TargetId == template.Id.ToString())).ActorUserId);
        Assert.EndsWith("via=Api", (await db.AuditLog.AsNoTracking().SingleAsync(x => x.Action == AuditActions.PromptTemplateCreate && x.TargetId == template.Id.ToString())).Detail);
    }

    [Fact]
    public async Task EnabledCategories_ShouldListOnlyEnabled()
    {
        var enabled = $"啟用分類-{Guid.NewGuid():N}";
        var disabled = $"停用分類-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            db.Category.AddRange(new Category { Name = enabled, IsEnabled = true }, new Category { Name = disabled, IsEnabled = false });
            await db.SaveChangesAsync();
        }

        using var client = await LoginAsync();
        var names = (await ReadAsync<List<string>>(await client.GetAsync("/api/v1/PromptTemplate/categories/enabled"))).Data!;

        Assert.Contains(enabled, names);
        Assert.DoesNotContain(disabled, names);
    }

    [Fact]
    public async Task Search_ShouldFilterByEnabledAndSortByName()
    {
        using var client = await LoginAsync();
        var prefix = $"搜尋提示詞-{Guid.NewGuid():N}";
        var b = await CreateAsync(client, new PromptTemplateCreateUpdateDto { Id = 0, Name = $"{prefix}-b", Content = "x" });
        var a = await CreateAsync(client, new PromptTemplateCreateUpdateDto { Id = 0, Name = $"{prefix}-a", Content = "x" });
        var c = await CreateAsync(client, new PromptTemplateCreateUpdateDto { Id = 0, Name = $"{prefix}-c", Content = "x", IsEnabled = false });

        var enabledByName = await SearchAsync(client, new PromptTemplateSearchRequestDto { Keyword = prefix, IsEnabled = true, SortBy = "name" });
        Assert.Equal([a.Id, b.Id], enabledByName.Items.Select(x => x.Id));

        var allByNameDesc = await SearchAsync(client, new PromptTemplateSearchRequestDto { Keyword = prefix, SortBy = "NAME", SortDescending = true });
        Assert.Equal([c.Id, b.Id, a.Id], allByNameDesc.Items.Select(x => x.Id));
    }

    private async Task DeleteByNameAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        await db.PromptTemplate.Where(x => x.Name == name).ExecuteDeleteAsync();
    }

    private static async Task<PromptTemplateDto> CreateAsync(HttpClient client, PromptTemplateCreateUpdateDto dto)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/PromptTemplate", dto);
        var result = await ReadAsync<PromptTemplateDto>(response);
        Assert.True(result.Success, result.ErrorMessage);
        return result.Data!;
    }

    private static async Task<PagedResult<PromptTemplateDto>> SearchAsync(HttpClient client, PromptTemplateSearchRequestDto request)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/PromptTemplate/search", request);
        return (await ReadAsync<PagedResult<PromptTemplateDto>>(response)).Data!;
    }

    private async Task<HttpClient> LoginAsync(string? account = null)
    {
        account ??= (await TestCookieLogin.CreateUserAsync(factory.Services, Password, [MagicObjectHelper.角色_提示詞清單])).Account;
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await TestCookieLogin.LoginAsync(client, account, Password);
        client.DefaultRequestHeaders.Add(MagicObjectHelper.ClientRequestHeader, "1");
        return client;
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<ApiResult<T>>(await response.Content.ReadAsStringAsync(), JsonOptions)
           ?? throw new InvalidOperationException("回應不是 ApiResult");
}
