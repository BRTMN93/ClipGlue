using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipGlue.Models;

/// <summary>One subtitle cue: active during [Start, End) seconds.</summary>
public readonly record struct SubtitleCue(double Start, double End, string Text);

/// <summary>
/// Minimal SRT/ASS subtitle parsing for the preview player's subtitle
/// overlay - just enough to answer "what text is showing at time t", not a
/// general-purpose subtitle library. Direct port of
/// clipglue/subtitle_utils.py.
/// </summary>
public static partial class SubtitleUtils
{
    [GeneratedRegex(@"(\d+):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d+):(\d{2}):(\d{2})[,.](\d{3})")]
    private static partial Regex SrtTimeRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssOverrideRegex();

    private static double SrtTimestampToSeconds(string h, string m, string s, string ms) =>
        int.Parse(h, CultureInfo.InvariantCulture) * 3600
        + int.Parse(m, CultureInfo.InvariantCulture) * 60
        + int.Parse(s, CultureInfo.InvariantCulture)
        + int.Parse(ms, CultureInfo.InvariantCulture) / 1000.0;

    /// <summary>
    /// Parses SRT text into cues sorted by start time. Malformed blocks are
    /// skipped rather than raising - this only ever feeds a best-effort
    /// preview overlay.
    /// </summary>
    public static List<SubtitleCue> ParseSrt(string text)
    {
        var cues = new List<SubtitleCue>();
        var blocks = Regex.Split(text.Trim(), @"\r?\n\r?\n+");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Trim().Length != 0)
                .ToList();
            if (lines.Count == 0) continue;

            Match? m = null;
            foreach (var line in lines)
            {
                var candidate = SrtTimeRegex().Match(line);
                if (candidate.Success) { m = candidate; break; }
            }
            if (m is null) continue;

            int timeLineIdx = lines.FindIndex(l => SrtTimeRegex().IsMatch(l));
            double start = SrtTimestampToSeconds(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
            double end = SrtTimestampToSeconds(m.Groups[5].Value, m.Groups[6].Value, m.Groups[7].Value, m.Groups[8].Value);
            var content = string.Join("\n", lines.Skip(timeLineIdx + 1)).Trim();
            if (content.Length > 0 && end > start)
                cues.Add(new SubtitleCue(start, end, content));
        }
        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    private static double AssTimestampToSeconds(string t)
    {
        // H:MM:SS.cc (centiseconds, 1-digit hour is normal in ASS)
        var parts = t.Split(':');
        var h = parts[0];
        var m = parts[1];
        var rest = parts[2].Split('.');
        var s = rest[0];
        var cs = rest[1];
        return int.Parse(h, CultureInfo.InvariantCulture) * 3600
            + int.Parse(m, CultureInfo.InvariantCulture) * 60
            + int.Parse(s, CultureInfo.InvariantCulture)
            + int.Parse(cs, CultureInfo.InvariantCulture) / 100.0;
    }

    private static string CleanAssText(string raw)
    {
        var text = AssOverrideRegex().Replace(raw, "");
        text = text.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " ");
        return text.Trim();
    }

    /// <summary>
    /// Parses the [Events]/Dialogue: lines of an ASS/SSA file into cues
    /// sorted by start time. Override tags ({\...}) are stripped and
    /// \N/\n/\h are turned into plain whitespace - good enough for a
    /// plain-text preview overlay, not a faithful re-render of
    /// styled/positioned subtitles.
    /// </summary>
    public static List<SubtitleCue> ParseAss(string text)
    {
        var cues = new List<SubtitleCue>();
        bool inEvents = false;
        int? textFieldIndex = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var stripped = line.Trim();
            if (stripped.Length == 0) continue;

            if (stripped.StartsWith('['))
            {
                inEvents = stripped.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents) continue;

            if (stripped.StartsWith("format:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = stripped["format:".Length..]
                    .Split(',').Select(f => f.Trim().ToLowerInvariant()).ToList();
                var idx = fields.IndexOf("text");
                if (idx >= 0) textFieldIndex = idx;
                continue;
            }
            if (!stripped.StartsWith("dialogue:", StringComparison.OrdinalIgnoreCase)) continue;

            // Standard v4+ layout: Layer,Start,End,Style,Name,MarginL,MarginR,
            // MarginV,Effect,Text - 9 fields before Text.
            int fieldIdx = textFieldIndex ?? 9;
            var rest = stripped["dialogue:".Length..].Trim();
            var parts = rest.Split(',', fieldIdx + 1);
            if (parts.Length <= fieldIdx) continue;

            try
            {
                double start = AssTimestampToSeconds(parts[1].Trim());
                double end = AssTimestampToSeconds(parts[2].Trim());
                var content = CleanAssText(parts[fieldIdx]);
                if (content.Length > 0 && end > start)
                    cues.Add(new SubtitleCue(start, end, content));
            }
            catch (Exception e) when (e is FormatException or IndexOutOfRangeException)
            {
                continue;
            }
        }
        cues.Sort((a, b) => a.Start.CompareTo(b.Start));
        return cues;
    }

    /// <summary>
    /// Returns the text of the cue active at time t, or null. <paramref name="cues"/>
    /// must already be sorted by start time (both ParseSrt/ParseAss guarantee this).
    /// </summary>
    public static string? ActiveCueText(List<SubtitleCue> cues, double t)
    {
        foreach (var (start, end, content) in cues)
        {
            if (start <= t && t < end) return content;
            if (start > t) break;
        }
        return null;
    }
}
