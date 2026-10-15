namespace MeetingRecord.AccessDatas.Migrations.Sql;

/// <summary>
/// 1.0.29 migration <c>AddAiCallLogAndMeetingDuration</c> 的回填 SQL：從用量帳本算出既有會議的影音時長，
/// 寫進新欄位 <c>Meeting.MediaDurationSeconds</c>。抽成常數是為了讓 <c>MeetingDurationBackfillSqlTests</c> 直接驗證。
///
/// <para>
/// 規則與 1.0.29 之前儀表板的算法（<c>DashboardMetrics.SumLatestRunAudioSeconds</c>）相同：
/// 只看轉錄完成（3）的會議，加總<b>最後一輪</b>（<c>OccurredAt &gt;= TranscriptionStartedAt</c>）成功（Outcome 0）
/// 的轉錄（Feature 2）秒數——重轉錄會再寫一整輪，全部加總會把會議算成兩倍長。
/// 日期欄位在 SQLite 是 EF 寫入的固定格式文字，字串比較即時間先後。
/// </para>
/// </summary>
public static class MeetingDurationBackfill
{
    public const string Sql = """
        UPDATE "Meeting" SET "MediaDurationSeconds" = (
            SELECT SUM(l."AudioSeconds") FROM "AiUsageLog" AS l
            WHERE l."MeetingId" = "Meeting"."Id"
              AND l."Feature" = 2
              AND l."Outcome" = 0
              AND l."AudioSeconds" IS NOT NULL
              AND l."OccurredAt" >= "Meeting"."TranscriptionStartedAt")
        WHERE "TranscriptionStatus" = 3 AND "TranscriptionStartedAt" IS NOT NULL;
        """;
}
