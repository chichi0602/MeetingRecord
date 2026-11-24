using System.Net.Http.Json;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;

namespace MeetingRecord.Web.Client.Api;

/// <summary>監控與診斷：<c>api/v1/monitoring</c>，只限管理者。</summary>
public sealed class MonitoringApi(ApiClient api)
{
    private const string Base = "api/v1/monitoring";

    /// <summary>資料庫用量（1.1.32）。</summary>
    public Task<ApiResult<DatabaseUsageReport>> GetDatabaseUsageAsync()
        => api.GetAsync($"{Base}/database-usage", ClientJsonContext.Default.DatabaseUsageResult);

    /// <summary>檔案儲存用量（1.1.32）：影音目錄可能很大，和資料庫用量分開讀。</summary>
    public Task<ApiResult<StorageUsageReport>> GetStorageUsageAsync()
        => api.GetAsync($"{Base}/storage-usage", ClientJsonContext.Default.StorageUsageResult);

    /// <summary>日誌等級現況（1.1.33）。</summary>
    public Task<ApiResult<LogLevelStateDto>> GetLogLevelAsync()
        => api.GetAsync($"{Base}/log-level", ClientJsonContext.Default.LogLevelStateResult);

    /// <summary>套用日誌等級（1.1.33）；稽核由伺服器寫。</summary>
    public Task<ApiResult<LogLevelStateDto>> ApplyLogLevelAsync(LogLevelRank level)
        => api.PutAsync($"{Base}/log-level", new LogLevelApplyRequestDto { Level = level },
            ClientJsonContext.Default.LogLevelApplyRequestDto, ClientJsonContext.Default.LogLevelStateResult);

    /// <summary>還原為系統預設等級（1.1.33）；稽核由伺服器寫。</summary>
    public Task<ApiResult<LogLevelStateDto>> RestoreLogLevelAsync()
        => api.PostAsync($"{Base}/log-level/restore", ClientJsonContext.Default.LogLevelStateResult);

    /// <summary>系統健康：跑一輪全部檢查（1.1.35）。</summary>
    public Task<ApiResult<SystemHealthReport>> GetHealthAsync()
        => api.GetAsync($"{Base}/health", ClientJsonContext.Default.SystemHealthResult);

    /// <summary>系統健康：最近 24 小時警告以上日誌（1.1.35），時間正序。</summary>
    public Task<ApiResult<LogQueryResult>> GetRecentWarningsAsync()
        => api.GetAsync($"{Base}/logs/recent-warnings", ClientJsonContext.Default.LogQueryResultResult);

    /// <summary>日誌檢視頁的設定（1.1.42）：AI 日誌分析能不能用。</summary>
    public Task<ApiResult<LogViewerMetaDto>> GetLogViewerMetaAsync()
        => api.GetAsync($"{Base}/logs/meta", ClientJsonContext.Default.LogViewerMetaResult);

    /// <summary>日誌檢視查詢（1.1.42），時間正序。</summary>
    public Task<ApiResult<LogQueryResult>> QueryLogsAsync(LogQueryRequest request)
        => api.PostAsync($"{Base}/logs/query", request, ClientJsonContext.Default.LogQueryRequest, ClientJsonContext.Default.LogQueryResultResult);

    /// <summary>匯出 .log（1.1.42）：伺服器依同一組條件重查產生；稽核由伺服器寫。</summary>
    public Task<ApiDownload> ExportLogsAsync(LogQueryRequest request)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/logs/export")
        {
            Content = JsonContent.Create(request, ClientJsonContext.Default.LogQueryRequest),
        });

    /// <summary>AI 日誌分析（1.1.42，會產生費用）：伺服器依同一組條件重查、取最新 100 筆送出；回完成事件。</summary>
    public Task<DiagnosticAiStreamEventDto> AnalyzeLogsAsync(LogQueryRequest request, Action<string> onDelta, CancellationToken cancellationToken)
        => DiagnosticAiStream.ReadAsync(api, new HttpRequestMessage(HttpMethod.Post, $"{Base}/logs/ai-analysis")
        {
            Content = JsonContent.Create(request, ClientJsonContext.Default.LogQueryRequest),
        }, onDelta, cancellationToken);

    /// <summary>匯出 AI 日誌分析 PDF（1.1.42）：伺服器產生，附錄由伺服器重查重組；稽核由伺服器寫。</summary>
    public Task<ApiDownload> ExportLogAiPdfAsync(LogAiPdfRequestDto request)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/logs/ai-analysis/pdf")
        {
            Content = JsonContent.Create(request, ClientJsonContext.Default.LogAiPdfRequestDto),
        });

    /// <summary>轉錄比較頁的初始資料（1.1.72）。</summary>
    public Task<ApiResult<TranscriptionCompareMetaDto>> GetTranscriptionCompareMetaAsync()
        => api.GetAsync($"{Base}/transcription-compare/meta", ClientJsonContext.Default.TranscriptionCompareMetaResult);

    /// <summary>轉錄比較的預估費用（1.1.72）。</summary>
    public Task<ApiResult<TranscriptionCompareEstimateDto>> EstimateTranscriptionCompareAsync(TranscriptionCompareRequestDto request)
        => api.PostAsync($"{Base}/transcription-compare/estimate", request,
            ClientJsonContext.Default.TranscriptionCompareRequestDto, ClientJsonContext.Default.TranscriptionCompareEstimateResult);

    /// <summary>
    /// 轉錄比較（1.1.72，會產生費用）：逐筆回報事件（片段、每組結果）。失敗丟 <see cref="InvalidOperationException"/>（伺服器原文）；
    /// 取消＝中斷請求，伺服器還沒跑的組就不跑。
    /// </summary>
    public async Task RunTranscriptionCompareAsync(
        TranscriptionCompareRequestDto request,
        Action<TranscriptionCompareEventDto> onEvent,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Base}/transcription-compare")
        {
            Content = JsonContent.Create(request, ClientJsonContext.Default.TranscriptionCompareRequestDto),
        };
        using var response = await api.SendStreamingAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await api.ReadFailureAsync(response, cancellationToken);
            throw new InvalidOperationException(failure.ErrorMessage ?? failure.Message);
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var item = System.Text.Json.JsonSerializer.Deserialize(line, ClientJsonContext.Default.TranscriptionCompareEventDto);
            switch (item?.Type)
            {
                case TranscriptionCompareEventDto.Done:
                    return;
                case TranscriptionCompareEventDto.Error:
                    throw new InvalidOperationException(item.Message ?? "轉錄比較未完成，請稍後再試。");
                case not null:
                    onEvent(item);
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("連線中斷，沒有收到完整的比較結果，請重新整理後再試。");
    }

    /// <summary>測試 AI 連線（1.1.35）：會產生費用，呼叫前畫面要先確認；稽核由伺服器寫。</summary>
    public Task<ApiResult<List<AiConnectionProbeResult>>> TestAiConnectionAsync()
        => api.PostAsync($"{Base}/ai-connection-test", ClientJsonContext.Default.AiConnectionProbeResults);
}
