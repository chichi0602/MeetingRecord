using System.Text.Json;
using MeetingRecord.Dtos.Diagnostics;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// 讀監控與診斷 AI 分析的串流（1.1.42 從 <see cref="ExceptionLogApi"/> 抽出，AI 例外分析與 AI 日誌分析共用）。
/// 每行一筆 JSON：delta…done／error。失敗丟 <see cref="InvalidOperationException"/>（訊息是伺服器給的原文），
/// 呼叫端取消（關掉視窗）丟 <see cref="OperationCanceledException"/>——中斷請求，伺服器跟著停止呼叫 AI。
/// </summary>
public static class DiagnosticAiStream
{
    /// <summary>送出請求、逐段回報新文字（<paramref name="onDelta"/>），回完成事件（結果、模型、說明行）。</summary>
    public static async Task<DiagnosticAiStreamEventDto> ReadAsync(
        ApiClient api,
        HttpRequestMessage request,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        using var ownedRequest = request;
        using var response = await api.SendStreamingAsync(request, cancellationToken);
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

            var item = JsonSerializer.Deserialize(line, ClientJsonContext.Default.DiagnosticAiStreamEventDto);
            switch (item?.Type)
            {
                case DiagnosticAiStreamEventDto.Delta when item.Text is not null:
                    onDelta(item.Text);
                    break;
                case DiagnosticAiStreamEventDto.Done:
                    return item;
                case DiagnosticAiStreamEventDto.Error:
                    throw new InvalidOperationException(item.Message ?? "AI 分析未完成，請稍後再試。");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("連線中斷，沒有收到完整的分析結果，請重新整理後再試。");
    }
}
