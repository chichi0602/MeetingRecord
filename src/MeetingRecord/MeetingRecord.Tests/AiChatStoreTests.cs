using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MeetingRecord.Business.Services.AiChat;
using MeetingRecord.Models.Systems;

namespace MeetingRecord.Tests;

/// <summary>
/// AI 問答對話檔（.jsonl）的存取測試。
///
/// 這個格式有兩個錯了很難查的地方：**第一行帶著檔案的 BOM**（不去掉就反序列化失敗，
/// 症狀是「第一則訊息神秘消失」），以及 **append 不能重複寫 BOM**。兩者都有專門的測試。
/// </summary>
public sealed class AiChatStoreTests : IDisposable
{
    private readonly string rootPath;
    private readonly ILoggerFactory loggerFactory;
    private readonly AiChatStore store;

    public AiChatStoreTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), "MeetingRecordTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        loggerFactory = LoggerFactory.Create(_ => { });

        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiChatPath = rootPath;

        store = new AiChatStore(Options.Create(settings), loggerFactory.CreateLogger<AiChatStore>());
    }

    #region 寫入與讀回

    [Fact]
    public async Task AppendTurn_ThenRead_ShouldRoundTripBothMessages()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "這次會議結論是什麼", "王小明", "依據會議紀錄，結論是…");

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12);

        Assert.Equal(2, history.Count);

        Assert.True(history[0].IsUser);
        Assert.Equal("這次會議結論是什麼", history[0].Content);
        Assert.Equal("王小明", history[0].AskedBy);

        Assert.False(history[1].IsUser);
        Assert.Equal("依據會議紀錄，結論是…", history[1].Content);
        // 回答沒有提問者，這個欄位必須是 null 而不是空字串——UI 會拿它決定顯示「AI 助理」。
        Assert.Null(history[1].AskedBy);
    }

    [Fact]
    public async Task ReadHistory_ShouldParseFirstLineDespiteBom()
    {
        // 檔案以 UTF-8 含 BOM 寫入（與逐字稿一致，使用者可能用記事本開）。
        // 沒有 TrimStart('\uFEFF') 的話，第一則訊息會被當成壞行而默默消失。
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "第一個問題", "王小明", "第一個回答");

        var bytes = await File.ReadAllBytesAsync(store.GetFullPath(AiChatScope.Meeting, 12));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12);

        Assert.Equal(2, history.Count);
        Assert.Equal("第一個問題", history[0].Content);
    }

    [Fact]
    public async Task AppendTurn_Twice_ShouldAppendNotOverwriteAndNotRepeatBom()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "問題一", "王小明", "回答一");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "問題二", "李小華", "回答二");

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12);

        Assert.Equal(4, history.Count);
        Assert.Equal("問題一", history[0].Content);
        Assert.Equal("回答二", history[3].Content);

        // BOM 只能出現一次。這裡一定要看原始位元組——File.ReadAllText 會自動吃掉開頭的
        // BOM，用字串去數永遠是 0，那條斷言驗不到任何東西。
        var bytes = await File.ReadAllBytesAsync(store.GetFullPath(AiChatScope.Meeting, 12));
        var bomCount = 0;
        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == 0xEF && bytes[i + 1] == 0xBB && bytes[i + 2] == 0xBF)
            {
                bomCount++;
            }
        }

        Assert.Equal(1, bomCount);
    }

    [Fact]
    public async Task AppendTurn_ShouldPreserveNewlinesAndJsonSpecialCharacters()
    {
        // 回答幾乎一定含換行（條列式），內容也可能含引號與反斜線。
        // 換行若沒被 JSON 逃脫，一則訊息會被拆成好幾行而讀壞整個檔案。
        const string answer = "第一點\n第二點：他說「好」\n路徑是 C:\temp\a.txt";

        await store.AppendTurnAsync(AiChatScope.Project, 3, "請條列", "王小明", answer);

        var history = await store.ReadHistoryAsync(AiChatScope.Project, 3);

        Assert.Equal(2, history.Count);
        Assert.Equal(answer, history[1].Content);
    }

    [Fact]
    public async Task AppendTurn_ShouldTrimQuestionButKeepAnswerAsIs()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "  有空白  ", "王小明", "  回答保持原樣  ");

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12);

        Assert.Equal("有空白", history[0].Content);
        Assert.Equal("  回答保持原樣  ", history[1].Content);
    }

    #endregion

    #region 對話之間的隔離

    [Fact]
    public async Task Scopes_WithSameId_ShouldNotShareFile()
    {
        // 專案 12 與會議 12 是完全不同的兩段對話，混在一起會外洩到不相干的畫面。
        await store.AppendTurnAsync(AiChatScope.Project, 12, "專案的問題", "王小明", "專案的回答");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "會議的問題", "王小明", "會議的回答");

        var projectHistory = await store.ReadHistoryAsync(AiChatScope.Project, 12);
        var meetingHistory = await store.ReadHistoryAsync(AiChatScope.Meeting, 12);

        Assert.Equal("專案的問題", Assert.Single(projectHistory, x => x.IsUser).Content);
        Assert.Equal("會議的問題", Assert.Single(meetingHistory, x => x.IsUser).Content);
        Assert.NotEqual(
            store.GetFullPath(AiChatScope.Project, 12),
            store.GetFullPath(AiChatScope.Meeting, 12));
    }

    #endregion

    #region 不存在與損毀

    [Fact]
    public async Task ReadHistory_ForUnknownConversation_ShouldReturnEmpty()
    {
        // 每個對話視窗第一次開啟都會走到這裡，不能拋例外。
        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 999);

        Assert.Empty(history);
    }

    [Fact]
    public void TryDelete_ForUnknownConversation_ShouldNotThrow()
    {
        var exception = Record.Exception(() => store.TryDelete(AiChatScope.Project, 999));

        Assert.Null(exception);
    }

    [Fact]
    public async Task TryDelete_ShouldRemoveTheWholeConversation()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "問題", "王小明", "回答");

        store.TryDelete(AiChatScope.Meeting, 12);

        Assert.False(File.Exists(store.GetFullPath(AiChatScope.Meeting, 12)));
        Assert.Empty(await store.ReadHistoryAsync(AiChatScope.Meeting, 12));
    }

    [Fact]
    public async Task ReadHistory_ShouldSkipMalformedLinesAndKeepTheRest()
    {
        // 手動編輯過、或寫到一半斷電。一行壞掉不該讓整段歷史都讀不出來。
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "好的問題", "王小明", "好的回答");

        var path = store.GetFullPath(AiChatScope.Meeting, 12);
        await File.AppendAllTextAsync(path, "{ 這不是合法的 JSON\n\n");

        var history = await store.ReadHistoryAsync(AiChatScope.Meeting, 12);

        Assert.Equal(2, history.Count);
        Assert.Equal("好的問題", history[0].Content);
    }

    #endregion

    #region 儀表板計數

    [Fact]
    public async Task CountQuestions_ShouldCountOnlyUserMessagesAcrossAllConversations()
    {
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "問題一", "王小明", "回答一");
        await store.AppendTurnAsync(AiChatScope.Meeting, 12, "問題二", "王小明", "回答二");
        await store.AppendTurnAsync(AiChatScope.Project, 3, "問題三", "李小華", "回答三");

        // 三問三答共六則，但「提問次數」只算三次。
        Assert.Equal(3, store.CountQuestions());
    }

    [Fact]
    public void CountQuestions_WithNoConversationsYet_ShouldReturnZero()
    {
        // 全新安裝時根目錄可能還不存在，不能拋 DirectoryNotFoundException 把儀表板弄掛。
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiChatPath = Path.Combine(rootPath, "not-created-yet");

        var emptyStore = new AiChatStore(
            Options.Create(settings),
            loggerFactory.CreateLogger<AiChatStore>());

        Assert.Equal(0, emptyStore.CountQuestions());
    }

    #endregion

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
