namespace TemporalGraph.Data.Tests;

// The EDTF subset the story-time pass produces, and the derived _lo / _hi bounds.
public class EdtfTests
{
    [Theory]
    [InlineData("1850", "1850-01-01", "1850-12-31")]
    [InlineData("1850-03", "1850-03-01", "1850-03-31")]
    [InlineData("1852-02", "1852-02-01", "1852-02-29")]        // leap year
    [InlineData("1850-03-12", "1850-03-12", "1850-03-12")]
    [InlineData("185X", "1850-01-01", "1859-12-31")]
    [InlineData("18XX", "1800-01-01", "1899-12-31")]
    [InlineData("1850-XX", "1850-01-01", "1850-12-31")]
    [InlineData("1850-03-XX", "1850-03-01", "1850-03-31")]
    [InlineData("1850-XX-XX", "1850-01-01", "1850-12-31")]
    [InlineData("1850-21", "1850-03-01", "1850-05-31")]        // spring
    [InlineData("1850-23", "1850-09-01", "1850-11-30")]        // autumn
    [InlineData("1851-24", "1851-12-01", "1852-02-29")]        // winter runs into the next year
    [InlineData("1850~", "1850-01-01", "1850-12-31")]          // qualifiers don't widen the bounds
    [InlineData("1850-03?", "1850-03-01", "1850-03-31")]
    [InlineData("1850%", "1850-01-01", "1850-12-31")]
    public void Dates_give_the_first_and_last_day_they_can_mean(string text, string lo, string hi)
    {
        var v = Edtf.Parse(text);
        Assert.Equal(lo, v.LoIso);
        Assert.Equal(hi, v.HiIso);
        Assert.False(v.Interval);
    }

    [Theory]
    [InlineData("1848/1852", "1848-01-01", "1852-12-31")]
    [InlineData("1850-03/1850-05-10", "1850-03-01", "1850-05-10")]
    [InlineData("1850~/1852?", "1850-01-01", "1852-12-31")]
    [InlineData("1850/..", "1850-01-01", null)]
    [InlineData("../1850", null, "1850-12-31")]
    [InlineData("1850/", "1850-01-01", null)]
    [InlineData("/1850", null, "1850-12-31")]
    [InlineData("1850-03/1850-03", "1850-03-01", "1850-03-31")]
    public void Intervals_take_the_start_lo_and_the_end_hi(string text, string? lo, string? hi)
    {
        var v = Edtf.Parse(text);
        Assert.True(v.Interval);
        Assert.Equal(lo, v.LoIso);
        Assert.Equal(hi, v.HiIso);
    }

    [Theory]
    [InlineData("XXXX-03-12")]
    [InlineData("XXXX-02-29")]
    [InlineData("XXXX-03")]
    [InlineData("XXXX-23")]
    public void A_known_month_or_day_in_an_unknown_year_is_valid_with_no_bounds(string text)
    {
        var v = Edtf.Parse(text);
        Assert.Null(v.Lo);
        Assert.Null(v.Hi);
    }

    [Fact]
    public void Qualifiers_are_recorded()
    {
        Assert.True(Edtf.Parse("1850~").Approximate);
        Assert.False(Edtf.Parse("1850~").Uncertain);
        Assert.True(Edtf.Parse("1850?").Uncertain);
        Assert.True(Edtf.Parse("1850%").Approximate && Edtf.Parse("1850%").Uncertain);
        Assert.True(Edtf.Parse("1848/1850~").Approximate);
        Assert.False(Edtf.Parse("1850").Approximate || Edtf.Parse("1850").Uncertain);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 1850")]
    [InlineData("1850 ")]
    [InlineData("50")]
    [InlineData("18500")]
    [InlineData("-1850")]
    [InlineData("Y18500")]
    [InlineData("1850-13")]
    [InlineData("1850-00")]
    [InlineData("1850-25")]              // level 2 seasons
    [InlineData("1850-02-30")]
    [InlineData("1851-02-29")]           // not a leap year
    [InlineData("1850-21-01")]           // a season with a day
    [InlineData("1X50")]                 // X inside the year
    [InlineData("185X-03")]              // partly unknown year with a month
    [InlineData("1850-X3")]
    [InlineData("1850-XX-12")]           // known day, unknown month
    [InlineData("XXXX")]
    [InlineData("XXXX-XX")]
    [InlineData("1850~?")]
    [InlineData("?1850")]
    [InlineData("1850-?03")]             // level 2 group qualifier
    [InlineData("1852/1848")]            // start after end
    [InlineData("1850/1849-12")]
    [InlineData("/")]
    [InlineData("../..")]
    [InlineData("1850/1851/1852")]
    [InlineData("[1850,1851]")]          // sets
    [InlineData("1850-03-12T10:00:00")]
    [InlineData("about 1850")]
    [InlineData("0000")]
    public void Anything_outside_the_subset_is_rejected_with_a_reason(string text)
    {
        Assert.False(Edtf.TryParse(text, out var v, out var error));
        Assert.Null(v);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<FormatException>(() => Edtf.Parse(text));
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.False(Edtf.TryParse(null, out _, out var error));
        Assert.Equal("empty", error);
    }
}
