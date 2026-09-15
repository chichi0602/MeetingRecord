using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

/// <summary>
/// 名單提示詞區塊的單元測試。
///
/// 最重要的一條是「兩份都空 → 完全空字串」：<see cref="MeetingDraftJobRunnerTests"/> 對
/// UserPrompt 是全等比對，這裡多吐一個換行，那邊就會紅——而那正是「沒設名單時行為不變」
/// 的回歸保證。
/// </summary>
public sealed class NameGuidancePromptHelperTests
{
    #region 空清單

    [Fact]
    public void Build_BothEmpty_ShouldReturnExactlyEmptyString()
    {
        // 全等比對，不是 IsNullOrWhiteSpace——換行也不行。
        Assert.Equal(string.Empty, NameGuidancePromptHelper.Build([], []));
    }

    [Fact]
    public void Build_BothNull_ShouldReturnExactlyEmptyString()
    {
        Assert.Equal(string.Empty, NameGuidancePromptHelper.Build(null, null));
    }

    [Fact]
    public void Build_OnlyBlankEntries_ShouldReturnExactlyEmptyString()
    {
        Assert.Equal(string.Empty, NameGuidancePromptHelper.Build(["  ", string.Empty], ["\t"]));
    }

    #endregion

    #region 內容

    [Fact]
    public void Build_WithBothLists_ShouldContainBoth()
    {
        var text = NameGuidancePromptHelper.Build(["甲專案", "乙系統"], ["王小明", "陳大文"]);

        Assert.Contains("常用名詞：甲專案、乙系統", text);
        Assert.Contains("與會人員：王小明、陳大文", text);
    }

    [Fact]
    public void Build_WithOnlyGlossary_ShouldOmitAttendeeLineAndSpeakerRule()
    {
        var text = NameGuidancePromptHelper.Build(["甲專案"], []);

        Assert.Contains("常用名詞：甲專案", text);
        Assert.DoesNotContain("與會人員：", text);
        // 沒有人名清單時，「發言者標示只能用名單裡的名字」這條規則會誤導模型。
        Assert.DoesNotContain("發言者標示", text);
    }

    [Fact]
    public void Build_WithOnlyAttendees_ShouldOmitGlossaryLine()
    {
        var text = NameGuidancePromptHelper.Build([], ["王小明"]);

        Assert.Contains("與會人員：王小明", text);
        Assert.DoesNotContain("常用名詞：", text);
        Assert.Contains("發言者標示", text);
    }

    [Fact]
    public void Build_ShouldNumberRulesContiguously()
    {
        // 少了人名那條之後編號不能跳號，否則模型可能以為漏掉了一條規則。
        var text = NameGuidancePromptHelper.Build(["甲專案"], []);

        Assert.Contains("1. ", text);
        Assert.Contains("2. ", text);
        Assert.Contains("3. ", text);
        Assert.DoesNotContain("4. ", text);
    }

    [Fact]
    public void Build_ShouldAuthoriseCorrectionAgainstTheSystemPrompt()
    {
        // 共用的 SystemPrompt 說「不要臆測或補充逐字稿沒有提到的資訊」。
        // 沒有這句授權，模型多半會照 SystemPrompt 放棄改名。
        var text = NameGuidancePromptHelper.Build([], ["王小明"]);

        Assert.Contains("已提供的資訊", text);
        Assert.Contains("不屬於臆測", text);
    }

    [Fact]
    public void Build_ShouldForbidInventingNames()
    {
        // 授權的只有「改寫已存在的誤字」，不是「無中生有」。
        var text = NameGuidancePromptHelper.Build([], ["王小明"]);

        Assert.Contains("不得新增名單中沒有", text);
        Assert.Contains("不要憑空替他生出發言或待辦", text);
    }

    [Fact]
    public void Build_ShouldTellModelToKeepOriginalWhenUnsure()
    {
        var text = NameGuidancePromptHelper.Build([], ["王小明"]);

        Assert.Contains("保留逐字稿原本的寫法，不要猜", text);
    }

    #endregion

    #region 清洗

    [Fact]
    public void Build_ShouldTrimAndDropBlankEntries()
    {
        var text = NameGuidancePromptHelper.Build([" 甲專案 ", "   ", "乙系統"], []);

        Assert.Contains("常用名詞：甲專案、乙系統", text);
    }

    [Fact]
    public void Build_ShouldDeduplicateIgnoringCase()
    {
        var text = NameGuidancePromptHelper.Build(["ProjectX", "projectx", "ProjectY"], []);

        Assert.Contains("常用名詞：ProjectX、ProjectY", text);
    }

    [Fact]
    public void Build_ShouldPreserveOriginalOrder()
    {
        var text = NameGuidancePromptHelper.Build([], ["陳大文", "王小明", "李小華"]);

        Assert.Contains("與會人員：陳大文、王小明、李小華", text);
    }

    #endregion

    [Fact]
    public void ReduceReminder_ShouldPointBackToTheListAtTheTop()
    {
        Assert.Contains("開頭名單", NameGuidancePromptHelper.ReduceReminder);
    }
}
