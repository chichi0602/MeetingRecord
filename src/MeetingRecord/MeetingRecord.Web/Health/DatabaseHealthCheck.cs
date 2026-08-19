using Microsoft.Extensions.Diagnostics.HealthChecks;
using MeetingRecord.AccessDatas;

namespace MeetingRecord.Web.Health;

public class DatabaseHealthCheck : IHealthCheck
{
    private readonly BackendDBContext context;

    public DatabaseHealthCheck(BackendDBContext context)
    {
        this.context = context;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext healthCheckContext,
        CancellationToken cancellationToken = default)
    {
        return await context.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Database connection is available.")
            : HealthCheckResult.Unhealthy("Database connection is unavailable.");
    }
}
