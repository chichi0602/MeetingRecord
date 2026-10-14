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
}
