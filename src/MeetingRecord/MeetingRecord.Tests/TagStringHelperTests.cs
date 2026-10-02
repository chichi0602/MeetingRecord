using MeetingRecord.Business.Helpers;

namespace MeetingRecord.Tests;

public sealed class TagStringHelperTests
{
    private sealed class Row
    {
        public string? Teams { get; set; }
    }

    [Fact]
    public void ToStored_ThenToList_ShouldRoundTrip()
    {
        var stored = TagStringHelper.ToStored(["分類A", "分類B"]);
        var list = TagStringHelper.ToList(stored);

        Assert.Equal(["分類A", "分類B"], list);
    }

    [Fact]
    public void ToStored_ShouldTrimDeduplicateAndDropBlanks()
    {
        var stored = TagStringHelper.ToStored([" A ", "A", "", "  ", "B"]);
        var list = TagStringHelper.ToList(stored);

        Assert.Equal(["A", "B"], list);
    }

    [Fact]
    public void ToStored_WithNoValidValues_ShouldReturnNull()
    {
        Assert.Null(TagStringHelper.ToStored([]));
        Assert.Null(TagStringHelper.ToStored(["", "   "]));
        Assert.Null(TagStringHelper.ToStored(null));
    }

    [Fact]
    public void BuildContainsAnyPredicate_ShouldMatchExactMemberOnly()
    {
        List<Row> rows =
        [
            new() { Teams = TagStringHelper.ToStored(["團隊A"]) },
            new() { Teams = TagStringHelper.ToStored(["團隊AB"]) },
            new() { Teams = TagStringHelper.ToStored(["團隊B"]) },
            new() { Teams = null },
        ];

        var predicate = TagStringHelper.BuildContainsAnyPredicate<Row>(x => x.Teams, ["團隊A"]).Compile();
        var matched = rows.Where(predicate).ToList();

        Assert.Single(matched);
        Assert.Equal(TagStringHelper.ToStored(["團隊A"]), matched[0].Teams);
    }

    [Fact]
    public void BuildContainsAnyPredicate_WithEmptyValues_ShouldMatchAll()
    {
        var rows = new List<Row>
        {
            new() { Teams = TagStringHelper.ToStored(["A"]) },
            new() { Teams = null },
        };

        var predicate = TagStringHelper.BuildContainsAnyPredicate<Row>(x => x.Teams, []).Compile();

        Assert.Equal(2, rows.Where(predicate).Count());
    }
}
