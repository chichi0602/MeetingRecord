using System.Net.Http.Json;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Diagnostics;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// 系統例外（1.1.41）：<c>api/v1/exception-logs</c>（1.1.40），只限管理者。匯出、刪除、清除、AI 分析與 PDF 的稽核由伺服器寫。
/// AI 分析讀串流（每行一筆 JSON：delta…done／error）；失敗丟 <see cref="InvalidOperationException"/>（訊息是伺服器給的原文），
/// 關掉視窗（<c>cancellationToken</c> 取消）丟 <see cref="OperationCanceledException"/>——中斷請求，伺服器跟著停止呼叫 AI。
/// </summary>
public sealed class ExceptionLogApi(ApiClient api)
{
    private const string Base = "api/v1/exception-logs";

    public Task<ApiResult<ExceptionLogMetaDto>> GetMetaAsync()
        => api.GetAsync($"{Base}/meta", ClientJsonContext.Default.ExceptionLogMetaResult);

    public Task<ApiResult<PagedResult<ExceptionLogAdapterModel>>> SearchAsync(ExceptionLogQuery query)
        => api.PostAsync($"{Base}/search", query, ClientJsonContext.Default.ExceptionLogQuery, ClientJsonContext.Default.ExceptionLogPageResult);

    public Task<ApiResult<ExceptionLogStackTraceDto>> GetStackTraceAsync(int id)
        => api.GetAsync($"{Base}/{id}/stack-trace", ClientJsonContext.Default.ExceptionLogStackTraceResult);

    public Task<ApiResult<Dictionary<int, string?>>> GetStackTracesAsync(IReadOnlyCollection<int> ids)
        => api.PostAsync($"{Base}/stack-traces", new ExceptionLogStackTracesRequestDto { Ids = [.. ids] },
            ClientJsonContext.Default.ExceptionLogStackTracesRequestDto, ClientJsonContext.Default.ExceptionLogStackTracesResult);

    public Task<ApiDownload> ExportCsvAsync(ExceptionLogQuery query)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/export/csv")
        {
            Content = JsonContent.Create(query, ClientJsonContext.Default.ExceptionLogQuery),
        });

    public Task<ApiResult<object>> DeleteAsync(int id)
        => api.DeleteAsync($"{Base}/{id}", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> PurgeAsync()
        => api.PostAsync($"{Base}/purge", ClientJsonContext.Default.EmptyResult);

    public Task<ApiResult<object>> ClearAllAsync()
        => api.PostAsync($"{Base}/clear-all", ClientJsonContext.Default.EmptyResult);

    /// <summary>AI 例外分析；<paramref name="onDelta"/> 逐段回報新文字。回完成事件（結果、模型、說明行）。</summary>
    public Task<DiagnosticAiStreamEventDto> AnalyzeAsync(int id, Action<string> onDelta, CancellationToken cancellationToken)
        => DiagnosticAiStream.ReadAsync(api, new HttpRequestMessage(HttpMethod.Post, $"{Base}/{id}/ai-analysis"), onDelta, cancellationToken);

    public Task<ApiDownload> ExportAiPdfAsync(int id, DiagnosticAiPdfRequestDto request)
        => api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/{id}/ai-analysis/pdf")
        {
            Content = JsonContent.Create(request, ClientJsonContext.Default.DiagnosticAiPdfRequestDto),
        });
}
