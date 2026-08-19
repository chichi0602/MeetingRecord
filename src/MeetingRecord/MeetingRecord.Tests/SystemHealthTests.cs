using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using MeetingRecord.Web.Health;

namespace MeetingRecord.Tests;

public sealed class SystemHealthTests
{
    [Fact]
    public void CalculateScore_AllHealthy_ShouldReturnGreen100()
    {
        var items = new[]
        {
            CreateItem("A", 50, SystemHealthStatus.Healthy),
            CreateItem("B", 50, SystemHealthStatus.Healthy)
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        Assert.Equal(100, score);
        Assert.Equal(SystemHealthLight.Green, SystemHealthScoreCalculator.GetLight(score));
        Assert.Equal(SystemHealthStatus.Healthy, SystemHealthScoreCalculator.GetStatus(score));
    }

    [Fact]
    public void CalculateScore_DegradedRange_ShouldReturnYellow()
    {
        var items = new[]
        {
            CreateItem("A", 80, SystemHealthStatus.Healthy),
            CreateItem("B", 20, SystemHealthStatus.Unhealthy)
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        Assert.Equal(80, score);
        Assert.Equal(SystemHealthLight.Yellow, SystemHealthScoreCalculator.GetLight(score));
        Assert.Equal(SystemHealthStatus.Degraded, SystemHealthScoreCalculator.GetStatus(score));
    }

    [Fact]
    public void CalculateScore_UnhealthyRange_ShouldReturnRed()
    {
        var items = new[]
        {
            CreateItem("A", 60, SystemHealthStatus.Healthy),
            CreateItem("B", 40, SystemHealthStatus.Unhealthy)
        };

        var score = SystemHealthScoreCalculator.CalculateScore(items);

        Assert.Equal(60, score);
        Assert.Equal(SystemHealthLight.Red, SystemHealthScoreCalculator.GetLight(score));
        Assert.Equal(SystemHealthStatus.Unhealthy, SystemHealthScoreCalculator.GetStatus(score));
    }

    [Theory]
    [InlineData(SystemHealthStatus.Healthy, SystemHealthLight.Green)]
    [InlineData(SystemHealthStatus.Degraded, SystemHealthLight.Yellow)]
    [InlineData(SystemHealthStatus.Unhealthy, SystemHealthLight.Red)]
    public void GetLight_ItemStatus_ShouldMapTrafficLight(SystemHealthStatus status, SystemHealthLight expected)
    {
        Assert.Equal(expected, SystemHealthScoreCalculator.GetLight(status));
    }

    [Fact]
    public void HealthLogReader_ReadLatestLines_ShouldReturnLast100Lines()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordHealthTests", Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(rootPath, typeof(MeetingRecord.Web.Program).Namespace!);
        Directory.CreateDirectory(logDirectory);
        var logFile = Path.Combine(logDirectory, $"{typeof(MeetingRecord.Web.Program).Namespace}-logfile-{DateTime.Today:yyyy-MM-dd}.log");
        File.WriteAllLines(logFile, Enumerable.Range(1, 150).Select(index => $"line-{index}"));

        try
        {
            var reader = CreateReader(rootPath);

            var tail = reader.ReadLatestLines(100);

            Assert.Equal(SystemHealthStatus.Healthy, tail.Status);
            Assert.Equal(100, tail.Lines.Count);
            Assert.Equal("line-51", tail.Lines.First());
            Assert.Equal("line-150", tail.Lines.Last());
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void HealthLogReader_MissingFile_ShouldReturnDegraded()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordHealthTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var reader = CreateReader(rootPath);

            var tail = reader.ReadLatestLines(100);

            Assert.Equal(SystemHealthStatus.Degraded, tail.Status);
            Assert.Empty(tail.Lines);
            Assert.Contains("尚未建立", tail.Message);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static SystemHealthItem CreateItem(string name, int weight, SystemHealthStatus status)
    {
        return new SystemHealthItem
        {
            Name = name,
            Category = "Test",
            Weight = weight,
            Status = status,
            Light = SystemHealthScoreCalculator.GetLight(status),
            Evidence = "test"
        };
    }

    private static HealthLogReader CreateReader(string rootPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NLog:BasePath"] = rootPath
            })
            .Build();

        return new HealthLogReader(configuration, new TestWebHostEnvironment());
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MeetingRecord.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
