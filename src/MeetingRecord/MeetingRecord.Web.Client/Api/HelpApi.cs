using System.Net.Http.Json;
using System.Text.Json;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Dtos.Helps;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// WASM 使用說明頁的資料來源（1.1.30）：<c>api/v1/help</c>（1.1.29）。
/// 提問讀串流（每行一筆 JSON：delta…done／error）；失敗丟 <see cref="InvalidOperationException"/>（訊息是伺服器給的原文），
/// 使用者按「停止」（<paramref name="cancellationToken"/> 取消）丟 <see cref="OperationCanceledException"/>——中斷請求，伺服器跟著停止呼叫 AI。
/// </summary>
public sealed class HelpApi(ApiClient api)
{
    private const string Base = "api/v1/help";

    public Task<ApiResult<HelpManualDto>> GetManualAsync()
        => api.GetAsync($"{Base}/manual", ClientJsonContext.Default.HelpManualResult);

    /// <summary>提問；<paramref name="onDelta"/> 逐段回報新文字。回完整回答與模型名稱。</summary>
    public async Task<(string Answer, string? Model)> AskAsync(
        string question,
        IReadOnlyList<HelpAssistantTurnDto> history,
        string threadKey,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/assistant/ask")
        {
            Content = JsonContent.Create(
                new HelpAssistantAskRequestDto { Question = question, History = [.. history], ThreadKey = threadKey },
                ClientJsonContext.Default.HelpAssistantAskRequestDto),
        };

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

            var item = JsonSerializer.Deserialize(line, ClientJsonContext.Default.HelpAssistantStreamEventDto);
            switch (item?.Type)
            {
                case HelpAssistantStreamEventDto.Delta when item.Text is not null:
                    onDelta(item.Text);
                    break;
                case HelpAssistantStreamEventDto.Done:
                    return (item.Answer ?? string.Empty, item.Model);
                case HelpAssistantStreamEventDto.Error:
                    throw new InvalidOperationException(item.Message ?? "AI 助理沒有回答成功，請稍後再試。");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("連線中斷，沒有收到完整的回答，請重新整理後再試。");
    }
}
