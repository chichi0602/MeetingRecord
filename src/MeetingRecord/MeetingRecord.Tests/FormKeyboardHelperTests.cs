using Microsoft.AspNetCore.Components.Web;
using MeetingRecord.Web.Components.Commons;

namespace MeetingRecord.Tests;

/// <summary>
/// 表單鍵盤判斷的單元測試。
///
/// <para>
/// ⚠️ 這裡測得到的只有「規則本身」。真正會出事的是 Blazor Server 的輸入法時序，
/// 而 <c>開發慣例與限制速查</c> §6.8 明寫「這個症狀在本機無頭瀏覽器上重現不出來（延遲太低）」——
/// 所以**實際用注音打字的行為只能由使用者試**，這份測試不能當成驗收。
/// </para>
/// </summary>
public sealed class FormKeyboardHelperTests
{
    [Fact]
    public void IsSubmit_PlainEnter_ShouldBeTrue()
    {
        Assert.True(FormKeyboardHelper.IsSubmit(new KeyboardEventArgs { Key = "Enter" }));
    }

    [Fact]
    public void IsSubmit_WhileComposing_ShouldBeFalse()
    {
        // 這是整組測試裡最重要的一筆：中文輸入法用 Enter 確認候選字，
        // 沒有這道防護的話，使用者打「會議」按 Enter 選字就會把對話框存檔關閉。
        var args = new KeyboardEventArgs { Key = "Enter", IsComposing = true };

        Assert.False(FormKeyboardHelper.IsSubmit(args));
    }

    [Fact]
    public void IsSubmit_ShiftEnter_ShouldBeFalse()
    {
        // Shift+Enter 是多行欄位的換行鍵。這裡回 false，瀏覽器的原生行為就會換行。
        Assert.False(FormKeyboardHelper.IsSubmit(new KeyboardEventArgs { Key = "Enter", ShiftKey = true }));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void IsSubmit_WithModifier_ShouldBeFalse(bool ctrl, bool alt, bool meta)
    {
        var args = new KeyboardEventArgs
        {
            Key = "Enter",
            CtrlKey = ctrl,
            AltKey = alt,
            MetaKey = meta,
        };

        Assert.False(FormKeyboardHelper.IsSubmit(args));
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("a")]
    [InlineData("Tab")]
    [InlineData("NumpadEnter")]
    public void IsSubmit_OtherKeys_ShouldBeFalse(string key)
    {
        Assert.False(FormKeyboardHelper.IsSubmit(new KeyboardEventArgs { Key = key }));
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("Esc")]
    public void IsCancel_ShouldAcceptBothSpellings(string key)
    {
        // 不同瀏覽器回報的字串不一致，兩種都要收。
        Assert.True(FormKeyboardHelper.IsCancel(new KeyboardEventArgs { Key = key }));
    }

    [Fact]
    public void IsCancel_Enter_ShouldBeFalse()
    {
        Assert.False(FormKeyboardHelper.IsCancel(new KeyboardEventArgs { Key = "Enter" }));
    }

    [Fact]
    public void IsSubmit_NullArgs_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => FormKeyboardHelper.IsSubmit(null!));
    }

    #region IsReverseSubmit（搜尋框的「上一筆」，0.4.82）

    [Fact]
    public void IsReverseSubmit_ShiftEnter_ShouldBeTrue()
    {
        Assert.True(FormKeyboardHelper.IsReverseSubmit(
            new KeyboardEventArgs { Key = "Enter", ShiftKey = true }));
    }

    [Fact]
    public void IsReverseSubmit_WhileComposing_ShouldBeFalse()
    {
        // 中文輸入法組字期間的 Shift+Enter 是在選字，不是在找上一筆。
        // 這是 0.4.77 花了一整版修的東西，新元件不能倒退回去。
        Assert.False(FormKeyboardHelper.IsReverseSubmit(
            new KeyboardEventArgs { Key = "Enter", ShiftKey = true, IsComposing = true }));
    }

    [Fact]
    public void IsReverseSubmit_PlainEnter_ShouldBeFalse()
    {
        // 純 Enter 是「下一筆」，由 IsSubmit 負責。兩支不可以同時回 true，
        // 否則按一次 Enter 會前進又後退，看起來像卡住不動。
        var args = new KeyboardEventArgs { Key = "Enter" };

        Assert.True(FormKeyboardHelper.IsSubmit(args));
        Assert.False(FormKeyboardHelper.IsReverseSubmit(args));
    }

    [Theory]
    [InlineData("Escape")]
    [InlineData("a")]
    [InlineData("Tab")]
    public void IsReverseSubmit_OtherKeys_ShouldBeFalse(string key)
    {
        Assert.False(FormKeyboardHelper.IsReverseSubmit(
            new KeyboardEventArgs { Key = key, ShiftKey = true }));
    }

    [Fact]
    public void IsReverseSubmit_NullArgs_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => FormKeyboardHelper.IsReverseSubmit(null!));
    }

    #endregion
}
