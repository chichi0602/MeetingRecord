using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// 對話檔的<b>就地修改</b>測試（0.4.70 起）。
///
/// 這是唯一會整檔重寫的路徑，三個地雷都在這裡：重寫後 BOM 只能有一個、
/// 壞行與空行必須原樣保留（否則會默默吃掉使用者的資料）、以及對不上預期時
/// 必須整批不動。
/// </summary>
public sealed class AiChatStoreUpdateTests : IDisposable
{
    private const int TargetId = 12;

    private readonly string rootPath;
    private readonly ILoggerFactory loggerFactory;
    /// <summary>測試用的固定對話 Id。0.4.79 起一個對象底下可以有多段對話。</summary>
    private const string Conv = "test-conversation";

    private readonly AiChatStore store;

    public AiChatStoreUpdateTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        loggerFactory = LoggerFactory.Create(_ => { });

        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiChatPath = rootPath;

        store = new AiChatStore(Options.Create(settings), loggerFactory.CreateLogger<AiChatStore>());
    }

    #region 正常修改

    [Fact]
    public async Task Update_ShouldReplaceTargetContent()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "原本的問題", "王小明", "原本的回答");

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "原本的問題", "改過的問題")]);

        Assert.Equal(UpdateOutcome.Updated, outcome);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);
        Assert.Equal("改過的問題", history[0].Content);
    }

    [Fact]
    public async Task Update_ShouldLeaveOtherMessagesUntouched()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題一", "王小明", "回答一");
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題二", "李小華", "回答二");

        await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(2, AiChatService.UserRole, "問題二", "問題二改")]);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);

        Assert.Equal(4, history.Count);
        Assert.Equal("問題一", history[0].Content);
        Assert.Equal("回答一", history[1].Content);
        Assert.Equal("問題二改", history[2].Content);
        Assert.Equal("回答二", history[3].Content);
    }

    [Fact]
    public async Task Update_ShouldPreserveAskedByAndCreatedAt()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");
        var before = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);

        await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "問題", "問題改")]);

        var after = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);

        // 就地編輯是「更正內容」，不是「重新發問」，所以這兩個欄位不該動。
        Assert.Equal("王小明", after[0].AskedBy);
        Assert.Equal(before[0].CreatedAt, after[0].CreatedAt);
    }

    [Fact]
    public async Task Update_ShouldReplaceAskedByWhenRequested()
    {
        // 重新產生時要換成實際操作的人，否則「王小明問的」其實是李小華改的。
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "問題", "問題改", NewAskedBy: "李小華")]);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);
        Assert.Equal("李小華", history[0].AskedBy);
    }

    [Fact]
    public async Task Update_ShouldApplyTwoEditsAtOnce()
    {
        // 重新產生會同時改提問與回答。只改到一半是最糟的中間態。
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
        [
            new MessageEdit(0, AiChatService.UserRole, "問題", "新問題"),
            new MessageEdit(1, AiChatService.AssistantRole, "回答", "新回答"),
        ]);

        Assert.Equal(UpdateOutcome.Updated, outcome);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);
        Assert.Equal("新問題", history[0].Content);
        Assert.Equal("新回答", history[1].Content);
    }

    [Fact]
    public async Task Update_ShouldRoundTripTrickyCharacters()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var tricky = "第一行\n第二行\t含「引號」與 \\ 反斜線 \"double\"";
        await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(1, AiChatService.AssistantRole, "回答", tricky)]);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);
        Assert.Equal(tricky, history[1].Content);
    }

    #endregion

    #region BOM 與後續 append

    [Fact]
    public async Task Update_ShouldKeepExactlyOneBom()
    {
        // 整檔重寫最容易出的錯：寫回去時又補了一個 BOM，或者把原本的弄丟。
        // 一定要看原始位元組——File.ReadAllText 會自動吃掉開頭的 BOM，用字串去數永遠是 0。
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "問題", "問題改")]);

        var bytes = await File.ReadAllBytesAsync(store.GetFullPath(AiChatScope.Meeting, TargetId, Conv));

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.Equal(1, CountBom(bytes));
    }

    [Fact]
    public async Task Update_ThenAppend_ShouldKeepOrderAndSingleBom()
    {
        // 重寫時結尾若沒留換行，下一次 append 會直接黏在最後一行後面，那一行就壞了。
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題一", "王小明", "回答一");

        await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "問題一", "問題一改")]);

        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題二", "李小華", "回答二");

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);

        Assert.Equal(4, history.Count);
        Assert.Equal("問題一改", history[0].Content);
        Assert.Equal("問題二", history[2].Content);

        var bytes = await File.ReadAllBytesAsync(store.GetFullPath(AiChatScope.Meeting, TargetId, Conv));
        Assert.Equal(1, CountBom(bytes));
    }

    #endregion

    #region 壞行與空行

    [Fact]
    public async Task Update_ShouldTargetCorrectMessageWhenMalformedLinesComeFirst()
    {
        // 「第 n 則有效訊息」不等於「檔案第 n 行」。用行號定位會改到別人。
        await ArrangeWithLeadingJunkAsync();

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "問題", "問題改")]);

        Assert.Equal(UpdateOutcome.Updated, outcome);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);
        Assert.Equal(2, history.Count);
        Assert.Equal("問題改", history[0].Content);
        Assert.Equal("回答", history[1].Content);
    }

    [Fact]
    public async Task Update_ShouldPreserveMalformedAndBlankLines()
    {
        // 讀不懂的行可能是使用者自己手動編輯過的內容，重寫時不能默默丟掉。
        await ArrangeWithLeadingJunkAsync();

        await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "問題", "問題改")]);

        var lines = await File.ReadAllLinesAsync(store.GetFullPath(AiChatScope.Meeting, TargetId, Conv));

        Assert.Equal(4, lines.Length);
        Assert.Equal(MalformedLine, lines[0]);
        Assert.Equal(string.Empty, lines[1]);
    }

    #endregion

    #region 衝突與不存在

    [Fact]
    public async Task Update_ShouldReturnConflictWhenContentChanged()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var fullPath = store.GetFullPath(AiChatScope.Meeting, TargetId, Conv);
        var before = await File.ReadAllBytesAsync(fullPath);

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "別人已經改掉的內容", "我的修改")]);

        Assert.Equal(UpdateOutcome.Conflict, outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task Update_ShouldReturnConflictWhenRoleDoesNotMatch()
    {
        // 重新產生時假設「提問的下一則是回答」，但檔案可能被手改過。
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.AssistantRole, "問題", "我的修改")]);

        Assert.Equal(UpdateOutcome.Conflict, outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Update_ShouldReturnConflictWhenIndexOutOfRange(int index)
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(index, AiChatService.UserRole, "問題", "問題改")]);

        Assert.Equal(UpdateOutcome.Conflict, outcome);
    }

    [Fact]
    public async Task Update_ShouldNotApplyAnyEditWhenOneOfThemConflicts()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
        [
            new MessageEdit(0, AiChatService.UserRole, "問題", "新問題"),
            new MessageEdit(1, AiChatService.AssistantRole, "對不上的回答", "新回答"),
        ]);

        Assert.Equal(UpdateOutcome.Conflict, outcome);

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, TargetId, Conv);
        Assert.Equal("問題", history[0].Content);
        Assert.Equal("回答", history[1].Content);
    }

    [Fact]
    public async Task Update_ShouldReturnNotFoundAndNotRecreateDeletedConversation()
    {
        // 有人按了「清空這段對話」。這時重建檔案會讓一則已刪的訊息憑空復活。
        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv,
            [new MessageEdit(0, AiChatService.UserRole, "問題", "問題改")]);

        Assert.Equal(UpdateOutcome.NotFound, outcome);
        Assert.False(File.Exists(store.GetFullPath(AiChatScope.Meeting, TargetId, Conv)));
    }

    [Fact]
    public async Task Update_WithNoEdits_ShouldSucceedWithoutTouchingTheFile()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var fullPath = store.GetFullPath(AiChatScope.Meeting, TargetId, Conv);
        var before = await File.ReadAllBytesAsync(fullPath);

        var outcome = await store.UpdateMessagesAsync(AiChatScope.Meeting, TargetId, Conv, []);

        Assert.Equal(UpdateOutcome.Updated, outcome);
        Assert.Equal(before, await File.ReadAllBytesAsync(fullPath));
    }

    #endregion

    private const string MalformedLine = "{ 這不是合法的 JSON";

    /// <summary>在一輪正常的問答前面塞一行壞的與一行空的，模擬被手動編輯過的檔案。</summary>
    private async Task ArrangeWithLeadingJunkAsync()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, TargetId, Conv, "問題", "王小明", "回答");

        var fullPath = store.GetFullPath(AiChatScope.Meeting, TargetId, Conv);
        var original = await File.ReadAllLinesAsync(fullPath);

        await File.WriteAllLinesAsync(
            fullPath,
            [MalformedLine, string.Empty, original[0], original[1]],
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static int CountBom(byte[] bytes)
    {
        var count = 0;

        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF)
            {
                count++;
            }
        }

        return count;
    }

    public void Dispose()
    {
        loggerFactory.Dispose();

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
