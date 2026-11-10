using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.DataAccess;
using MeetingRecord.Business.Services.Export;
using MeetingRecord.Business.Services.Other;
using MeetingRecord.Dtos.AiChats;
using MeetingRecord.Share.Enums;
using MeetingRecord.Share.Helpers;

namespace MeetingRecord.Web.AiChat;

/// <summary>
/// AI 問答在 Web 這一層的共用操作（1.1.22）：<c>AiChatController</c>（WASM 頁）與 <see cref="ServerAiChatSource"/>（Blazor Server 頁）
/// 都用這一份——匯出 PDF、讀附件（下載才寫稽核）、型別轉換。規則仍在 <see cref="AiChatService"/>。
/// </summary>
public sealed class AiChatOperations(
    AiChatService chatService,
    ProjectService projectService,
    MeetingService meetingService,
    IAuditLogService auditLogService,
    IPdfRenderer pdfRenderer)
{
    private const string PdfContentType = "application/pdf";

    /// <summary>
    /// 匯出 PDF：<paramref name="messageIndex"/> 有值＝只匯出那一則，沒值＝整段。內容一律從對話檔讀；找不到要匯出的內容回 null。
    /// </summary>
    public async Task<AiChatFileDto?> ExportPdfAsync(AiChatScope scope, int targetId, string conversationId, int? messageIndex, string via, CancellationToken cancellationToken = default)
    {
        var history = await chatService.GetHistoryAsync(scope, targetId, conversationId, cancellationToken);
        if (history.Count == 0 || messageIndex is { } i && (i < 0 || i >= history.Count))
        {
            return null;
        }

        var conversationTitle = (await chatService.ListConversationsAsync(scope, targetId, cancellationToken))
            .FirstOrDefault(x => x.Id == conversationId)?.Title;
        var targetName = scope == AiChatScope.Project
            ? (await projectService.GetAsync(targetId)).Title
            : (await meetingService.GetAsync(targetId)).Title;
        var exportedAt = DateTime.Now;
        string html, fileName, kind;
        if (messageIndex is { } index)
        {
            html = AiChatDocumentExporter.BuildMessageHtml(targetName, history[index], index + 1, exportedAt);
            fileName = AiChatDocumentExporter.BuildMessageFileName(targetName, index + 1, exportedAt, conversationTitle);
            kind = "Message";
        }
        else
        {
            html = AiChatDocumentExporter.BuildConversationHtml(targetName, history, exportedAt, conversationTitle);
            fileName = AiChatDocumentExporter.BuildConversationFileName(targetName, exportedAt, conversationTitle);
            kind = "Conversation";
        }

        var pdf = await pdfRenderer.RenderAsync(html, cancellationToken);
        await auditLogService.WriteForCurrentUserAsync(
            AuditActions.AiChatExportPdf, targetType: scope.ToString(), targetId: targetId.ToString(),
            detail: $"conversationId={conversationId}; kind={kind}; file={fileName}; bytes={pdf.Length}; via={via}");
        return new AiChatFileDto(fileName, PdfContentType, pdf);
    }

    /// <summary>
    /// 讀提問附上的檔案：只認這段對話裡真的有的附件（用磁碟檔名找）。<paramref name="download"/>（文件下載）寫稽核；圖片預覽不寫。
    /// 找不到回 null。
    /// </summary>
    public async Task<AiChatFileDto?> ReadAttachmentAsync(AiChatScope scope, int targetId, string conversationId, string storedName, bool download, string via, CancellationToken cancellationToken = default)
    {
        var history = await chatService.GetHistoryAsync(scope, targetId, conversationId, cancellationToken);
        var attachment = history.SelectMany(x => x.AttachmentList).FirstOrDefault(x => x.StoredName == storedName);
        var content = attachment is null ? null : await chatService.ReadAttachmentAsync(scope, targetId, conversationId, attachment, cancellationToken);
        if (attachment is null || content is null)
        {
            return null;
        }

        if (download)
        {
            await auditLogService.WriteForCurrentUserAsync(
                AuditActions.AiChatAttachmentDownload, targetType: scope.ToString(), targetId: targetId.ToString(),
                detail: $"conversationId={conversationId}; file={attachment.FileName}; size={content.Length}; via={via}");
        }

        var contentType = attachment.Kind == AiChatAttachmentKind.Image
            ? AiChatAttachmentPolicy.GetImageMediaType(attachment.FileName) ?? "application/octet-stream"
            : "application/octet-stream";
        return new AiChatFileDto(attachment.FileName, contentType, content);
    }

    public static AiChatConversationDto ToDto(AiChatConversationInfo x) => new()
    {
        Id = x.Id,
        Title = x.Title,
        CreatedBy = x.CreatedBy,
        CreatedAt = x.CreatedAt,
        UpdatedAt = x.UpdatedAt,
        MessageCount = x.MessageCount,
    };

    public static AiChatMessageDto ToDto(AiChatMessageItem x) => new()
    {
        Role = x.Role,
        Content = x.Content,
        AskedBy = x.AskedBy,
        CreatedAt = x.CreatedAt,
        Attachments = [.. x.AttachmentList.Select(a => new AiChatAttachmentDto
        {
            FileName = a.FileName,
            StoredName = a.StoredName,
            Kind = a.Kind.ToString(),
            Size = a.Size,
        })],
    };

    public static AiChatAnswerDto ToDto(AiChatAnswer x) => new()
    {
        Answer = x.Answer,
        UsedLabels = [.. x.UsedLabels],
        TruncatedLabels = [.. x.TruncatedLabels],
        SkippedLabels = [.. x.SkippedLabels],
    };

    public static AiChatUpdateOutcome ToDto(UpdateOutcome x) => x switch
    {
        UpdateOutcome.Updated => AiChatUpdateOutcome.Updated,
        UpdateOutcome.NotFound => AiChatUpdateOutcome.NotFound,
        _ => AiChatUpdateOutcome.Conflict,
    };
}
