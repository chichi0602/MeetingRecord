using MeetingRecord.Web.Configuration;

namespace MeetingRecord.Tests;

public class CacheSettingsTests
{
    [Fact]
    public void GetProvider_ShouldDefaultToMemory_WhenBlank()
    {
        var settings = new CacheSettings { Provider = string.Empty };

        Assert.Equal(CacheProvider.Memory, settings.GetProvider());
    }

    [Theory]
    [InlineData("Redis")]
    [InlineData("redis")]
    [InlineData("REDIS")]
    public void GetProvider_ShouldParseRedis_CaseInsensitive(string provider)
    {
        var settings = new CacheSettings { Provider = provider };

        Assert.Equal(CacheProvider.Redis, settings.GetProvider());
    }

    [Fact]
    public void GetProvider_ShouldThrow_WhenUnknown()
    {
        var settings = new CacheSettings { Provider = "Memcached" };

        Assert.Throws<InvalidOperationException>(() => settings.GetProvider());
    }
}
