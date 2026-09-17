using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 多段對話（0.4.79）的儲存層測試。
///
/// <para>
/// 最重要的兩件事：**舊對話不能不見**（自動轉檔），以及 **meta 行不可以被當成訊息**——
/// 後者第一次實作時就漏掉了，畫面上出現一則沒有內容、發話者是「AI 助理」的空氣訊息。
/// </para>
/// </summary>
public sealed class AiChatConversationTests : IDisposable
{
    private readonly string rootPath;
    private readonly ILoggerFactory loggerFactory;
    private readonly AiChatStore store;

    public AiChatConversationTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        loggerFactory = LoggerFactory.Create(_ => { });

        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiChatPath = rootPath;

        store = new AiChatStore(Options.Create(settings), loggerFactory.CreateLogger<AiChatStore>());
    }

    #region meta 行不是訊息

    [Fact]
    public async Task NewConversation_ShouldHaveNoMessages()
    {
        // ⚠️ 這一筆守的是實際發生過的 bug：meta 行被當成訊息渲染，
        // 使用者還沒問任何問題就看到一則空白的「AI 助理」。
        var id = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12, id);

        Assert.Empty(history);
    }

    [Fact]
    public async Task NewConversation_ShouldAppearInListImmediately()
    {
        // 按了「開新對話」卻什麼都沒發生是最糟的。還沒問問題也要在清單上。
        var id = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");

        var list = await store.ListConversationsAsync(AiChatScope.Meeting, 12);

        var only = Assert.Single(list);
        Assert.Equal(id, only.Id);
        Assert.Equal("新對話", only.Title);
        Assert.Equal("王小明", only.CreatedBy);
        Assert.Equal(0, only.MessageCount);
    }

    [Fact]
    public async Task MetaLine_ShouldNotShiftMessageIndexes()
    {
        // meta 行若只被 ReadHistory 排除、沒被索引映射排除，編輯「第 0 則」會改到 meta。
        var id = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, id, "問題", "王小明", "回答");

        var outcome = await store.UpdateMessagesAsync(
            AiChatScope.Meeting,
            12,
            id,
            [new MessageEdit(0, AiChatService.UserRole, "問題", "改過的問題")]);

        Assert.Equal(UpdateOutcome.Updated, outcome);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12, id);
        Assert.Equal(2, history.Count);
        Assert.Equal("改過的問題", history[0].Content);
        Assert.Equal("回答", history[1].Content);
    }

    [Fact]
    public async Task CountQuestions_ShouldIgnoreMetaLinesAndReachNestedFolders()
    {
        // 兩件事：meta 行不可以被算成提問，而且對話檔在 0.4.79 之後**多了一層資料夾**
        // （meeting/12/<id>.jsonl）。少遞迴一層，儀表板的「AI 問答次數」會整個變成 0。
        var first = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        var second = await store.CreateConversationAsync(AiChatScope.Project, 3, "李小華");

        await store.AppendTurnAsync(AiChatScope.Meeting, 12, first, "問題一", "王小明", "回答一");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, first, "問題二", "王小明", "回答二");
        await store.AppendTurnAsync(AiChatScope.Project, 3, second, "問題三", "李小華", "回答三");

        // 3 則提問。3 則回答與 2 行 meta 都不算。
        Assert.Equal(3, store.CountQuestions());
    }

    #endregion

    #region 多段對話互不干擾

    [Fact]
    public async Task Conversations_ShouldNotContaminateEachOther()
    {
        var first = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        var second = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "李小華");

        await store.AppendTurnAsync(AiChatScope.Meeting, 12, first, "第一段的問題", "王小明", "第一段的回答");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, second, "第二段的問題", "李小華", "第二段的回答");

        var firstHistory = await store.ReadHistoryAsync(AiChatScope.Meeting, 12, first);
        var secondHistory = await store.ReadHistoryAsync(AiChatScope.Meeting, 12, second);

        Assert.Equal("第一段的問題", firstHistory[0].Content);
        Assert.Equal("第二段的問題", secondHistory[0].Content);
        Assert.Equal(2, firstHistory.Count);
        Assert.Equal(2, secondHistory.Count);
    }

    [Fact]
    public async Task DeletingOneConversation_ShouldKeepTheOthers()
    {
        var first = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        var second = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "李小華");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, second, "留下來的問題", "李小華", "留下來的回答");

        store.TryDeleteConversation(AiChatScope.Meeting, 12, first);

        var list = await store.ListConversationsAsync(AiChatScope.Meeting, 12);

        Assert.Equal(second, Assert.Single(list).Id);
    }

    [Fact]
    public async Task Scopes_WithSameId_ShouldNotShareConversations()
    {
        var projectConversation = await store.CreateConversationAsync(AiChatScope.Project, 12, "王小明");
        await store.AppendTurnAsync(AiChatScope.Project, 12, projectConversation, "專案的問題", "王小明", "專案的回答");

        var meetingList = await store.ListConversationsAsync(AiChatScope.Meeting, 12);

        Assert.Empty(meetingList);
    }

    #endregion

    #region 標題

    [Fact]
    public async Task Title_ShouldFallBackToFirstQuestion()
    {
        var id = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, id, "這次會議的決議是什麼", "王小明", "回答");

        var list = await store.ListConversationsAsync(AiChatScope.Meeting, 12);

        Assert.Equal("這次會議的決議是什麼", Assert.Single(list).Title);
    }

    [Fact]
    public async Task Title_ShouldTruncateLongQuestion()
    {
        var id = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        await store.AppendTurnAsync(
            AiChatScope.Meeting, 12, id, new string('問', 50), "王小明", "回答");

        var title = Assert.Single(await store.ListConversationsAsync(AiChatScope.Meeting, 12)).Title;

        // 20 個字加一個刪節號。清單放不下一整句話。
        Assert.Equal(21, title.Length);
        Assert.EndsWith("…", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_ShouldWinOverFirstQuestion()
    {
        var id = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, id, "原本的問題", "王小明", "回答");

        var outcome = await store.RenameConversationAsync(AiChatScope.Meeting, 12, id, "合約條款討論");

        Assert.Equal(UpdateOutcome.Updated, outcome);
        Assert.Equal("合約條款討論", Assert.Single(await store.ListConversationsAsync(AiChatScope.Meeting, 12)).Title);
    }

    [Fact]
    public async Task Rename_ShouldKeepMessagesIntact()
    {
        // 改名是整檔重寫，訊息一則都不能少。
        var id = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, id, "問題", "王小明", "回答");

        await store.RenameConversationAsync(AiChatScope.Meeting, 12, id, "新名字");

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12, id);
        Assert.Equal(2, history.Count);
        Assert.Equal("問題", history[0].Content);
        Assert.Equal("王小明", history[0].AskedBy);
    }

    [Fact]
    public async Task Rename_MissingConversation_ShouldReturnNotFound()
    {
        var outcome = await store.RenameConversationAsync(AiChatScope.Meeting, 12, "no-such-conversation", "名字");

        Assert.Equal(UpdateOutcome.NotFound, outcome);
    }

    #endregion

    #region 舊資料自動轉檔

    [Fact]
    public async Task LegacyFile_ShouldBeMigratedWithContentIntact()
    {
        // ⚠️ 使用者最在意的就是「先前問過的東西不要不見」。
        WriteLegacyFile(AiChatScope.Meeting, 12,
            """{"role":"user","content":"舊的問題","askedBy":"王小明","createdAt":"2026-09-10T10:00:00"}""",
            """{"role":"assistant","content":"舊的回答","createdAt":"2026-09-10T10:00:05"}""");

        var list = await store.ListConversationsAsync(AiChatScope.Meeting, 12);

        var only = Assert.Single(list);
        Assert.Equal("舊的問題", only.Title);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12, only.Id);
        Assert.Equal(2, history.Count);
        Assert.Equal("舊的問題", history[0].Content);
        Assert.Equal("王小明", history[0].AskedBy);
        Assert.Equal("舊的回答", history[1].Content);
    }

    [Fact]
    public async Task Migration_ShouldRemoveLegacyFile()
    {
        WriteLegacyFile(AiChatScope.Meeting, 12,
            """{"role":"user","content":"舊的問題","askedBy":"王小明","createdAt":"2026-09-10T10:00:00"}""");

        await store.ListConversationsAsync(AiChatScope.Meeting, 12);

        Assert.False(File.Exists(LegacyPath(AiChatScope.Meeting, 12)));
    }

    [Fact]
    public async Task Migration_ShouldBeIdempotent()
    {
        // 每次列清單都會呼叫轉檔，重複執行不可以變成兩段對話。
        WriteLegacyFile(AiChatScope.Meeting, 12,
            """{"role":"user","content":"舊的問題","askedBy":"王小明","createdAt":"2026-09-10T10:00:00"}""");

        await store.ListConversationsAsync(AiChatScope.Meeting, 12);
        var list = await store.ListConversationsAsync(AiChatScope.Meeting, 12);

        Assert.Single(list);
    }

    #endregion

    #region 刪除整個對象

    [Fact]
    public async Task TryDelete_ShouldRemoveEveryConversation()
    {
        // ⚠️ 0.4.79 之前刪的是單一檔案。只刪一個檔的話，其他幾段會永遠留在硬碟上。
        var first = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "王小明");
        var second = await store.CreateConversationAsync(AiChatScope.Meeting, 12, "李小華");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, first, "問題一", "王小明", "回答一");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, second, "問題二", "李小華", "回答二");

        store.TryDelete(AiChatScope.Meeting, 12);

        Assert.False(Directory.Exists(store.GetConversationDirectory(AiChatScope.Meeting, 12)));
        Assert.Empty(await store.ListConversationsAsync(AiChatScope.Meeting, 12));
    }

    [Fact]
    public async Task TryDelete_ShouldAlsoRemoveNotYetMigratedLegacyFile()
    {
        // 還沒被轉檔就直接刪除的對象，舊的單檔不清掉就是孤兒。
        WriteLegacyFile(AiChatScope.Meeting, 12,
            """{"role":"user","content":"舊的問題","askedBy":"王小明","createdAt":"2026-09-10T10:00:00"}""");

        store.TryDelete(AiChatScope.Meeting, 12);

        Assert.False(File.Exists(LegacyPath(AiChatScope.Meeting, 12)));
        Assert.Empty(await store.ListConversationsAsync(AiChatScope.Meeting, 12));
    }

    #endregion

    private string LegacyPath(AiChatScope scope, int targetId)
        => Path.Combine(rootPath, scope == AiChatScope.Project ? "project" : "meeting", $"{targetId}.jsonl");

    /// <summary>寫一個 0.4.79 之前格式的對話檔（UTF-8 含 BOM，與正式寫入一致）。</summary>
    private void WriteLegacyFile(AiChatScope scope, int targetId, params string[] lines)
    {
        var fullPath = LegacyPath(scope, targetId);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(rootPath, recursive: true);
        }
        catch (IOException)
        {
            // 測試用的暫存目錄清不掉不該讓測試失敗。
        }
    }
}
