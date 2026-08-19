namespace MeetingRecord.Web.Configuration;

public class CacheSettings
{
    public const string SectionName = "CacheSettings";

    public string Provider { get; set; } = nameof(CacheProvider.Memory);
    public string RedisConnection { get; set; } = string.Empty;
    public string InstanceName { get; set; } = "MeetingRecord:";
    public int DefaultExpirationMinutes { get; set; } = 30;

    public CacheProvider GetProvider()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            return CacheProvider.Memory;
        }

        if (Enum.TryParse<CacheProvider>(Provider, ignoreCase: true, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"不支援的快取 provider：{Provider}");
    }
}

public enum CacheProvider
{
    Memory,
    Redis
}
