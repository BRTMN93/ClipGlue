using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClipGlue.Controls;

/// <summary>
/// The app's line icons, hand-drawn as vector geometry on a shared 16x16
/// box instead of borrowed from a font. The Python original had to use bare
/// Unicode glyphs (✕ ☰ ▶) because Tkinter's Canvas can only draw text or
/// primitives and an icon webfont would not survive the offline PyInstaller
/// build; WPF draws real paths, so the icons can be stroked at a consistent
/// weight, share one optical size, and pick up hover colors by simply
/// re-pointing a Brush.
///
/// Everything is authored inside the same 16x16 box and rendered with
/// <see cref="Stretch.None"/>, so the geometry coordinates ARE the device
/// -independent pixels - no scaling pass that would also scale (and blur)
/// the stroke weight.
/// </summary>
public static class Icons
{
    public const double Box = 16;
    public const double Stroke = 1.6;

    /// <summary>Delete - two crossed lines, deliberately drawn short of the
    /// box edges so it reads as an icon rather than a full-bleed X.</summary>
    public static readonly Geometry Close = Frz(Geometry.Parse("M4,4 L12,12 M12,4 L4,12"));

    /// <summary>Drag handle - a 2x3 dot grid, the conventional "grab me"
    /// affordance, replacing the original's ☰ hamburger glyph (which reads
    /// as a menu, not as a grip).</summary>
    public static readonly Geometry Grip = BuildGrip();

    /// <summary>Preview - an outline triangle, matching the outline weight
    /// of the other row icons instead of the solid ▶ glyph.</summary>
    public static readonly Geometry Play = Frz(Geometry.Parse("M5.5,3.2 L12.8,8 L5.5,12.8 Z"));

    public static readonly Geometry Plus = Frz(Geometry.Parse("M8,3.2 L8,12.8 M3.2,8 L12.8,8"));

    /// <summary>Save - a floppy outline: body with a clipped top-right
    /// corner, the shutter at the top and the label panel at the bottom.</summary>
    public static readonly Geometry Save = Frz(Geometry.Parse(
        "M2.8,2.8 L10.4,2.8 L13.2,5.6 L13.2,13.2 L2.8,13.2 Z " +
        "M5.6,2.8 L5.6,6.4 L10.4,6.4 L10.4,2.8 " +
        "M5.2,13.2 L5.2,9.4 L10.8,9.4 L10.8,13.2"));

    // -- transport ------------------------------------------------------------
    // Solid shapes, unlike the outlined row icons: transport controls are
    // small and sit on colored discs, where an outline at this size turns to
    // mush. Every bar is a closed rectangle rather than a stroked line so the
    // whole glyph can be drawn with a single Fill.

    public static readonly Geometry PlaySolid = Frz(Geometry.Parse("M4.8,3.0 L12.8,8 L4.8,13.0 Z"));

    public static readonly Geometry PauseBars = Frz(Geometry.Parse(
        "M5.2,3.2 L7.2,3.2 L7.2,12.8 L5.2,12.8 Z M8.8,3.2 L10.8,3.2 L10.8,12.8 L8.8,12.8 Z"));

    public static readonly Geometry SkipBack = Frz(Geometry.Parse(
        "M12.6,3.2 L6.3,8 L12.6,12.8 Z M3.4,3.2 L5.0,3.2 L5.0,12.8 L3.4,12.8 Z"));

    public static readonly Geometry SkipForward = Frz(Geometry.Parse(
        "M3.4,3.2 L9.7,8 L3.4,12.8 Z M11.0,3.2 L12.6,3.2 L12.6,12.8 L11.0,12.8 Z"));

    private static Geometry BuildGrip()
    {
        var g = new GeometryGroup();
        foreach (double x in new[] { 6.0, 10.0 })
            foreach (double y in new[] { 4.0, 8.0, 12.0 })
                g.Children.Add(new EllipseGeometry(new Point(x, y), 1.25, 1.25));
        return Frz(g);
    }

    /// <summary>Language switcher - a globe: outer meridian, one vertical
    /// "meridian" ellipse and the equator line, the conventional shorthand
    /// for "language" that reads at 16px without needing actual glyphs.</summary>
    public static readonly Geometry Globe = BuildGlobe();

    private static Geometry BuildGlobe()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(8, 8), 5.3, 5.3));
        g.Children.Add(new EllipseGeometry(new Point(8, 8), 2.35, 5.3));
        g.Children.Add(new LineGeometry(new Point(2.7, 8), new Point(13.3, 8)));
        return Frz(g);
    }

    /// <summary>Settings - a hub circle with eight short spokes, the same
    /// "gear" shorthand used everywhere for a settings/options entry point.
    /// Built as a loop (like <see cref="Grip"/>'s dot grid) rather than one
    /// hand-authored gear silhouette: a stroked outline of an actual
    /// toothed gear reads as clutter at 16px, where the spokes stay crisp.</summary>
    public static readonly Geometry Gear = BuildGear();

    private static Geometry BuildGear()
    {
        var g = new GeometryGroup();
        g.Children.Add(new EllipseGeometry(new Point(8, 8), 3.0, 3.0));
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            g.Children.Add(new LineGeometry(
                new Point(8 + cos * 3.0, 8 + sin * 3.0),
                new Point(8 + cos * 5.6, 8 + sin * 5.6)));
        }
        return Frz(g);
    }

    private static Geometry Frz(Geometry g)
    {
        g.Freeze();
        return g;
    }

    /// <summary>Wraps a geometry as an outlined <see cref="Path"/>.</summary>
    public static Path Stroked(Geometry geometry, Brush stroke, double thickness = Stroke)
    {
        return new Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            Width = Box,
            Height = Box,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    /// <summary>Wraps a geometry as a solid <see cref="Path"/> (the grip
    /// dots, which are filled rather than outlined).</summary>
    public static Path Filled(Geometry geometry, Brush fill)
    {
        return new Path
        {
            Data = geometry,
            Fill = fill,
            Stretch = Stretch.None,
            Width = Box,
            Height = Box,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    // -- trim-range window ------------------------------------------------------

    public static readonly Geometry Minus = Frz(Geometry.Parse("M3.2,8 L12.8,8"));

    /// <summary>Volume - cone plus one arc. A second arc reads as noise at
    /// 16px inside a pill this small.</summary>
    public static readonly Geometry Speaker = Frz(Geometry.Parse(
        "M2.8,6.2 L5.4,6.2 L8.8,3.2 L8.8,12.8 L5.4,9.8 L2.8,9.8 Z " +
        "M11.1,5.8 A3.1,3.1 0 0 1 11.1,10.2"));

    public static readonly Geometry SpeakerMuted = Frz(Geometry.Parse(
        "M2.8,6.2 L5.4,6.2 L8.8,3.2 L8.8,12.8 L5.4,9.8 L2.8,9.8 Z " +
        "M11.0,6.2 L14.0,9.8 M14.0,6.2 L11.0,9.8"));

    /// <summary>Snap-to-keyframe marker: a horseshoe magnet with its poles
    /// pointing down at the value it has stuck to.</summary>
    public static readonly Geometry Magnet = Frz(Geometry.Parse(
        "M3.8,13.0 L3.8,7.8 A4.2,4.2 0 0 1 12.2,7.8 L12.2,13.0 " +
        "M6.6,13.0 L6.6,7.8 A1.4,1.4 0 0 1 9.4,7.8 L9.4,13.0 " +
        "M3.8,10.6 L6.6,10.6 M9.4,10.6 L12.2,10.6"));

    public static readonly Geometry Pencil = Frz(Geometry.Parse(
        "M3.2,12.8 L3.2,10.4 L10.5,3.1 L12.9,5.5 L5.6,12.8 Z M8.9,4.7 L11.3,7.1"));

    public static readonly Geometry Trash = Frz(Geometry.Parse(
        "M3.4,4.6 L12.6,4.6 M6.2,4.6 L6.2,3.0 L9.8,3.0 L9.8,4.6 " +
        "M4.7,4.6 L5.3,13.0 L10.7,13.0 L11.3,4.6 M7.1,7.0 L7.3,10.7 M8.9,7.0 L8.7,10.7"));

    /// <summary>Refresh - a 270 degree arc with a two-stroke arrowhead on
    /// its open end. Rebuilds the project preview after the ranges behind it
    /// have been edited by hand.</summary>
    public static readonly Geometry Reload = Frz(Geometry.Parse(
        "M11.25,4.95 A4.6,4.6 0 1 0 11.25,11.45 M11.25,4.95 L12.2,7.6 M11.25,4.95 L13.9,5.9"));

    /// <summary>Copy - two overlapping outlined squares, the conventional
    /// "copy to clipboard" shorthand. Used on the console header's copy-log
    /// button.</summary>
    public static readonly Geometry Copy = Frz(Geometry.Parse(
        "M3.0,3.0 L10.0,3.0 L10.0,10.0 L3.0,10.0 Z M6.0,6.0 L13.0,6.0 L13.0,13.0 L6.0,13.0 Z"));

    /// <summary>Checkmark - brief post-click confirmation, swapped in for
    /// <see cref="Copy"/> for a moment after a successful clipboard copy.</summary>
    public static readonly Geometry Check = Frz(Geometry.Parse("M3.2,8.4 L6.6,11.8 L12.8,4.6"));

    // -- redesign ---------------------------------------------------------------
    // Added for the mockup, which labels far more of the UI with icons than
    // the pre-redesign window did. Same 16x16 box and same Stroke weight as
    // everything above, so they can be mixed inside one toolbar without the
    // line weight visibly stepping.

    /// <summary>Show/preview - an almond with a pupil. Fronts the "Preview
    /// project" toggle, where a play triangle would wrongly promise
    /// playback of a single file (the row's own preview arrow already means
    /// that).</summary>
    public static readonly Geometry Eye = BuildEye();

    private static Geometry BuildEye()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(Geometry.Parse("M2.2,8 C4.6,4.6 11.4,4.6 13.8,8 C11.4,11.4 4.6,11.4 2.2,8 Z"));
        g.Children.Add(new EllipseGeometry(new Point(8, 8), 1.9, 1.9));
        return Frz(g);
    }

    /// <summary>A smaller cross for the range chip's remove button. The
    /// chip's button is itself only 16 DIP across (token file), and
    /// <see cref="Close"/> is authored to fill the whole 16x16 box, so
    /// reusing it puts the glyph hard against the button's own edge. Drawn
    /// on the same box, just shorter, since every icon here renders with
    /// Stretch.None and cannot simply be scaled down without thinning the
    /// stroke with it.</summary>
    public static readonly Geometry CloseSmall = Frz(Geometry.Parse("M5.6,5.6 L10.4,10.4 M10.4,5.6 L5.6,10.4"));

    // -- title bar caption buttons --------------------------------------------
    // Minimize reuses Minus (a bare horizontal line) - no separate glyph
    // needed. Maximize/Restore are the two caption glyphs Minus doesn't
    // cover, drawn at the same weight as the rest of the icon set.

    /// <summary>Maximize - a single square outline.</summary>
    public static readonly Geometry Maximize = Frz(Geometry.Parse("M4.4,4.4 L11.6,4.4 L11.6,11.6 L4.4,11.6 Z"));

    /// <summary>Restore - two overlapping squares: a partial one (top+right
    /// edges only, so it doesn't read as a second full square competing with
    /// the front one) behind a full one.</summary>
    public static readonly Geometry Restore = Frz(Geometry.Parse(
        "M6.2,4.2 L11.8,4.2 L11.8,9.8 M4.2,6.2 L9.8,6.2 L9.8,11.8 L4.2,11.8 Z"));

    /// <summary>Disclosure arrows for the collapsible instruction panel and
    /// the console drawer: right when closed, down when open.</summary>
    public static readonly Geometry ChevronRight = Frz(Geometry.Parse("M6.4,3.6 L10.8,8 L6.4,12.4"));
    public static readonly Geometry ChevronDown = Frz(Geometry.Parse("M3.6,6.4 L8,10.8 L12.4,6.4"));

    /// <summary>Load project - an open folder.</summary>
    public static readonly Geometry FolderOpen = Frz(Geometry.Parse(
        "M2.6,12.6 L2.6,3.8 L6.4,3.8 L7.9,5.8 L12.4,5.8 L12.4,7.6 " +
        "M2.6,12.6 L4.7,7.6 L13.9,7.6 L11.8,12.6 Z"));

    /// <summary>Console - a terminal window with a prompt caret and a
    /// command line.</summary>
    public static readonly Geometry Terminal = Frz(Geometry.Parse(
        "M2.4,3.6 L13.6,3.6 L13.6,12.4 L2.4,12.4 Z " +
        "M5.0,6.6 L7.3,8.6 L5.0,10.6 M8.8,10.6 L11.4,10.6"));

    /// <summary>CPU stat tile - a chip with its pins.
    ///
    /// Two pins per side rather than the usual three, and the die is small:
    /// the first draft used a 4.0-12.0 package around a 6.4-9.6 die, which
    /// reads at 5x magnification but collapses into a blob at the 16 DIP it
    /// actually ships at, because a 2.4 DIP gap stroked at 1.6 leaves only
    /// 0.8 DIP of clear space. Verified by rendering both sizes side by side.
    /// The package moved out and the die in, so every gap now clears the
    /// stroke.</summary>
    public static readonly Geometry Cpu = BuildCpu();

    private static Geometry BuildCpu()
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(Geometry.Parse("M3.4,3.4 L12.6,3.4 L12.6,12.6 L3.4,12.6 Z"));
        g.Children.Add(Geometry.Parse("M6.7,6.7 L9.3,6.7 L9.3,9.3 L6.7,9.3 Z"));
        foreach (double at in new[] { 6.2, 9.8 })
        {
            g.Children.Add(new LineGeometry(new Point(at, 3.4), new Point(at, 1.8)));
            g.Children.Add(new LineGeometry(new Point(at, 12.6), new Point(at, 14.2)));
            g.Children.Add(new LineGeometry(new Point(3.4, at), new Point(1.8, at)));
            g.Children.Add(new LineGeometry(new Point(12.6, at), new Point(14.2, at)));
        }
        return Frz(g);
    }

    /// <summary>Generic numeric-readout mark for the stat tiles that are not
    /// the CPU one (memory, speed, ETA) - three ascending bars.</summary>
    public static readonly Geometry Chart = Frz(Geometry.Parse(
        "M3.8,12.4 L3.8,9.0 M8,12.4 L8,5.4 M12.2,12.4 L12.2,7.2"));

}
