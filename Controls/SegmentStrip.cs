using System.Windows;
using System.Windows.Media;
using ClipGlue.Models;

namespace ClipGlue.Controls;

/// <summary>
/// A picture of what the job will keep and what it will throw away, drawn on
/// the SOURCE timelines rather than the output one.
///
/// This is the piece <see cref="ProjectScrubber"/> cannot show. That bar's
/// axis is the finished clip, where by definition everything is kept, so the
/// cuts are invisible on it - they are the joins between its blocks. Here
/// every input file gets a slice of the width proportional to its own
/// duration, its kept ranges are painted in the information accent and
/// everything else stays at the empty-trough color, so a glance answers "how
/// much of each episode survives, and where from".
///
/// <para><b>Durations arrive late, and the strip is honest about it.</b>
/// Until a file has been probed its extent is unknown, and the only thing
/// derivable from the ranges alone is where the last kept moment is. Drawing
/// that as if it were the whole file would hide exactly the cut Bartek makes
/// most often - the trailing one - so an unprobed file is drawn with a
/// hatched tail and its slice widens once the real duration lands.</para>
/// </summary>
public sealed class SegmentStrip : FrameworkElement
{
    public const double BarH = 12;

    private const double FileGap = 3;
    /// <summary>How much slice an unprobed file gets past its last kept
    /// moment, as a fraction, purely so the "unknown" hatch has room.</summary>
    internal const double UnknownTail = 0.12;
    private const double Radius = 3;

    /// <summary>One input file: its ranges in source time, and its duration
    /// when known (0 until the probe comes back).</summary>
    public sealed record Entry(string Path, IReadOnlyList<TimeRange> Ranges, double Duration)
    {
        /// <summary>The last moment this file is known to reach.</summary>
        public double Accounted => Ranges.Count == 0 ? 1 : Math.Max(1, Ranges.Max(r => r.End));

        /// <summary>How wide this file's slice is. While the duration is
        /// unknown the slice is stretched past the last kept moment by
        /// <see cref="UnknownTail"/>, so there is somewhere to draw "and
        /// possibly more" - without it an unprobed file would look like one
        /// that genuinely ends on its last kept frame, which is the single
        /// most misleading thing this strip could say.</summary>
        public double Extent => Duration > 0 ? Duration : Accounted * (1 + SegmentStrip.UnknownTail);

        public bool DurationKnown => Duration > 0;
    }

    private IReadOnlyList<Entry> _entries = Array.Empty<Entry>();
    private double _totalExtent;
    private int _activeEntry = -1;
    private double _activeSourcePos;

    public SegmentStrip()
    {
        Height = BarH;
        IsHitTestVisible = false;
    }

    public void SetModel(IReadOnlyList<Entry> entries)
    {
        _entries = entries;
        _totalExtent = entries.Sum(e => e.Extent);
        InvalidateVisual();
    }

    /// <summary>Where playback currently is, in the same terms as
    /// <see cref="Entry"/>: which entry (by index into the list last passed
    /// to <see cref="SetModel"/>) is playing and how far into its source
    /// time. Entries before it are drawn as fully played, entries after it
    /// as not yet played - mirroring how <see cref="ProjectScrubber"/>
    /// shades the output timeline, just on this bar's per-file axis instead.
    /// -1 means nothing is playing (nothing is drawn as played).</summary>
    public void SetProgress(int activeEntry, double sourcePos)
    {
        if (_activeEntry == activeEntry && Math.Abs(_activeSourcePos - sourcePos) < 0.02) return;
        _activeEntry = activeEntry;
        _activeSourcePos = sourcePos;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 0) return;

        if (_entries.Count == 0 || _totalExtent <= 0)
        {
            // Nothing to show yet (no files, or none with a kept range) - but
            // an OnRender that draws nothing here leaves a hole in the layout
            // where a bar should be, breaking the visual tie to the scrubber
            // above it (which always draws its track, even empty). Paint the
            // same "cut" base color the real strip uses for its unkept
            // stretches, at the same size, so the empty state reads as "this
            // bar has nothing in it yet" rather than "this bar is missing".
            dc.DrawRoundedRectangle(Theme.BgCardHl2Brush, null, new Rect(0, 0, w, h), Radius, Radius);
            return;
        }

        double gaps = FileGap * Math.Max(0, _entries.Count - 1);
        double usable = Math.Max(1, w - gaps);
        double x = 0;

        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            double slice = usable * (entry.Extent / _totalExtent);
            var box = new Rect(x, 0, slice, h);

            // The cut base: everything that is not kept.
            dc.DrawRoundedRectangle(Theme.BgCardHl2Brush, null, box, Radius, Radius);

            dc.PushClip(new RectangleGeometry(box, Radius, Radius));
            // How far playback has reached into THIS entry's source time, as a
            // fraction of its extent: a full file to the left of whichever one
            // is currently playing counts as entirely played, one to the right
            // as not played at all - same convention ProjectScrubber uses on
            // the output axis, just walked per source file here instead.
            double playedFrac = _activeEntry < 0 ? 0
                : i < _activeEntry ? 1
                : i > _activeEntry ? 0
                : Math.Clamp(_activeSourcePos / entry.Extent, 0, 1);
            double playedX = x + slice * playedFrac;
            foreach (var range in entry.Ranges)
            {
                if (range.Length <= 0) continue;
                double a = x + slice * Math.Clamp(range.Start / entry.Extent, 0, 1);
                double b = x + slice * Math.Clamp(range.End / entry.Extent, 0, 1);
                if (b - a < 1) b = a + 1;
                dc.DrawRectangle(Theme.KeptSegmentBrush, null, new Rect(a, 0, b - a, h));
                // The played slice of this range, drawn over the kept fill in
                // the same accent color ProjectScrubber uses for progress -
                // only ever within a kept band, since nothing else ever plays.
                double playedB = Math.Min(b, playedX);
                if (playedB > a)
                    dc.DrawRectangle(Theme.FgAccentBrush, null, new Rect(a, 0, playedB - a, h));
            }

            // Unprobed: only the TAIL is a question. Hatching the whole slice
            // (the first attempt) turned the strip into noise and said the
            // wrong thing anyway - the kept bands themselves are known.
            if (!entry.DurationKnown)
            {
                double tailStart = x + slice * (1 / (1 + UnknownTail));
                DrawHatch(dc, new Rect(tailStart, 0, x + slice - tailStart, h));
            }
            dc.Pop();

            x += slice + FileGap;
        }
    }

    /// <summary>Diagonal ticks, spaced so they read as "unknown" without
    /// competing with the kept bands next to them.</summary>
    private static void DrawHatch(DrawingContext dc, Rect box)
    {
        var pen = new Pen(Theme.BorderBrush, 1);
        pen.Freeze();
        for (double i = box.Left - box.Height; i < box.Right; i += 6)
            dc.DrawLine(pen, new Point(i, box.Bottom), new Point(i + box.Height, box.Top));
    }
}
