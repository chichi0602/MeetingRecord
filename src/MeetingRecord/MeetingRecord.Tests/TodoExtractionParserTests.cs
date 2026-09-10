using MeetingRecord.Business.Services.TodoExtraction;
using MeetingRecord.Models.AdapterModel;

namespace MeetingRecord.Tests;

/// <summary>
/// LLM 輸出的解析測試。
///
/// 這是整個「AI 抽出待辦」功能唯一真正脆弱的地方——模型不保證回乾淨的 JSON，
/// 而且它歪掉的方式每次都不一樣。這裡把實務上會遇到的形狀一個個釘住。
/// </summary>
public sealed class TodoExtractionParserTests
{
    private const string CleanJson = """
        [
          {"title":"完成第一期介面設計稿","description":"含手機版","owner":"王小明","dueDate":"2026-09-20","priority":"高"},
          {"title":"串接付款 API","description":null,"owner":null,"dueDate":null,"priority":"中"}
        ]
        """;

    #region 各種歪掉的外層包裝

    [Fact]
    public void Parse_CleanJsonArray_ShouldReturnAllItems()
    {
        var results = TodoExtractionParser.Parse(CleanJson);

        Assert.Equal(2, results.Count);
        Assert.Equal("完成第一期介面設計稿", results[0].Title);
        Assert.Equal("含手機版", results[0].Description);
        Assert.Equal("王小明", results[0].Owner);
        Assert.Equal(new DateTime(2026, 9, 20), results[0].DueDate);
        Assert.Equal("高", results[0].Priority);
    }

    [Fact]
    public void Parse_WrappedInMarkdownFence_ShouldStillParse()
    {
        // 最常見的一種：提示詞說了「不要圍籬」，模型還是會包。
        var raw = $"```json\n{CleanJson}\n```";

        Assert.Equal(2, TodoExtractionParser.Parse(raw).Count);
    }

    [Fact]
    public void Parse_WithSurroundingProse_ShouldStillParse()
    {
        // 第二常見：前面加一句客套話、後面補一段說明。
        var raw = $"好的，以下是我從會議紀錄抽出的待辦事項：\n\n{CleanJson}\n\n如需調整請告訴我。";

        Assert.Equal(2, TodoExtractionParser.Parse(raw).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("這份會議紀錄沒有明確的待辦事項。")]
    [InlineData("[ 這不是合法的 JSON")]
    [InlineData("{\"title\":\"這是物件不是陣列\"}")]
    public void Parse_UnusableOutput_ShouldReturnEmptyWithoutThrowing(string? raw)
    {
        // 「抽不到待辦」與「模型今天回了奇怪的東西」對使用者是同一件事：
        // 畫面顯示提示，不是紅色錯誤。所以一律回空清單、不拋例外。
        var results = TodoExtractionParser.Parse(raw);

        Assert.Empty(results);
    }

    [Fact]
    public void Parse_EmptyArray_ShouldReturnEmpty()
    {
        Assert.Empty(TodoExtractionParser.Parse("[]"));
    }

    #endregion

    #region 截止日

    [Theory]
    [InlineData("2026-09-20")]
    [InlineData("2026/09/20")]
    [InlineData("2026.09.20")]
    [InlineData("20260920")]
    public void Parse_AbsoluteDate_ShouldBeAccepted(string value)
    {
        var results = TodoExtractionParser.Parse($$"""[{"title":"T","dueDate":"{{value}}"}]""");

        Assert.Equal(new DateTime(2026, 9, 20), Assert.Single(results).DueDate);
    }

    [Theory]
    [InlineData("下週五")]
    [InlineData("三天內")]
    [InlineData("ASAP")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_NonAbsoluteDate_ShouldBecomeNull(string? value)
    {
        // 刻意不去推算相對日期：算錯一個截止日比留白更糟——留白使用者一眼看得出要補，
        // 算錯了他不會發現。
        var json = value is null
            ? """[{"title":"T","dueDate":null}]"""
            : $$"""[{"title":"T","dueDate":"{{value}}"}]""";

        Assert.Null(Assert.Single(TodoExtractionParser.Parse(json)).DueDate);
    }

    [Fact]
    public void Parse_MissingDueDateField_ShouldBecomeNull()
    {
        Assert.Null(Assert.Single(TodoExtractionParser.Parse("""[{"title":"T"}]""")).DueDate);
    }

    #endregion

    #region 優先度

    [Theory]
    [InlineData("高", "高")]
    [InlineData("中", "中")]
    [InlineData("低", "低")]
    [InlineData("High", "高")]
    [InlineData("low", "低")]
    [InlineData("MEDIUM", "中")]
    public void Parse_KnownPriority_ShouldNormalize(string input, string expected)
    {
        var results = TodoExtractionParser.Parse($$"""[{"title":"T","priority":"{{input}}"}]""");

        Assert.Equal(expected, Assert.Single(results).Priority);
    }

    [Theory]
    [InlineData("緊急")]
    [InlineData("一般")]
    [InlineData("P0")]
    [InlineData("")]
    public void Parse_UnknownPriority_ShouldFallBackToMedium(string input)
    {
        // 寧可全部落在「中」讓使用者自己調，也不要亂猜成「高」。
        var results = TodoExtractionParser.Parse($$"""[{"title":"T","priority":"{{input}}"}]""");

        Assert.Equal(TodoAdapterModel.PriorityOptions[1], Assert.Single(results).Priority);
    }

    [Fact]
    public void Parse_MissingPriorityField_ShouldFallBackToMedium()
    {
        var results = TodoExtractionParser.Parse("""[{"title":"T"}]""");

        Assert.Equal(TodoAdapterModel.PriorityOptions[1], Assert.Single(results).Priority);
    }

    #endregion

    #region 欄位清理

    [Fact]
    public void Parse_ItemWithoutTitle_ShouldBeDropped()
    {
        // 標題是必填，留著只會在寫入時才失敗，不如當場丟掉。
        var raw = """
            [
              {"title":"有標題","priority":"中"},
              {"title":"","priority":"高"},
              {"title":"   ","priority":"高"},
              {"priority":"高"}
            ]
            """;

        var results = TodoExtractionParser.Parse(raw);

        Assert.Equal("有標題", Assert.Single(results).Title);
    }

    [Fact]
    public void Parse_OverlongFields_ShouldBeTruncatedToModelLimits()
    {
        // TodoAdapterModel 的 StringLength 會擋下超長內容，與其讓寫入失敗不如先截斷。
        var longTitle = new string('標', 250);
        var longOwner = new string('人', 80);
        var raw = $$"""[{"title":"{{longTitle}}","owner":"{{longOwner}}","priority":"中"}]""";

        var item = Assert.Single(TodoExtractionParser.Parse(raw));

        Assert.Equal(200, item.Title.Length);
        Assert.Equal(50, item.Owner!.Length);
    }

    [Fact]
    public void Parse_BlankOptionalFields_ShouldBecomeNullNotEmptyString()
    {
        // 空字串與 null 在畫面上的意義不同：Owner 為 null 才會顯示「未指定」。
        var raw = """[{"title":"T","description":"  ","owner":"","priority":"中"}]""";

        var item = Assert.Single(TodoExtractionParser.Parse(raw));

        Assert.Null(item.Description);
        Assert.Null(item.Owner);
    }

    [Fact]
    public void Parse_ShouldTrimWhitespaceAroundValues()
    {
        var raw = """[{"title":"  有空白  ","owner":"  王小明  ","priority":"中"}]""";

        var item = Assert.Single(TodoExtractionParser.Parse(raw));

        Assert.Equal("有空白", item.Title);
        Assert.Equal("王小明", item.Owner);
    }

    #endregion

    #region 寫入時的固定欄位

    [Fact]
    public void ToAdapterModel_ShouldAlwaysStartAsPendingAndLinkTheSourceMeeting()
    {
        // 狀態不讓 AI 決定——剛抽出來的東西不可能已完成。
        // MeetingId 要填，這正是 Todo.MeetingId 這個外鍵存在的理由。
        var extracted = new ExtractedTodo("完成設計稿", null, "王小明", new DateTime(2026, 9, 20), "高");

        var model = TodoExtractionService.ToAdapterModel(extracted, projectId: 3, meetingId: 12);

        Assert.Equal(TodoAdapterModel.StatusOptions[0], model.Status);
        Assert.Equal(3, model.ProjectId);
        Assert.Equal(12, model.MeetingId);
        Assert.Equal("完成設計稿", model.Title);
        Assert.Equal("高", model.Priority);
    }

    #endregion

    #region 提示詞

    [Fact]
    public void BuildUserPrompt_ShouldContainDraftAndMeetingDate()
    {
        var prompt = TodoExtractionService.BuildUserPrompt("這是會議紀錄內容", new DateTime(2026, 9, 8));

        Assert.Contains("這是會議紀錄內容", prompt);
        // 會議日期是模型換算相對日期的唯一基準，漏掉的話「下週五」會被算到今天而不是會期。
        Assert.Contains("2026-09-08", prompt);
        Assert.Contains("yyyy-MM-dd", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithoutMeetingDate_ShouldTellModelToGiveNull()
    {
        var prompt = TodoExtractionService.BuildUserPrompt("內容", meetingDate: null);

        Assert.Contains("沒有記錄日期", prompt);
    }

    #endregion
}
