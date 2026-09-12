using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipGlue.Models;

/// <summary>
/// Pure time-range parsing/formatting helpers - a direct port of
/// clipglue/time_utils.py. No ffmpeg, no UI dependency.
/// </summary>
public static partial class TimeUtils
{
    /// <summary>Converts "MM:SS:ff" -&gt; number of seconds.</summary>
    public static double ParseTime(string t)
    {
        var parts = t.Trim().Split(':');
        if (parts.Length != 3)
            throw new FormatException($"Invalid time format: '{t}'. Expected MM:SS:ff");
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int frac))
            throw new FormatException($"Invalid time format: '{t}'. Expected MM:SS:ff");
        return minutes * 60 + seconds + frac / 100.0;
    }

    /// <summary>Converts seconds -&gt; "MM:SS:ff", the exact inverse of ParseTime.</summary>
    public static string FormatTime(double seconds)
    {
        long totalHundredths = (long)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        long minutes = Math.DivRem(totalHundredths, 6000, out long rem);
        long secs = Math.DivRem(rem, 100, out long frac);
        return $"{minutes:D2}:{secs:D2}:{frac:D2}";
    }

    // The dash is KEPT by this filter on purpose. It used to be stripped and
    // re-inserted by digit count - six digits of start, six of end - which is
    // only correct while minutes fit in two digits: past 99:59 that split
    // landed inside the minutes field and silently rewrote the range
    // ("125:30:00-140:15:50" came back as "12:53:00-01:40:15", end before
    // start). Now that the user types the dash and it survives the round trip,
    // each segment is split where the user put it and either half may carry as
    // many minute digits as it needs.
    [GeneratedRegex("[^0-9,-]")]
    private static partial Regex NonTimeCharRegex();

    [GeneratedRegex("[^0-9]")]
    private static partial Regex NonDigitRegex();

    /// <summary>
    /// Upper bound on the digits of one time value: six minute digits
    /// (999999:59:99, roughly 694 days) plus seconds and hundredths. Nothing
    /// real reaches it - it is here only so a mashed keyboard cannot build a
    /// minutes field that overflows <see cref="int"/> in <see cref="ParseTime"/>.
    /// </summary>
    private const int MaxTimeDigits = 10;

    private static string FormatTimeBlock(string digits)
    {
        var d = digits.Length > MaxTimeDigits ? digits[..MaxTimeDigits] : digits;
        if (d.Length <= 2) return d;
        if (d.Length <= 4) return d[..2] + ":" + d[2..];
        if (d.Length <= 6) return d[..2] + ":" + d[2..4] + ":" + d[4..];
        // Past six digits this is no longer "MM:SS:ff being filled in left to
        // right" but a real time longer than 99:59:99, so seconds and
        // hundredths are the LAST four digits and everything before them is
        // minutes ("1253000" -> "125:30:00"). At exactly six digits both
        // readings agree, so the switch is invisible while typing.
        return d[..^4] + ":" + d[^4..^2] + ":" + d[^2..];
    }

    private static string FormatRangeSegment(string segment)
    {
        int dash = segment.IndexOf('-');
        if (dash < 0) return FormatTimeBlock(NonDigitRegex().Replace(segment, ""));
        // The dash is written out even when one side is still empty: it appears
        // the moment the user types it, and swallowing it back would make the
        // separator impossible to enter by hand.
        return FormatTimeBlock(NonDigitRegex().Replace(segment[..dash], ""))
             + "-"
             + FormatTimeBlock(NonDigitRegex().Replace(segment[(dash + 1)..], ""));
    }

    /// <summary>
    /// Keeps only digits/commas/dashes from <paramref name="raw"/> and re-renders
    /// with "MM:SS:ff-MM:SS:ff, MM:SS:ff-MM:SS:ff ..." formatting applied
    /// automatically, so the user only ever has to type digits plus the two
    /// separators (the dash between start and end, the comma between ranges).
    /// </summary>
    public static string FormatRangesText(string raw)
    {
        var cleaned = NonTimeCharRegex().Replace(raw, "");
        var segments = cleaned.Split(',');
        return string.Join(", ", segments.Select(FormatRangeSegment));
    }

    /// <summary>
    /// The same digits-only auto-formatting as <see cref="FormatRangesText"/>,
    /// but for a field holding ONE time value. Feeding such a field the range
    /// formatter is what used to put a dash inside the inline editor's "Start"
    /// box ("125:30:00" came back as "12:53:00-0"): a comma or a dash is not a
    /// separator here, it is simply not part of a single time.
    /// </summary>
    public static string FormatTimeText(string raw)
        => FormatTimeBlock(NonDigitRegex().Replace(raw, ""));
}
