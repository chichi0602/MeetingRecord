using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeetingRecord.Dtos.AiChats;
using MeetingRecord.Dtos.Commons;
using MeetingRecord.Share.Enums;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.Client.Api;

/// <summary>
/// AI 問答視窗的資料來源（1.1.22）：<c>api/v1/ai-chat/{project|meeting}/{targetId}</c>。
/// 提問與重新產生讀串流（每行一筆 JSON：delta…done／error）。失敗一律丟 <see cref="InvalidOperationException"/>（訊息是伺服器給的原文）。
/// </summary>
public sealed class AiChatApi(ApiClient api) : IAiChatSource
{
    private static string Base(AiChatScope scope, int targetId) => $"api/v1/ai-chat/{(scope == AiChatScope.Project ? "project" : "meeting")}/{targetId}";

    public async Task<IReadOnlyList<AiChatConversationDto>> ListConversationsAsync(AiChatScope scope, int targetId)
        => Data(await api.GetAsync($"{Base(scope, targetId)}/conversations", ClientJsonContext.Default.AiChatConversationsResult)) ?? [];

    public async Task<string> CreateConversationAsync(AiChatScope scope, int targetId, bool automatic)
        => Data(await api.PostAsync($"{Base(scope, targetId)}/conversations?automatic={(automatic ? "true" : "false")}", ClientJsonContext.Default.StringResult)) ?? string.Empty;

    public async Task<AiChatUpdateOutcome> RenameConversationAsync(AiChatScope scope, int targetId, string conversationId, string title)
        => Outcome(await api.PutAsync($"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}/title", new AiChatRenameRequestDto { Title = title },
            ClientJsonContext.Default.AiChatRenameRequestDto, ClientJsonContext.Default.EmptyResult));

    public async Task DeleteConversationAsync(AiChatScope scope, int targetId, string conversationId)
        => Data(await api.DeleteAsync($"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}", ClientJsonContext.Default.EmptyResult));

    public async Task<IReadOnlyList<AiChatMessageDto>> GetHistoryAsync(AiChatScope scope, int targetId, string conversationId)
        => Data(await api.GetAsync($"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}/messages", ClientJsonContext.Default.AiChatMessagesResult)) ?? [];

    public Task<AiChatAnswerDto> AskAsync(AiChatScope scope, int targetId, string conversationId, string question, IReadOnlyList<AiChatPendingFile> files, Action<string> onDelta)
    {
        var form = new MultipartFormDataContent { { new StringContent(question), "question" } };
        foreach (var file in files)
        {
            var part = new ByteArrayContent(file.Content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", file.FileName);
        }

        return StreamAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}/ask") { Content = form }, onDelta);
    }

    public async Task<AiChatUpdateOutcome> UpdateMessageAsync(AiChatScope scope, int targetId, string conversationId, int index, AiChatMessageDto original, string newContent)
        => Outcome(await api.PutAsync($"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}/messages/{index}",
            new AiChatUpdateMessageRequestDto { Role = original.Role, Content = original.Content, NewContent = newContent },
            ClientJsonContext.Default.AiChatUpdateMessageRequestDto, ClientJsonContext.Default.EmptyResult));

    public Task<AiChatAnswerDto> RegenerateAsync(AiChatScope scope, int targetId, string conversationId, int questionIndex, string expectedQuestion, string newQuestion, Action<string> onDelta)
        => StreamAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}/messages/{questionIndex}/regenerate")
        {
            Content = JsonContent.Create(new AiChatRegenerateRequestDto { ExpectedQuestion = expectedQuestion, NewQuestion = newQuestion }, ClientJsonContext.Default.AiChatRegenerateRequestDto),
        }, onDelta);

    public async Task<AiChatFileDto?> ReadAttachmentAsync(AiChatScope scope, int targetId, string conversationId, string storedName, bool download)
        => File(await api.DownloadAsync(new HttpRequestMessage(HttpMethod.Get,
            $"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}/attachments/{Uri.EscapeDataString(storedName)}?download={(download ? "true" : "false")}")));

    public async Task<AiChatFileDto?> ExportPdfAsync(AiChatScope scope, int targetId, string conversationId, int? messageIndex)
        => File(await api.DownloadAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base(scope, targetId)}/conversations/{Uri.EscapeDataString(conversationId)}/pdf")
        {
            Content = JsonContent.Create(new AiChatExportRequestDto { MessageIndex = messageIndex }, ClientJsonContext.Default.AiChatExportRequestDto),
        }));

    /// <summary>讀串流：每行一筆 <see cref="AiChatStreamEventDto"/>。第一段文字前就失敗時伺服器回一般的錯誤（非 200）。</summary>
    private async Task<AiChatAnswerDto> StreamAsync(HttpRequestMessage request, Action<string> onDelta)
    {
        using (request)
        using (var response = await api.SendStreamingAsync(request))
        {
            if (!response.IsSuccessStatusCode)
            {
                var failure = await api.ReadFailureAsync(response);
                throw new InvalidOperationException(failure.ErrorMessage ?? failure.Message);
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var item = JsonSerializer.Deserialize(line, ClientJsonContext.Default.AiChatStreamEventDto);
                switch (item?.Type)
                {
                    case AiChatStreamEventDto.Delta when item.Text is not null:
                        onDelta(item.Text);
                        break;
                    case AiChatStreamEventDto.Done when item.Answer is not null:
                        return item.Answer;
                    case AiChatStreamEventDto.Error:
                        throw new InvalidOperationException(item.Message ?? "AI 問答發生錯誤，請稍後再試。");
                }
            }
        }

        throw new InvalidOperationException("連線中斷，沒有收到完整的回答，請重新整理後再試。");
    }

    private static T? Data<T>(ApiResult<T> result)
        => result.Success ? result.Data : throw new InvalidOperationException(result.ErrorMessage ?? result.Message);

    private static AiChatUpdateOutcome Outcome(ApiResult<object> result) => result.StatusCode switch
    {
        _ when result.Success => AiChatUpdateOutcome.Updated,
        404 => AiChatUpdateOutcome.NotFound,
        409 => AiChatUpdateOutcome.Conflict,
        _ => throw new InvalidOperationException(result.ErrorMessage ?? result.Message),
    };

    private static AiChatFileDto? File(ApiDownload download) => download switch
    {
        { Success: true } => new AiChatFileDto(download.FileName!, download.ContentType!, download.Content!),
        { StatusCode: 404 } => null,
        _ => throw new InvalidOperationException(download.Error ?? "下載失敗，請稍後再試。"),
    };
}
