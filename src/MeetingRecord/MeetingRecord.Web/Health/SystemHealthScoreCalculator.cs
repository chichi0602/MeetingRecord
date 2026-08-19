namespace MeetingRecord.Web.Health;

public static class SystemHealthScoreCalculator
{
    public static int CalculateScore(IEnumerable<SystemHealthItem> items)
    {
        var itemList = items.ToList();
        var totalWeight = itemList.Sum(item => item.Weight);
        if (totalWeight <= 0)
        {
            return 100;
        }

        var earned = itemList.Sum(item => item.Status switch
        {
            SystemHealthStatus.Healthy => item.Weight,
            SystemHealthStatus.Degraded => item.Weight * 0.5,
            _ => 0
        });

        return (int)Math.Round(earned / totalWeight * 100, MidpointRounding.AwayFromZero);
    }

    public static SystemHealthLight GetLight(int score)
    {
        if (score >= 90)
        {
            return SystemHealthLight.Green;
        }

        return score >= 70
            ? SystemHealthLight.Yellow
            : SystemHealthLight.Red;
    }

    public static SystemHealthLight GetLight(SystemHealthStatus status)
    {
        return status switch
        {
            SystemHealthStatus.Healthy => SystemHealthLight.Green,
            SystemHealthStatus.Degraded => SystemHealthLight.Yellow,
            _ => SystemHealthLight.Red
        };
    }

    public static SystemHealthStatus GetStatus(int score)
    {
        if (score >= 90)
        {
            return SystemHealthStatus.Healthy;
        }

        return score >= 70
            ? SystemHealthStatus.Degraded
            : SystemHealthStatus.Unhealthy;
    }
}
