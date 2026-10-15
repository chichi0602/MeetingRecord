using MeetingRecord.Share.Enums;

namespace MeetingRecord.Models.AdapterModel;

/// <summary>
/// AI 對話紀錄的畫面繫結模型（1.0.30）。本頁唯讀，因此不需要 ICloneable。
/// </summary>
public class AiCallLogAdapterModel
{
    public int Id { get; set; }

    public Guid CallId { get; set; }

    public DateTime OccurredAt { get; set; }

    public AiUsageFeature Feature { get; set; }

    public AiUsageOutcome Outcome { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int? UserId { get; set; }

    public string? UserName { get; set; }

    public int? MeetingId { get; set; }

    public int? ProjectId { get; set; }

    public string? RelatedInfo { get; set; }

    public string? ThreadKey { get; set; }

    public int? HttpStatus { get; set; }

    public long ElapsedMilliseconds { get; set; }

    public int RequestCharacters { get; set; }

    public int ResponseCharacters { get; set; }

    public bool IsResponsePartial { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ContentFile { get; set; }

    /// <summary>功能的中文名稱。</summary>
    public string FeatureText => AiUsageFeatureText.Describe(Feature);

    /// <summary>結果的中文名稱；回應不完整時加註。</summary>
    public string OutcomeText => Outcome switch
    {
        AiUsageOutcome.Succeeded => "成功",
        AiUsageOutcome.Cancelled => IsResponsePartial ? "已取消（部分回應）" : "已取消",
        _ => IsResponsePartial ? "失敗（部分回應）" : "失敗",
    };

    /// <summary>耗時：一秒內顯示毫秒，否則顯示秒。</summary>
    public string ElapsedText => ElapsedMilliseconds < 1000
        ? $"{ElapsedMilliseconds} ms"
        : $"{ElapsedMilliseconds / 1000d:0.0} 秒";

    /// <summary>對象：會議或專案編號。</summary>
    public string TargetText => MeetingId is { } meetingId
        ? $"會議 #{meetingId}"
        : ProjectId is { } projectId
            ? $"專案 #{projectId}"
            : "—";
}
