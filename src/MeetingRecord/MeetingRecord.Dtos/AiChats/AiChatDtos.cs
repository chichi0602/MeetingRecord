using System.Text.Json.Serialization;

namespace MeetingRecord.Dtos.AiChats;

/// <summary>一段 AI 問答對話（清單用，1.1.21）。</summary>
public class AiChatConversationDto
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public int MessageCount { get; set; }
}

/// <summary>提問附上的一個檔案。</summary>
public class AiChatAttachmentDto
{
    public string FileName { get; set; } = string.Empty;

    /// <summary>磁碟上的檔名（只在對話資料夾內有意義）；讀附件時用它指定。</summary>
    public string StoredName { get; set; } = string.Empty;

    /// <summary><c>Image</c> 或 <c>Document</c>。</summary>
    public string Kind { get; set; } = "Document";

    public long Size { get; set; }

    [JsonIgnore]
    public bool IsImage => Kind == "Image";
}

/// <summary>一則對話訊息。</summary>
public class AiChatMessageDto
{
    /// <summary><c>user</c> 或 <c>assistant</c>。</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string? AskedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<AiChatAttachmentDto> Attachments { get; set; } = [];

    [JsonIgnore]
    public bool IsUser => Role == "user";
}

/// <summary>一次提問的結果：回答本身，以及這次實際讀到了什麼。</summary>
public class AiChatAnswerDto
{
    public string Answer { get; set; } = string.Empty;

    public List<string> UsedLabels { get; set; } = [];

    public List<string> TruncatedLabels { get; set; } = [];

    public List<string> SkippedLabels { get; set; } = [];
}

/// <summary>
/// 提問與重新產生的串流（每行一筆 JSON，<c>application/x-ndjson</c>）：先是零到多筆 <c>delta</c>（新產生的文字），
/// 最後一筆是 <c>done</c>（附完整結果）或 <c>error</c>（附原因）。
/// </summary>
public class AiChatStreamEventDto
{
    public const string Delta = "delta";
    public const string Done = "done";
    public const string Error = "error";

    public string Type { get; set; } = Delta;

    public string? Text { get; set; }

    public AiChatAnswerDto? Answer { get; set; }

    public string? Message { get; set; }
}

/// <summary>修改類動作的結果（1.1.22，Server 與 WASM 兩種資料來源共用）。</summary>
public enum AiChatUpdateOutcome
{
    Updated,

    /// <summary>整段對話已經不存在（被刪除或清空）。</summary>
    NotFound,

    /// <summary>原本的內容對不上（多半是別人先改過）。</summary>
    Conflict,
}

/// <summary>畫面剛選、還沒送出的附件（內容整份在記憶體裡，上限見 <c>AiChatAttachmentPolicy</c>）。</summary>
public sealed record AiChatPendingFile(string FileName, byte[] Content);

/// <summary>讀出來的檔案（附件或匯出的 PDF）。</summary>
public sealed record AiChatFileDto(string FileName, string ContentType, byte[] Content);

public class AiChatRenameRequestDto
{
    public string Title { get; set; } = string.Empty;
}

/// <summary>修改一則訊息：帶原本的角色與內容，伺服器比對沒被別人改過才寫入。</summary>
public class AiChatUpdateMessageRequestDto
{
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string NewContent { get; set; } = string.Empty;
}

/// <summary>改問題並重新產生回答：帶原本的問題，伺服器比對沒被別人改過才進行。</summary>
public class AiChatRegenerateRequestDto
{
    public string ExpectedQuestion { get; set; } = string.Empty;

    public string NewQuestion { get; set; } = string.Empty;
}

/// <summary>匯出 PDF：<see cref="MessageIndex"/> 有值＝只匯出那一則，沒值＝整段對話。內容一律由伺服器從對話檔讀，不收用戶端的文字。</summary>
public class AiChatExportRequestDto
{
    public int? MessageIndex { get; set; }
}
