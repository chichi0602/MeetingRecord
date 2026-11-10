using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Business.Services.AiChat;

/// <summary>
/// 已存進對話資料夾的一個附件。
/// </summary>
/// <param name="FileName">使用者看到的原始檔名。</param>
/// <param name="StoredName">磁碟上的檔名（隨機產生，只在對話資料夾內有意義）。</param>
/// <param name="Kind">圖片或文件。</param>
/// <param name="Size">位元組數。</param>
public sealed record AiChatAttachment(string FileName, string StoredName, AiChatAttachmentKind Kind, long Size);

/// <summary>畫面剛收到、還沒存檔的附件。內容整份在記憶體裡（上限見 <see cref="AiChatAttachmentPolicy"/>）。</summary>
public sealed record PendingAttachment(string FileName, byte[] Content);
