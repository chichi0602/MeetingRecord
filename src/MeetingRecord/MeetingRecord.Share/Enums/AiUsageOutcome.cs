namespace MeetingRecord.Share.Enums;

/// <summary>
/// 一次 AI 呼叫的結果。以 int 儲存於資料庫。
///
/// <para>
/// ⚠️ 失敗與取消**一樣要記帳**：串流跑到一半斷掉時，已經生成的 token 供應商照算。
/// 只記成功的呼叫，帳本就會系統性地少報。
/// </para>
/// </summary>
public enum AiUsageOutcome
{
    Succeeded = 0,

    Failed = 1,

    /// <summary>使用者主動取消。刻意與 <see cref="Failed"/> 分開，理由同 <see cref="DraftStatus.Cancelled"/>。</summary>
    Cancelled = 2,
}

/// <summary>結果的顯示文字。</summary>
public static class AiUsageOutcomeText
{
    public static string Describe(AiUsageOutcome outcome) => outcome switch
    {
        AiUsageOutcome.Succeeded => "成功",
        AiUsageOutcome.Failed => "失敗",
        AiUsageOutcome.Cancelled => "已取消",
        _ => "未知",
    };
}
