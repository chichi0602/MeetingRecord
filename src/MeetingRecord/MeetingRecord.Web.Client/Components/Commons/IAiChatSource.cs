using MeetingRecord.Dtos.AiChats;
using MeetingRecord.Share.Enums;

namespace MeetingRecord.Web.Components.Commons;

/// <summary>
/// AI 問答視窗的資料來源（1.1.22 起，比照通知鈴的 <see cref="INotificationSource"/>）。
/// 實作是 <c>AiChatApi</c>：打 <c>api/v1/ai-chat</c>，提問與重新產生讀串流。
/// 失敗一律丟 <see cref="InvalidOperationException"/>，訊息是給使用者看的原文（視窗顯示「…失敗：原因」）。
/// </summary>
public interface IAiChatSource
{
    Task<IReadOnlyList<AiChatConversationDto>> ListConversationsAsync(AiChatScope scope, int targetId);

    /// <summary><paramref name="automatic"/>：開視窗時自動建的第一段對話，不算使用者的動作、不寫稽核。</summary>
    Task<string> CreateConversationAsync(AiChatScope scope, int targetId, bool automatic);

    Task<AiChatUpdateOutcome> RenameConversationAsync(AiChatScope scope, int targetId, string conversationId, string title);

    Task DeleteConversationAsync(AiChatScope scope, int targetId, string conversationId);

    Task<IReadOnlyList<AiChatMessageDto>> GetHistoryAsync(AiChatScope scope, int targetId, string conversationId);

    /// <summary>提問。回答邊產生邊經 <paramref name="onDelta"/> 回報（可能在背景執行緒）。</summary>
    Task<AiChatAnswerDto> AskAsync(AiChatScope scope, int targetId, string conversationId, string question, IReadOnlyList<AiChatPendingFile> files, Action<string> onDelta);

    Task<AiChatUpdateOutcome> UpdateMessageAsync(AiChatScope scope, int targetId, string conversationId, int index, AiChatMessageDto original, string newContent);

    Task<AiChatAnswerDto> RegenerateAsync(AiChatScope scope, int targetId, string conversationId, int questionIndex, string expectedQuestion, string newQuestion, Action<string> onDelta);

    /// <summary>讀附件；<paramref name="download"/>（文件下載）會寫稽核。找不到回 null。</summary>
    Task<AiChatFileDto?> ReadAttachmentAsync(AiChatScope scope, int targetId, string conversationId, string storedName, bool download);

    /// <summary>匯出 PDF（會寫稽核）。找不到要匯出的內容回 null。</summary>
    Task<AiChatFileDto?> ExportPdfAsync(AiChatScope scope, int targetId, string conversationId, int? messageIndex);
}
