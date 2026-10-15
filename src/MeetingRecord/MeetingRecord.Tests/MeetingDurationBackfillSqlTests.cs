using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MeetingRecord.AccessDatas;
using MeetingRecord.AccessDatas.Migrations.Sql;
using MeetingRecord.AccessDatas.Models;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Tests;

/// <summary>
/// 1.0.29 migration 的會議時長回填：與改版前儀表板的算法一致——只算轉錄完成的會議、
/// 只算最後一輪、只算成功的轉錄。（原本守在 <c>DashboardMetrics.SumLatestRunAudioSeconds</c> 的測試搬到這裡。）
/// </summary>
public sealed class MeetingDurationBackfillSqlTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly BackendDBContext context;

    public MeetingDurationBackfillSqlTests()
    {
        connection.Open();
        context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Backfill_ShouldSumOnlyTheLatestSuccessfulRun()
    {
        var latestStart = new DateTime(2026, 9, 20, 10, 0, 0);
        var meeting = await AddMeetingAsync(TranscriptionStatus.Completed, latestStart);

        await AddUsageAsync(meeting, latestStart.AddDays(-1), 1800);                          // 上一輪：重轉錄前寫下的
        await AddUsageAsync(meeting, latestStart, 1800);                                      // 剛好在開始時間也算（>=）
        await AddUsageAsync(meeting, latestStart.AddMinutes(3), 600);
        await AddUsageAsync(meeting, latestStart.AddMinutes(4), 900, AiUsageOutcome.Failed);  // 失敗不算

        await context.Database.ExecuteSqlRawAsync(MeetingDurationBackfill.Sql);

        Assert.Equal(2400d, await DurationOfAsync(meeting));
    }

    [Fact]
    public async Task Backfill_ShouldSkipUnfinishedOrUnmeasuredMeetings()
    {
        var start = new DateTime(2026, 9, 20, 10, 0, 0);
        var processing = await AddMeetingAsync(TranscriptionStatus.Processing, start);
        var noUsage = await AddMeetingAsync(TranscriptionStatus.Completed, start);
        await AddUsageAsync(processing, start.AddMinutes(1), 600);

        await context.Database.ExecuteSqlRawAsync(MeetingDurationBackfill.Sql);

        Assert.Null(await DurationOfAsync(processing));
        Assert.Null(await DurationOfAsync(noUsage));
    }

    private async Task<int> AddMeetingAsync(TranscriptionStatus status, DateTime startedAt)
    {
        var meeting = new Meeting { Title = "會議", TranscriptionStatus = status, TranscriptionStartedAt = startedAt };
        context.Meeting.Add(meeting);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return meeting.Id;
    }

    private async Task AddUsageAsync(int meetingId, DateTime occurredAt, double seconds, AiUsageOutcome outcome = AiUsageOutcome.Succeeded)
    {
        context.AiUsageLog.Add(new AiUsageLog
        {
            OccurredAt = occurredAt,
            Feature = AiUsageFeature.Transcription,
            Outcome = outcome,
            Provider = "AzureOpenAI",
            Model = "gpt-transcribe",
            AudioSeconds = seconds,
            MeetingId = meetingId,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private Task<double?> DurationOfAsync(int meetingId)
        => context.Meeting.AsNoTracking().Where(x => x.Id == meetingId).Select(x => x.MediaDurationSeconds).SingleAsync();
}
