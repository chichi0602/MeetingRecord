using System.ComponentModel.DataAnnotations;
using MeetingRecord.Web.Auth;

namespace MeetingRecord.Tests;

/// <summary>
/// `CookieSettings` 以 ValidateDataAnnotations + ValidateOnStart 註冊，
/// 設定寫壞時是**啟動就失敗**而不是執行期才出事。這裡釘住邊界。
/// </summary>
public sealed class CookieSettingsTests
{
    private static IReadOnlyList<ValidationResult> Validate(CookieSettings settings)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
        return results;
    }

    /// <summary>
    /// 預設值必須是有效的 —— 出貨的 appsettings.json 若一開始就驗不過，服務起不來。
    /// </summary>
    [Fact]
    public void Defaults_ShouldBeValid()
    {
        var settings = new CookieSettings();

        Assert.Empty(Validate(settings));
        Assert.Equal(480, settings.ExpireMinutes);
        Assert.Equal(30, settings.RememberMeDays);
        Assert.True(settings.SlidingExpiration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(43201)]
    public void ExpireMinutes_OutOfRange_ShouldFailValidation(int minutes)
    {
        var settings = new CookieSettings { ExpireMinutes = minutes };

        Assert.Contains(Validate(settings), x => x.MemberNames.Contains(nameof(CookieSettings.ExpireMinutes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void RememberMeDays_OutOfRange_ShouldFailValidation(int days)
    {
        var settings = new CookieSettings { RememberMeDays = days };

        Assert.Contains(Validate(settings), x => x.MemberNames.Contains(nameof(CookieSettings.RememberMeDays)));
    }

    /// <summary>
    /// 「記住我」的效期必須比一般登入長，否則勾了反而更短，語意就反了。
    /// 這條釘的是**出貨預設值之間的關係**，不是使用者改完之後的值。
    /// </summary>
    [Fact]
    public void RememberMe_ShouldOutliveNormalLogin_ByDefault()
    {
        var settings = new CookieSettings();

        Assert.True(TimeSpan.FromDays(settings.RememberMeDays)
            > TimeSpan.FromMinutes(settings.ExpireMinutes));
    }
}
