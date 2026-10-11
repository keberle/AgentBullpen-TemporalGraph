using System.Globalization;
using System.Text.RegularExpressions;

namespace TemporalGraph.Data;

/// <summary>
/// A parsed EDTF string (Library of Congress, Extended Date/Time Format, https://www.loc.gov/standards/datetime/):
/// <paramref name="Lo"/> is the earliest day and <paramref name="Hi"/> the latest day it can mean; null where the text
/// gives no bound (an unspecified year <c>XXXX</c>, an open <c>..</c> or unknown end of an interval).
/// The <c>~</c> (approximate), <c>?</c> (uncertain) and <c>%</c> (both) qualifiers are recorded but don't widen the bounds.
/// </summary>
public sealed record EdtfValue(string Text, DateOnly? Lo, DateOnly? Hi, bool Interval, bool Approximate, bool Uncertain)
{
    /// <summary>ISO-8601 calendar date (yyyy-MM-dd), the form stored in the <c>_lo</c>/<c>_hi</c> columns.</summary>
    public static string? Iso(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string? LoIso => Iso(Lo);
    public string? HiIso => Iso(Hi);
}

/// <summary>
/// Parser and validator for the EDTF subset the story-time pass produces (there is no maintained .NET library).
/// Accepted:
/// <list type="bullet">
/// <item>Level 0 dates: <c>YYYY</c>, <c>YYYY-MM</c>, <c>YYYY-MM-DD</c> (years 0000–9999; real calendar dates).</item>
/// <item>Level 1 unspecified digits: trailing <c>X</c> in the year (<c>188X</c>, <c>18XX</c>, <c>XXXX</c>), and <c>XX</c>
/// for the month or day (<c>1889-XX</c>, <c>1889-06-XX</c>, <c>1889-XX-XX</c>). Also <c>XXXX-MM</c> and
/// <c>XXXX-MM-DD</c> (a known month or day in an unknown year; level 2 in the standard), whose bounds are null.</item>
/// <item>Level 1 seasons: <c>YYYY-21</c> spring, <c>-22</c> summer, <c>-23</c> autumn, <c>-24</c> winter. Bounds use
/// northern meteorological seasons: Mar–May, Jun–Aug, Sep–Nov, and Dec of YYYY to Feb of YYYY+1.</item>
/// <item>Level 1 qualifier at the end of a date: <c>~</c>, <c>?</c> or <c>%</c>.</item>
/// <item>Level 0/1 intervals <c>A/B</c>: each side a date as above, <c>..</c> (open) or empty (unknown), not both
/// missing. The start must not be after the end. Lo = the start's Lo, Hi = the end's Hi.</item>
/// </list>
/// Everything else (negative or Y-prefixed years, sets, level 2 group qualifiers, times) is rejected.
/// </summary>
public static partial class Edtf
{
    [GeneratedRegex(@"\A(?<y>[0-9X]{4})(?:-(?<m>[0-9X]{2})(?:-(?<d>[0-9X]{2}))?)?(?<q>[~?%])?\z")]
    private static partial Regex DateForm();

    /// <summary>
    /// A canonical form for comparing two EDTF strings (voting, scoring): trailing unknown parts are dropped from each date
    /// (<c>1884-XX-XX~</c> → <c>1884~</c>, <c>1884-05-XX</c> → <c>1884-05</c>, <c>XXXX-05-XX</c> → <c>XXXX-05</c>), and each
    /// side of an interval is treated alike. Returns the input unchanged when it isn't valid, or when the result wouldn't be.
    /// </summary>
    public static string Normalize(string text)
    {
        if (!TryParse(text, out _, out _)) return text;
        static string Side(string side)
        {
            if (side is "" or "..") return side;
            var q = side[^1] is '~' or '?' or '%' ? side[^1].ToString() : "";
            var parts = side[..^q.Length].Split('-').ToList();
            while (parts.Count > 1 && parts[^1] == "XX") parts.RemoveAt(parts.Count - 1);
            return string.Join("-", parts) + q;
        }
        var normalized = string.Join("/", text.Split('/').Select(Side));
        return TryParse(normalized, out _, out _) ? normalized : text;
    }

    public static EdtfValue Parse(string text) =>
        TryParse(text, out var value, out var error) ? value! : throw new FormatException($"Invalid EDTF '{text}': {error}");

    public static bool TryParse(string? text, out EdtfValue? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "empty"; return false; }
        if (text != text.Trim()) { error = "leading or trailing whitespace"; return false; }

        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            if (!TryDate(text, out var d, out error)) return false;
            value = new EdtfValue(text, d.Lo, d.Hi, false, d.Approximate, d.Uncertain);
            return true;
        }
        if (text.IndexOf('/', slash + 1) >= 0) { error = "more than one '/'"; return false; }
        var (startText, endText) = (text[..slash], text[(slash + 1)..]);
        if (startText is "" or ".." && endText is "" or "..") { error = "an interval needs at least one date"; return false; }

        Side? start = null, end = null;
        if (startText is not ("" or ".."))
        {
            if (!TryDate(startText, out var s, out error)) { error = "start: " + error; return false; }
            start = s;
        }
        if (endText is not ("" or ".."))
        {
            if (!TryDate(endText, out var e, out error)) { error = "end: " + error; return false; }
            end = e;
        }
        if (start?.Lo is { } a && end?.Lo is { } b && a > b) { error = "the start is after the end"; return false; }
        value = new EdtfValue(text, start?.Lo, end?.Hi, true,
            (start?.Approximate ?? false) || (end?.Approximate ?? false), (start?.Uncertain ?? false) || (end?.Uncertain ?? false));
        return true;
    }

    private sealed record Side(DateOnly? Lo, DateOnly? Hi, bool Approximate, bool Uncertain);

    private static bool TryDate(string text, out Side side, out string? error)
    {
        side = null!;
        error = null;
        var m = DateForm().Match(text);
        if (!m.Success) { error = "not a supported EDTF date (YYYY, YYYY-MM, YYYY-MM-DD, X digits, seasons 21–24, one trailing ~ ? %)"; return false; }
        var (y, mo, d, q) = (m.Groups["y"].Value, m.Groups["m"].Value, m.Groups["d"].Value, m.Groups["q"].Value);
        var approximate = q is "~" or "%";
        var uncertain = q is "?" or "%";

        // Year: digits followed by X only (188X, 18XX, XXXX); X inside the digits (1X89) is level 2.
        var xs = y.Length - y.TrimEnd('X').Length;
        if (y.TrimEnd('X').Contains('X')) { error = $"year '{y}': X digits must be at the end"; return false; }
        var yearUnknown = xs == 4;

        if (mo.Length == 0)
        {
            if (yearUnknown) { error = "'XXXX' alone says nothing"; return false; }
            if (y == "0000") { error = "year 0000 isn't supported"; return false; }
            var (lo, hi) = YearRange(y, xs);
            side = new Side(lo, hi, approximate, uncertain);
            return true;
        }

        if (mo.Contains('X') && mo != "XX") { error = $"month '{mo}': use XX or two digits"; return false; }
        if (d.Length > 0 && d.Contains('X') && d != "XX") { error = $"day '{d}': use XX or two digits"; return false; }
        if (xs > 0 && !yearUnknown) { error = $"'{y}' with a month or day: only a fully unknown year (XXXX) may have one"; return false; }

        if (mo == "XX")
        {
            if (d.Length > 0 && d != "XX") { error = "a known day with an unknown month"; return false; }
            if (yearUnknown) { error = "'XXXX-XX' says nothing"; return false; }
            if (y == "0000") { error = "year 0000 isn't supported"; return false; }
            var (lo, hi) = YearRange(y, 0);
            side = new Side(lo, hi, approximate, uncertain);
            return true;
        }

        var month = int.Parse(mo, CultureInfo.InvariantCulture);
        if (month is >= 21 and <= 24)
        {
            if (d.Length > 0) { error = "a season can't have a day"; return false; }
            if (yearUnknown) { side = new Side(null, null, approximate, uncertain); return true; }
            var year = int.Parse(y, CultureInfo.InvariantCulture);
            var (lo, hi) = Season(year, month);
            if (lo is null) { error = "season out of range"; return false; }
            side = new Side(lo, hi, approximate, uncertain);
            return true;
        }
        if (month is < 1 or > 12) { error = $"month '{mo}' out of range (01–12, or seasons 21–24)"; return false; }

        // A known month (and day) in an unknown year: valid, no bounds. Day 29 of February is allowed (some leap year).
        if (yearUnknown)
        {
            if (d.Length > 0 && d != "XX")
            {
                var day = int.Parse(d, CultureInfo.InvariantCulture);
                if (day < 1 || day > DateTime.DaysInMonth(2000, month)) { error = $"day '{d}' out of range for month {month}"; return false; }
            }
            side = new Side(null, null, approximate, uncertain);
            return true;
        }

        var yy = int.Parse(y, CultureInfo.InvariantCulture);
        if (yy < 1) { error = "year 0000 isn't supported"; return false; }
        var days = DateTime.DaysInMonth(yy, month);
        if (d.Length == 0 || d == "XX")
        {
            side = new Side(new DateOnly(yy, month, 1), new DateOnly(yy, month, days), approximate, uncertain);
            return true;
        }
        var dd = int.Parse(d, CultureInfo.InvariantCulture);
        if (dd < 1 || dd > days) { error = $"day '{d}' out of range for {yy}-{mo}"; return false; }
        var date = new DateOnly(yy, month, dd);
        side = new Side(date, date, approximate, uncertain);
        return true;
    }

    private static (DateOnly Lo, DateOnly Hi) YearRange(string y, int xs)
    {
        var digits = y[..(4 - xs)];
        var step = (int)Math.Pow(10, xs);
        var first = (digits.Length == 0 ? 0 : int.Parse(digits, CultureInfo.InvariantCulture)) * step;
        var last = first + step - 1;
        return (new DateOnly(Math.Max(first, 1), 1, 1), new DateOnly(last, 12, 31));
    }

    private static (DateOnly? Lo, DateOnly? Hi) Season(int year, int season)
    {
        if (year < 1 || (season == 24 && year >= 9999)) return (null, null);
        return season switch
        {
            21 => (new DateOnly(year, 3, 1), new DateOnly(year, 5, 31)),
            22 => (new DateOnly(year, 6, 1), new DateOnly(year, 8, 31)),
            23 => (new DateOnly(year, 9, 1), new DateOnly(year, 11, 30)),
            _ => (new DateOnly(year, 12, 1), new DateOnly(year + 1, 2, DateTime.DaysInMonth(year + 1, 2))),
        };
    }
}
