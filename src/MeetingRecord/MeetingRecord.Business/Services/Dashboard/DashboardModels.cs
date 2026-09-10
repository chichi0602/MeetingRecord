namespace MeetingRecord.Business.Services.Dashboard;

/// <summary>圓餅圖的一個切片，或長條圖的一列。</summary>
/// <param name="Label">顯示名稱。</param>
/// <param name="Value">數量。</param>
/// <param name="Tone">配色語意，讓「失敗」之類的項目在各張圖裡顏色一致。</param>
public sealed record ChartSlice(string Label, int Value, ChartTone Tone = ChartTone.Neutral);

/// <summary>切片的配色語意。實際色碼由圖表元件對應到燕麥奶茶色票。</summary>
public enum ChartTone
{
    /// <summary>一般項目，依序取色階。</summary>
    Neutral = 0,

    /// <summary>完成／成功。</summary>
    Success = 1,

    /// <summary>進行中／等待。</summary>
    Warning = 2,

    /// <summary>失敗。</summary>
    Danger = 3,
}

/// <summary>折線圖上的一天。</summary>
/// <param name="Label">X 軸標籤。</param>
/// <param name="Created">當天新增的會議數。</param>
/// <param name="Completed">當天完成會議紀錄的數量。</param>
public sealed record TrendPoint(string Label, int Created, int Completed);

/// <summary>一張數字卡。</summary>
/// <param name="Title">卡片標題。</param>
/// <param name="Value">主要數字（已格式化）。</param>
/// <param name="Caption">副標，用來補充「其中 N 筆…」這種脈絡。</param>
/// <param name="Tone">副標的配色語意（例如逾期用 Danger）。</param>
public sealed record StatCardItem(string Title, string Value, string Caption, ChartTone Tone = ChartTone.Neutral);

/// <summary>效能指標區塊。</summary>
/// <param name="AverageTranscriptionDuration">平均轉錄耗時（沒有完整區間時為「—」）。</param>
/// <param name="AverageDraftDuration">平均會議紀錄生成耗時。</param>
/// <param name="TranscriptionFailureRate">轉錄失敗率（0～100）；還沒有任何成敗紀錄時為 null。</param>
/// <param name="UnassignedTranscriptCount">已轉錄完成但尚未歸屬專案的逐字稿數。</param>
public sealed record PerformanceSummary(
    string AverageTranscriptionDuration,
    string AverageDraftDuration,
    double? TranscriptionFailureRate,
    int UnassignedTranscriptCount);

/// <summary>儀表板一次載入所需的全部資料。</summary>
public sealed record DashboardSummary(
    IReadOnlyList<StatCardItem> Cards,
    IReadOnlyList<ChartSlice> ProjectStatus,
    IReadOnlyList<ChartSlice> TranscriptionStatus,
    IReadOnlyList<ChartSlice> DraftStatus,
    IReadOnlyList<ChartSlice> MeetingsPerProject,
    IReadOnlyList<ChartSlice> PromptTemplateUsage,
    IReadOnlyList<TrendPoint> Trend,
    PerformanceSummary Performance);
