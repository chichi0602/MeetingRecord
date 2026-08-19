namespace MeetingRecord.Web.Health;

public interface IHealthLogReader
{
    HealthLogTail ReadLatestLines(int lineCount);
}

public sealed class HealthLogReader : IHealthLogReader
{
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;

    public HealthLogReader(IConfiguration configuration, IWebHostEnvironment environment)
    {
        this.configuration = configuration;
        this.environment = environment;
    }

    public HealthLogTail ReadLatestLines(int lineCount)
    {
        var logFilePath = GetTodayLogFilePath();
        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            return new HealthLogTail
            {
                FilePath = string.Empty,
                Lines = [],
                Status = SystemHealthStatus.Unhealthy,
                Message = "NLog:BasePath 未設定，無法定位日誌檔案。"
            };
        }

        if (!File.Exists(logFilePath))
        {
            return new HealthLogTail
            {
                FilePath = logFilePath,
                Lines = [],
                Status = SystemHealthStatus.Degraded,
                Message = "今日日誌檔案尚未建立。"
            };
        }

        try
        {
            var lines = ReadTail(logFilePath, lineCount);
            return new HealthLogTail
            {
                FilePath = logFilePath,
                Lines = lines,
                Status = SystemHealthStatus.Healthy,
                Message = $"已讀取最後 {lines.Count} 筆日誌。"
            };
        }
        catch (Exception ex)
        {
            return new HealthLogTail
            {
                FilePath = logFilePath,
                Lines = [],
                Status = SystemHealthStatus.Unhealthy,
                Message = $"讀取日誌失敗：{ex.GetType().Name}。"
            };
        }
    }

    public string GetTodayLogFilePath()
    {
        var nlogBasePrefixPath = configuration.GetValue<string>("NLog:BasePath");
        if (string.IsNullOrWhiteSpace(nlogBasePrefixPath))
        {
            return string.Empty;
        }

        var baseNamespace = typeof(Program).Namespace ?? environment.ApplicationName;
        var nlogBasePath = Path.Combine(nlogBasePrefixPath, baseNamespace);
        return Path.Combine(nlogBasePath, $"{baseNamespace}-logfile-{DateTime.Today:yyyy-MM-dd}.log");
    }

    private static IReadOnlyList<string> ReadTail(string filePath, int lineCount)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var queue = new Queue<string>();

        while (reader.ReadLine() is { } line)
        {
            queue.Enqueue(line);
            while (queue.Count > lineCount)
            {
                queue.Dequeue();
            }
        }

        return queue.ToList();
    }
}
