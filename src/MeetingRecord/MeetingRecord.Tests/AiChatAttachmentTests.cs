using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Business.Services.TextGeneration;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 問答附件（0.4.95）：格式規則、存檔與清除、送進模型的形狀。
/// </summary>
public sealed class AiChatAttachmentTests : IDisposable
{
    private const string Conv = "20260922153000000-abc";

    private readonly string rootPath;
    private readonly ILoggerFactory loggerFactory;
    private readonly AiChatStore store;

    public AiChatAttachmentTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        loggerFactory = LoggerFactory.Create(_ => { });

        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiChatPath = rootPath;
        store = new AiChatStore(Options.Create(settings), loggerFactory.CreateLogger<AiChatStore>());
    }

    // ───────── 規則 ─────────

    [Theory]
    [InlineData("截圖.PNG", AiChatAttachmentKind.Image)]
    [InlineData("photo.jpeg", AiChatAttachmentKind.Image)]
    [InlineData("a.webp", AiChatAttachmentKind.Image)]
    [InlineData("合約.pdf", AiChatAttachmentKind.Document)]
    [InlineData("notes.md", AiChatAttachmentKind.Document)]
    [InlineData("報表.docx", AiChatAttachmentKind.Document)]
    public void Classify_SupportedFormats(string fileName, AiChatAttachmentKind expected)
    {
        Assert.Equal(expected, AiChatAttachmentPolicy.Classify(fileName));
    }

    [Theory]
    [InlineData("簡報.pptx")]
    [InlineData("舊版.doc")]
    [InlineData("表格.xlsx")]
    [InlineData("程式.exe")]
    [InlineData("沒有副檔名")]
    [InlineData("")]
    public void Classify_UnsupportedFormats_ShouldBeNull(string fileName)
    {
        Assert.Null(AiChatAttachmentPolicy.Classify(fileName));
    }

    [Fact]
    public void Validate_ShouldEnforcePerKindSizeLimits()
    {
        Assert.Null(AiChatAttachmentPolicy.Validate("a.png", AiChatAttachmentPolicy.MaxImageBytes));
        Assert.NotNull(AiChatAttachmentPolicy.Validate("a.png", AiChatAttachmentPolicy.MaxImageBytes + 1));

        // 文件的上限比圖片大：同一個大小，文件收、圖片不收。
        Assert.Null(AiChatAttachmentPolicy.Validate("a.pdf", AiChatAttachmentPolicy.MaxImageBytes + 1));
        Assert.NotNull(AiChatAttachmentPolicy.Validate("a.pdf", AiChatAttachmentPolicy.MaxDocumentBytes + 1));

        Assert.NotNull(AiChatAttachmentPolicy.Validate("a.png", 0));
        Assert.Contains("格式不支援", AiChatAttachmentPolicy.Validate("a.pptx", 10));
    }

    // ───────── 存檔 ─────────

    [Fact]
    public async Task SaveThenAppend_ShouldRoundTripAttachmentsOnTheQuestion()
    {
        var saved = await store.SaveAttachmentsAsync(AiChatScope.Project, 3, Conv,
            [new PendingAttachment("截圖.png", [1, 2, 3]), new PendingAttachment("合約.txt", "付款條件：30 天"u8.ToArray())]);

        await store.AppendTurnAsync(AiChatScope.Project, 3, Conv, "這張圖是什麼？", "support", "是一張截圖。", default, saved);

        var history = await store.ReadHistoryAsync(AiChatScope.Project, 3, Conv);

        Assert.Equal(2, history.Count);
        Assert.Equal(["截圖.png", "合約.txt"], history[0].AttachmentList.Select(x => x.FileName));
        Assert.Equal([AiChatAttachmentKind.Image, AiChatAttachmentKind.Document], history[0].AttachmentList.Select(x => x.Kind));
        Assert.Empty(history[1].AttachmentList);

        // 磁碟上的檔名是隨機的，但內容要對得上。
        var imagePath = store.GetAttachmentFullPath(AiChatScope.Project, 3, Conv, history[0].AttachmentList[0].StoredName);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(imagePath));
        Assert.NotEqual("截圖.png", Path.GetFileName(imagePath));
    }

    [Fact]
    public async Task TurnWithoutAttachments_ShouldNotWriteAttachmentsField()
    {
        // 舊格式相容：沒附件的訊息不要多出欄位（WhenWritingNull）。
        await store.AppendTurnAsync(AiChatScope.Meeting, 5, Conv, "問題", "support", "回答");

        var raw = await File.ReadAllTextAsync(store.GetFullPath(AiChatScope.Meeting, 5, Conv));

        Assert.DoesNotContain("attachments", raw);
    }

    [Fact]
    public async Task ListConversations_ShouldIgnoreTheAttachmentFolder()
    {
        var saved = await store.SaveAttachmentsAsync(AiChatScope.Project, 3, Conv, [new PendingAttachment("a.png", [1])]);
        await store.AppendTurnAsync(AiChatScope.Project, 3, Conv, "問題", "support", "回答", default, saved);

        var list = await store.ListConversationsAsync(AiChatScope.Project, 3);

        Assert.Single(list);
    }

    [Fact]
    public async Task DeleteConversation_ShouldAlsoDeleteItsAttachments()
    {
        var saved = await store.SaveAttachmentsAsync(AiChatScope.Project, 3, Conv, [new PendingAttachment("a.png", [1])]);
        await store.AppendTurnAsync(AiChatScope.Project, 3, Conv, "問題", "support", "回答", default, saved);
        var imagePath = store.GetAttachmentFullPath(AiChatScope.Project, 3, Conv, saved[0].StoredName);

        store.TryDeleteConversation(AiChatScope.Project, 3, Conv);

        Assert.False(File.Exists(imagePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(imagePath)));
    }

    [Fact]
    public async Task TryDeleteAttachments_ShouldRemoveFilesOfAFailedQuestion()
    {
        var saved = await store.SaveAttachmentsAsync(AiChatScope.Project, 3, Conv, [new PendingAttachment("a.png", [1])]);
        var imagePath = store.GetAttachmentFullPath(AiChatScope.Project, 3, Conv, saved[0].StoredName);

        store.TryDeleteAttachments(AiChatScope.Project, 3, Conv, saved);

        Assert.False(File.Exists(imagePath));
    }

    [Fact]
    public void GetAttachmentFullPath_ShouldNotEscapeTheAttachmentFolder()
    {
        // 對話檔可以被手動編輯，storedName 不能被拿來做路徑跳脫。
        var path = store.GetAttachmentFullPath(AiChatScope.Project, 3, Conv, @"..\..\..\secret.txt");

        Assert.EndsWith($"{Conv}.files{Path.DirectorySeparatorChar}secret.txt", path);
    }

    // ───────── 送進模型的內容 ─────────

    [Fact]
    public void CollectAttachments_ShouldPutCurrentFirstThenRecentHistoryNewestFirst()
    {
        var history = new List<AiChatMessageItem>
        {
            User("第一問", Image("old.png")),
            Assistant(),
            User("第二問", Image("newer.png")),
            Assistant(),
        };

        var collected = AiChatService.CollectAttachments(history, [Image("now.png")], maxTurns: 6);

        Assert.Equal(["now.png", "newer.png", "old.png"], collected.Select(x => x.FileName));
    }

    [Fact]
    public void CollectAttachments_ShouldOnlyReachBackAsFarAsTheHistoryWindow()
    {
        var history = new List<AiChatMessageItem>
        {
            User("很久以前", Image("too-old.png")),
            Assistant(),
            User("最近", Image("recent.png")),
            Assistant(),
        };

        var collected = AiChatService.CollectAttachments(history, [], maxTurns: 1);

        Assert.Equal(["recent.png"], collected.Select(x => x.FileName));
    }

    [Fact]
    public void SelectImages_ShouldCapCountAndSkipDocuments()
    {
        var attachments = new[] { Image("1.png"), Document("a.pdf"), Image("2.png"), Image("3.png") };

        var selected = AiChatService.SelectImages(attachments, max: 2);

        Assert.Equal(["1.png", "2.png"], selected.Select(x => x.FileName));
    }

    [Fact]
    public void BuildUserPrompt_ShouldNameTheAttachmentsOnHistoryAndCurrentQuestion()
    {
        var history = new List<AiChatMessageItem> { User("上次的問題", Document("合約.pdf")), Assistant() };

        var prompt = AiChatService.BuildUserPrompt("資料", history, "這張圖呢？", [Image("截圖.png")]);

        Assert.Contains("使用者：上次的問題（附件：合約.pdf）", prompt);
        Assert.Contains("問題：這張圖呢？（附件：圖片 截圖.png）", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithoutAttachments_ShouldBeUnchanged()
    {
        var prompt = AiChatService.BuildUserPrompt("資料", [], "問題");

        Assert.EndsWith($"問題：問題{Environment.NewLine}", prompt);
    }

    [Fact]
    public void SerializeUserContent_WithoutImages_ShouldStayAPlainString()
    {
        // 會議紀錄生成、抽出待辦都不帶圖，送出的內容必須與以前完全相同。
        foreach (var images in new IReadOnlyList<PromptImage>?[] { null, [] })
        {
            using var document = JsonDocument.Parse(AzureOpenAiTextGenerationProvider.SerializeUserContent("問題", images));

            Assert.Equal(JsonValueKind.String, document.RootElement.ValueKind);
            Assert.Equal("問題", document.RootElement.GetString());
        }
    }

    [Fact]
    public void SerializeUserContent_WithImages_ShouldBeTextPartThenDataUrlParts()
    {
        var json = AzureOpenAiTextGenerationProvider.SerializeUserContent(
            "看圖", [new PromptImage("image/png", [1, 2, 3])]);

        using var document = JsonDocument.Parse(json);
        var parts = document.RootElement.EnumerateArray().ToList();

        Assert.Equal(2, parts.Count);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("看圖", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void ExportedPdf_ShouldListAttachmentNamesUnderTheQuestion()
    {
        var html = MeetingRecord.Business.Services.Export.AiChatDocumentExporter.BuildConversationHtml(
            "專案", [User("這張圖是什麼？", Image("截圖<1>.png"), Document("合約.pdf")), Assistant()], DateTime.Now);

        // 檔名要跳脫：使用者的檔名可能含有 < >。
        Assert.Contains("附件：截圖&lt;1&gt;.png、合約.pdf", html);
    }

    private static AiChatAttachment Image(string name) => new(name, name, AiChatAttachmentKind.Image, 1);

    private static AiChatAttachment Document(string name) => new(name, name, AiChatAttachmentKind.Document, 1);

    private static AiChatMessageItem User(string content, params AiChatAttachment[] attachments)
        => new(AiChatService.UserRole, content, "support", DateTime.Now, attachments);

    private static AiChatMessageItem Assistant() => new(AiChatService.AssistantRole, "回答", null, DateTime.Now);

    public void Dispose()
    {
        loggerFactory.Dispose();

        try
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
