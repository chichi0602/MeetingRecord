using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Dtos.AiChats;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Web.AiChat;

/// <summary>
/// Blazor Server 頁的 AI 問答資料來源（1.1.22，原本寫在 <c>AiChatModal</c> 裡）：直接呼叫 <see cref="AiChatService"/>，
/// 串流用服務層的 <c>onDelta</c>。動作權限由宿主頁傳給視窗（<c>CanEdit</c> 等），資料範圍由服務層檢查。
/// </summary>
public sealed class ServerAiChatSource(AiChatService chatService, AiChatOperations operations) : IAiChatSource
{
    public async Task<IReadOnlyList<AiChatConversationDto>> ListConversationsAsync(AiChatScope scope, int targetId)
        => [.. (await chatService.ListConversationsAsync(scope, targetId)).Select(AiChatOperations.ToDto)];

    public Task<string> CreateConversationAsync(AiChatScope scope, int targetId, bool automatic)
        => chatService.CreateConversationAsync(scope, targetId, audit: !automatic);

    public async Task<AiChatUpdateOutcome> RenameConversationAsync(AiChatScope scope, int targetId, string conversationId, string title)
        => AiChatOperations.ToDto(await chatService.RenameConversationAsync(scope, targetId, conversationId, title));

    public Task DeleteConversationAsync(AiChatScope scope, int targetId, string conversationId)
        => chatService.ClearHistoryAsync(scope, targetId, conversationId);

    public async Task<IReadOnlyList<AiChatMessageDto>> GetHistoryAsync(AiChatScope scope, int targetId, string conversationId)
        => [.. (await chatService.GetHistoryAsync(scope, targetId, conversationId)).Select(AiChatOperations.ToDto)];

    public async Task<AiChatAnswerDto> AskAsync(AiChatScope scope, int targetId, string conversationId, string question, IReadOnlyList<AiChatPendingFile> files, Action<string> onDelta)
        => AiChatOperations.ToDto(await chatService.AskAsync(
            scope, targetId, conversationId, question, onDelta,
            attachments: [.. files.Select(x => new PendingAttachment(x.FileName, x.Content))]));

    public async Task<AiChatUpdateOutcome> UpdateMessageAsync(AiChatScope scope, int targetId, string conversationId, int index, AiChatMessageDto original, string newContent)
        => AiChatOperations.ToDto(await chatService.UpdateMessageAsync(
            scope, targetId, conversationId, index, new AiChatMessageItem(original.Role, original.Content, original.AskedBy, original.CreatedAt), newContent));

    public async Task<AiChatAnswerDto> RegenerateAsync(AiChatScope scope, int targetId, string conversationId, int questionIndex, string expectedQuestion, string newQuestion, Action<string> onDelta)
        => AiChatOperations.ToDto(await chatService.RegenerateAsync(scope, targetId, conversationId, questionIndex, expectedQuestion, newQuestion, onDelta));

    public Task<AiChatFileDto?> ReadAttachmentAsync(AiChatScope scope, int targetId, string conversationId, string storedName, bool download)
        => operations.ReadAttachmentAsync(scope, targetId, conversationId, storedName, download, AuditVia.Web);

    public Task<AiChatFileDto?> ExportPdfAsync(AiChatScope scope, int targetId, string conversationId, int? messageIndex)
        => operations.ExportPdfAsync(scope, targetId, conversationId, messageIndex, AuditVia.Web);
}
