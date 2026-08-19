using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MeetingRecord.Web.Caching;
using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Tests;

public class CacheServiceTests
{
    private sealed record Sample(int Id, string Name, List<string> Tags);

    [Fact]
    public async Task SetAsync_Then_GetAsync_ShouldRoundTripComplexObject()
    {
        var service = CreateService();
        var value = new Sample(7, "menu", ["a", "b"]);

        await service.SetAsync("key", value);
        var result = await service.GetAsync<Sample>("key");

        Assert.NotNull(result);
        Assert.Equal(value.Id, result!.Id);
        Assert.Equal(value.Name, result.Name);
        Assert.Equal(value.Tags, result.Tags);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnDefault_WhenMissing()
    {
        var service = CreateService();

        var result = await service.GetAsync<Sample>("missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCreateAsync_ShouldInvokeFactoryOnce_ThenServeFromCache()
    {
        var service = CreateService();
        var calls = 0;

        Task<Sample> Factory()
        {
            calls++;
            return Task.FromResult(new Sample(1, "x", []));
        }

        var first = await service.GetOrCreateAsync("key", Factory);
        var second = await service.GetOrCreateAsync("key", Factory);

        Assert.Equal(1, calls);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Name, second.Name);
    }

    [Fact]
    public async Task RemoveAsync_ShouldEvictEntry()
    {
        var service = CreateService();
        await service.SetAsync("key", new Sample(1, "x", []));

        await service.RemoveAsync("key");
        var result = await service.GetAsync<Sample>("key");

        Assert.Null(result);
    }

    private static DistributedCacheService CreateService()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        return new DistributedCacheService(cache, Options.Create(new CacheSettings()));
    }
}
