using MeetingRecord.Web.Diagnostics;

namespace MeetingRecord.Tests;

/// <summary>錯誤追蹤碼。</summary>
public sealed class TraceCodeTests
{
    [Fact]
    public void New_ShouldBeEightUnambiguousCharacters()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => TraceCode.New()).ToList();

        Assert.All(codes, code => Assert.Matches("^[0-9A-HJKMNP-TV-Z]{8}$", code));
        Assert.True(codes.Distinct().Count() > 490);
    }

    [Fact]
    public void Begin_ShouldExposeCurrent_UntilDisposed()
    {
        Assert.Null(TraceCode.Current);

        using (TraceCode.Begin("K7Q2M9XA"))
        {
            Assert.Equal("K7Q2M9XA", TraceCode.Current);
        }

        Assert.Null(TraceCode.Current);
    }

    [Fact]
    public void Attach_ShouldCarryCurrentCodeOnTheException()
    {
        var exception = new InvalidOperationException("boom");

        using (TraceCode.Begin("K7Q2M9XA"))
        {
            TraceCode.Attach(exception);
        }

        Assert.Equal("K7Q2M9XA", TraceCode.FromException(exception));
    }

    [Fact]
    public void Suffix_ShouldBeEmptyWithoutCode()
    {
        Assert.Equal(string.Empty, TraceCode.Suffix(null));
        Assert.Equal("（錯誤追蹤碼：K7Q2M9XA）", TraceCode.Suffix("K7Q2M9XA"));
    }
}
