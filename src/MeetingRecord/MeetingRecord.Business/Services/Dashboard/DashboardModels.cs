namespace MeetingRecord.Business.Services.Dashboard;

/// <summary>圓餅圖的一個切片，或長條圖的一列。</summary>
/// <param name="Label">顯示名稱。</param>
/// <param name="Value">
/// 數量。**同時決定幾何（長條寬度、圓餅角度）與預設顯示文字**。
///
/// <para>
/// 要畫的量不是整數時（例如金額），把它換算成整數的最小單位放這裡負責幾何，
/// 再用 <paramref name="Display"/> 給正確的文字——見下。
/// </para>
/// </param>
/// <param name="Tone">配色語意，讓「失敗」之類的項目在各張圖裡顏色一致。</param>
/// <param name="Display">
/// 覆寫顯示文字；null 時直接印 <paramref name="Value"/>。
///
/// <para>
/// 0.4.80 新增，位置在最後且有預設值，所以既有的所有建構呼叫**原樣編譯、行為不變**。
/// 存在的理由：<c>BarChart</c> 與 <c>PieChart</c> 是把 <c>Value</c> **原樣印出來**的，
/// 金額以「分」為單位塞進去會顯示成「1234567」。
/// </para>
/// </param>
public sealed record ChartSlice(
    string Label,
    int Value,
    ChartTone Tone = ChartTone.Neutral,
    string? Display = null)
{
    /// <summary>圖表上要印的文字。</summary>
    public string DisplayText => Display ?? Value.ToString();
}

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
/// <param name="UnassignedTranscriptCount">
/// 已轉錄完成、未歸屬專案<b>且尚未產生會議紀錄</b>的逐字稿數，也就是「還沒處理完」的待辦量。
/// 0.4.73 起未歸屬但已有會議紀錄是正常終態，不計入。
/// </param>
public sealed record PerformanceSummary(
    string AverageTranscriptionDuration,
    string AverageDraftDuration,
    double? TranscriptionFailureRate,
    int UnassignedTranscriptCount);

/// <summary>提示詞範本的數量概況。</summary>
/// <param name="TotalCount">全公司的範本總數（0.4.97 起儀表板不套團隊過濾，所有人看到同一個數字）。</param>
/// <param name="EnabledCount">啟用中的數量。</param>
/// <param name="DisabledCount">已停用的數量。停用是正常的管理動作，畫面用警示色而非錯誤色。</param>
/// <param name="UnusedEnabledCount">
/// 啟用中、但沒有任何會議紀錄用過的數量。
/// ⚠️ 比對的是 <c>Meeting.DraftPromptTemplateName</c> 這個「生成當下的名稱快照」而不是外鍵，
/// 所以<b>改過名的範本會被算成未使用</b>。
/// 詳見 <see cref="DashboardMetrics.CountUnusedEnabledTemplates"/>。
/// </param>
public sealed record PromptTemplateSummary(
    int TotalCount,
    int EnabledCount,
    int DisabledCount,
    int UnusedEnabledCount);

/// <summary>儲存空間細分。</summary>
/// <param name="MediaBytes">影音檔，取自 <c>Meeting.MediaFileSize</c>（資料庫欄位）。</param>
/// <param name="TranscriptBytes">
/// 逐字稿。⚠️ 逐字稿<b>沒有容量欄位</b>，這是實際掃目錄量出來的，
/// 因此可能大於資料庫認得的逐字稿總和（孤兒檔案也算）。
/// </param>
/// <param name="AttachmentBytes">專案附件，取自 <c>ProjectFile.FileSize</c>（資料庫欄位）。</param>
public sealed record StorageSummary(long MediaBytes, long TranscriptBytes, long AttachmentBytes)
{
    /// <summary>三項相加。數字卡的「儲存空間」直接顯示這個值，確保卡片與細分同源。</summary>
    public long TotalBytes => MediaBytes + TranscriptBytes + AttachmentBytes;
}

/// <summary>待辦概況（0.4.97）。</summary>
/// <param name="OverdueCount">未完成且到期日已過。</param>
/// <param name="DueSoonCount">未完成且 7 天內（含今天）到期。</param>
/// <param name="InProgressCount">狀態為「進行中」。</param>
/// <param name="FromMeetingRate">由會議擷取（有 MeetingId）的占比 0～100；沒有任何待辦時為 null。</param>
public sealed record TodoOverview(int OverdueCount, int DueSoonCount, int InProgressCount, double? FromMeetingRate);

/// <summary>專案概況（0.4.97）。</summary>
/// <param name="AverageInProgressCompletion">「進行中」專案的平均完成度 0～100；沒有進行中專案時為 null。</param>
/// <param name="OverdueCount">結束日已過、但尚未「已完成」。</param>
/// <param name="DueSoonCount">尚未「已完成」且 14 天內（含今天）到期。</param>
/// <param name="OnHoldCount">狀態為「暫緩」或「等待」。</param>
public sealed record ProjectOverview(double? AverageInProgressCompletion, int OverdueCount, int DueSoonCount, int OnHoldCount);

/// <summary>
/// 會議時數（0.4.97），取自 AI 用量帳本的轉錄音訊秒數——Meeting 本身沒有時長欄位。
/// ⚠️ 帳本 0.4.80 起才有，更早轉錄的會議沒有時長，只能計入 <see cref="TranscribedCount"/> 不能計入時數。
/// </summary>
/// <param name="TotalDuration">累計時數（已格式化）。</param>
/// <param name="ThisMonthDuration">本月建立的會議時數。</param>
/// <param name="AverageDuration">有時長紀錄的會議平均每場時數。</param>
/// <param name="MeasuredCount">有時長紀錄的會議數。</param>
/// <param name="TranscribedCount">轉錄完成的會議數。</param>
public sealed record MeetingHoursSummary(
    string TotalDuration,
    string ThisMonthDuration,
    string AverageDuration,
    int MeasuredCount,
    int TranscribedCount);

/// <summary>儀表板一次載入所需的全部資料。</summary>
public sealed record DashboardSummary(
    IReadOnlyList<StatCardItem> Cards,
    IReadOnlyList<ChartSlice> ProjectStatus,
    IReadOnlyList<ChartSlice> TranscriptionStatus,
    IReadOnlyList<ChartSlice> DraftStatus,
    IReadOnlyList<ChartSlice> MeetingsPerProject,
    IReadOnlyList<ChartSlice> PromptTemplateUsage,
    IReadOnlyList<ChartSlice> OpenTodoPriority,
    PerformanceSummary Performance,
    PromptTemplateSummary PromptTemplates,
    StorageSummary Storage,
    TodoOverview Todos,
    ProjectOverview Projects,
    MeetingHoursSummary MeetingHours);
