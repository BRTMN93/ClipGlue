using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClipGlue;

/// <summary>
/// Dark theme palette, fonts and row-list layout constants.
///
/// Originally a direct port of clipglue/theme.py; the palette has since been
/// replaced wholesale by the redesign token set (Bartek's approved mockup,
/// transcribed in <c>lista tokenow.txt</c>). The file is now in two layers:
///
/// 1. <b>Palette</b> - the token names exactly as the token file spells them.
///    This is the ONLY place a color literal is written.
/// 2. <b>Roles</b> - every name the views already use, assigned FROM the
///    palette. Re-pointing a role is a one-line edit here and needs no change
///    at any of its call sites, which is why the old names were kept rather
///    than renamed across ~20 files.
///
/// Brushes are frozen for cross-thread readability and to avoid per-use
/// allocation.
/// </summary>
public static class Theme
{
    // =====================================================================
    // PALETTE - 1:1 with the token file. Nothing below invents a color;
    // everything else is one of these, or an explicitly documented
    // derivation of one.
    // =====================================================================

    // -- Depth. Four clearly separated surfaces instead of the two-and-a-half
    // the pre-redesign palette had, so "window" / "panel" / "card" /
    // "raised element" are told apart by value alone, without borders.
    /// <summary>Deepest layer: the window itself.</summary>
    public static readonly Color BgWindow = Color.FromRgb(0x0D, 0x0F, 0x14);
    /// <summary>Title-bar chrome and the surface cards are laid on.</summary>
    public static readonly Color BgPanel = Color.FromRgb(0x16, 0x19, 0x22);
    /// <summary>File cards and panels.</summary>
    public static readonly Color BgCard = Color.FromRgb(0x1C, 0x20, 0x29);
    /// <summary>Raised elements: inputs, stat chips, range chips.</summary>
    public static readonly Color BgCardHl = Color.FromRgb(0x24, 0x29, 0x38);
    /// <summary>Brightest layer: dialogs, the active element, progress troughs.</summary>
    public static readonly Color BgCardHl2 = Color.FromRgb(0x2E, 0x34, 0x46);

    // -- Lines.
    /// <summary>Hairline, low contrast - visible separators.</summary>
    public static readonly Color Border = Color.FromRgb(0x33, 0x3A, 0x4C);
    /// <summary>Card outline. Deliberately a translucent white rather than a
    /// fixed color, so one brush reads correctly over every depth layer
    /// instead of needing a per-surface variant.</summary>
    public static readonly Color BorderSoft = Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF);

    // -- Text.
    public static readonly Color TextHi = Color.FromRgb(0xF1, 0xF3, 0xF8);
    public static readonly Color TextMid = Color.FromRgb(0xA7, 0xAF, 0xC4);
    public static readonly Color TextLow = Color.FromRgb(0x6B, 0x72, 0x88);

    // -- Accents. Two of them now, and that is the point of the redesign:
    // gold is reserved for the actions that start something (primary CTA,
    // focus, the row's own identity), cyan carries information (times,
    // links, progress, kept segments). One color meaning everything meant
    // nothing.
    public static readonly Color AccentGold300 = Color.FromRgb(0xFF, 0xCF, 0x82);
    public static readonly Color AccentGold500 = Color.FromRgb(0xF2, 0xA5, 0x3C);
    public static readonly Color AccentGold700 = Color.FromRgb(0xC9, 0x76, 0x1F);
    public static readonly Color AccentCyan300 = Color.FromRgb(0x8F, 0xE4, 0xDC);
    public static readonly Color AccentCyan400 = Color.FromRgb(0x5F, 0xD0, 0xC8);

    // -- Status.
    public static readonly Color Danger = Color.FromRgb(0xEF, 0x5D, 0x6F);
    public static readonly Color DangerDim = Color.FromRgb(0x7A, 0x35, 0x40);
    public static readonly Color Success = Color.FromRgb(0x4B, 0xD1, 0x91);

    /// <summary>Text printed ON a gold surface - a warm near-black rather
    /// than the neutral <see cref="DarkText"/>, so it sits in the same
    /// temperature as the gradient under it.</summary>
    public static readonly Color OnAccent = Color.FromRgb(0x2B, 0x1A, 0x04);

    // =====================================================================
    // ROLES - the names the views use. Assigned from the palette above.
    // Static field initializers run in textual order, so these must stay
    // below it.
    // =====================================================================

    public static readonly Color Bg = BgWindow;
    public static readonly Color BgRow = BgCard;
    public static readonly Color BgRowHl = BgCardHl;
    /// <summary>Entry fields read as raised, not recessed, in the new depth
    /// scale - the token file lists inputs under BgCardHl together with the
    /// stat chips.</summary>
    public static readonly Color BgInput = BgCardHl;
    public static readonly Color BorderCard = BorderSoft;

    public static readonly Color Fg = TextHi;
    public static readonly Color FgDim = TextMid;
    /// <summary>Third text level, below <see cref="FgDim"/>: the small
    /// uppercase section labels ("FULL CLIP OVERVIEW", "START") and the
    /// keyboard hint line, which must sit behind the values they label.</summary>
    public static readonly Color FgFaint = TextLow;

    public static readonly Color FgAccent = AccentGold500;
    public static readonly Color HoverAccent = AccentGold300;
    /// <summary>The second accent, by its role name. Same value as
    /// <see cref="FgLink"/> - new code should say AccentInfo when it means
    /// "this is information", and FgLink only when it means "this is
    /// clickable".</summary>
    public static readonly Color AccentInfo = AccentCyan400;
    /// <summary>Was a cool blue; now the cyan accent. Every "link blue"
    /// element in the app (range text, the row's preview arrow, the scrubber
    /// band edges) moves to the second accent in one step because of this
    /// line.</summary>
    public static readonly Color FgLink = AccentCyan400;

    public static readonly Color Red = Danger;
    public static readonly Color Green = Success;
    /// <summary>Hover state of a danger surface - the token file gives only
    /// Danger and DangerDim, so this stays the lighter tint it always was.</summary>
    public static readonly Color HoverRed = Color.FromRgb(0xFF, 0x7B, 0x86);

    /// <summary>Neutral near-black used as a dark SURFACE (the OK chip on the
    /// red error panel) as well as for text on an accent fill. Text on gold
    /// should now use <see cref="OnAccent"/>; this stays neutral because
    /// re-pointing it warm would tint that error-dialog chip brown.</summary>
    public static readonly Color DarkText = Color.FromRgb(0x1C, 0x20, 0x28);

    /// <summary>Console surface: the deepest layer, so the log reads as a
    /// well cut into the panel it sits on.</summary>
    public static readonly Color LogBg = BgWindow;
    public static readonly Color LogFg = Color.FromRgb(0xC9, 0xD3, 0xE3);
    public static readonly Color LogDim = TextLow;

    /// <summary>The letterbox behind the preview picture - below even
    /// <see cref="BgWindow"/>, so a dark frame still reads as picture rather
    /// than blending into its own container.</summary>
    public static readonly Color VideoBg = Color.FromRgb(0x06, 0x07, 0x0A);

    public static readonly Color DisabledBg = BgCardHl;
    public static readonly Color DisabledFg = TextLow;
    public static readonly Color DisabledHandle = Border;
    /// <summary>Hover wash behind a row's icon buttons - the icons have no
    /// chrome of their own, so the hit area only becomes visible on hover.</summary>
    public static readonly Color IconHoverBg = Color.FromArgb(0x2e, 0xff, 0xff, 0xff);
    public static readonly Color ScrollThumb = Color.FromRgb(0x3C, 0x43, 0x57);
    public static readonly Color ScrollThumbHover = Color.FromRgb(0x53, 0x5C, 0x74);

    // -- Timeline roles (trim-range window, project scrubber). Two pairs of
    // range colors: solid ones for the overview bar (nothing behind them)
    // and translucent ones for the zoomed scrubber (thumbnails behind them,
    // which have to stay readable through the band).

    /// <summary>Empty trough of the overview bar, the scrubber and the
    /// progress bars.</summary>
    public static readonly Color TimelineTrack = BgCardHl2;
    /// <summary>A committed range. Cyan, matching the mockup's "kept
    /// segments" - the ranges are information about the output, not an
    /// action.</summary>
    public static readonly Color RangeFill = AccentCyan400;
    /// <summary>The range currently being edited, in the action accent so it
    /// separates from the committed ones at a glance.</summary>
    public static readonly Color RangeAccentFill = AccentGold700;
    public static readonly Color RangeFillSoft = Color.FromArgb(0x99, AccentCyanR, AccentCyanG, AccentCyanB);
    public static readonly Color RangeAccentSoft = Color.FromArgb(0x99, 0xC9, 0x76, 0x1F);
    /// <summary>Outline for an accent handle sitting on top of its own accent
    /// fill, where a light ring would disappear.</summary>
    public static readonly Color AccentDark = AccentGold700;
    /// <summary>The viewport window drawn over the overview bar.</summary>
    public static readonly Color ViewportWash = Color.FromArgb(0x24, 0xff, 0xff, 0xff);
    public static readonly Color ViewportEdge = Color.FromArgb(0x9a, 0xF1, 0xF3, 0xF8);
    /// <summary>Backing for the small pills floated over the picture (the
    /// volume control, the "frame @" readout) and for the scrubber's
    /// drag-time bubble.</summary>
    public static readonly Color OverlayPill = Color.FromArgb(0xc4, 0x06, 0x07, 0x0A);
    /// <summary>Scrim laid over the filmstrip so the bands, ticks, playhead
    /// and handles stay legible over a bright frame.</summary>
    public static readonly Color FilmScrim = Color.FromArgb(0x59, 0x06, 0x07, 0x0A);
    public static readonly Color KeyframeTick = Color.FromArgb(0x5e, 0xF1, 0xF3, 0xF8);

    // AccentCyan400's channels, spelled out because Color.FromArgb needs
    // compile-time byte arguments and cannot destructure an existing Color
    // in a field initializer.
    private const byte AccentCyanR = 0x5F, AccentCyanG = 0xD0, AccentCyanB = 0xC8;

    // =====================================================================
    // BRUSHES
    // =====================================================================

    public static readonly SolidColorBrush BgWindowBrush = Frz(BgWindow);
    public static readonly SolidColorBrush BgCardBrush = Frz(BgCard);
    public static readonly SolidColorBrush BgCardHlBrush = Frz(BgCardHl);
    public static readonly SolidColorBrush BgCardHl2Brush = Frz(BgCardHl2);
    public static readonly SolidColorBrush BorderSoftBrush = Frz(BorderSoft);
    public static readonly SolidColorBrush TextHiBrush = Frz(TextHi);
    public static readonly SolidColorBrush TextMidBrush = Frz(TextMid);
    public static readonly SolidColorBrush TextLowBrush = Frz(TextLow);
    public static readonly SolidColorBrush AccentGold300Brush = Frz(AccentGold300);
    public static readonly SolidColorBrush AccentGold500Brush = Frz(AccentGold500);
    public static readonly SolidColorBrush AccentGold700Brush = Frz(AccentGold700);
    public static readonly SolidColorBrush AccentCyan300Brush = Frz(AccentCyan300);
    public static readonly SolidColorBrush AccentCyan400Brush = Frz(AccentCyan400);
    public static readonly SolidColorBrush AccentInfoBrush = Frz(AccentInfo);
    public static readonly SolidColorBrush DangerBrush = Frz(Danger);
    public static readonly SolidColorBrush DangerDimBrush = Frz(DangerDim);
    public static readonly SolidColorBrush SuccessBrush = Frz(Success);
    public static readonly SolidColorBrush OnAccentBrush = Frz(OnAccent);

    public static readonly SolidColorBrush BgBrush = Frz(Bg);
    public static readonly SolidColorBrush BgPanelBrush = Frz(BgPanel);
    public static readonly SolidColorBrush BgRowBrush = Frz(BgRow);
    public static readonly SolidColorBrush BgRowHlBrush = Frz(BgRowHl);
    public static readonly SolidColorBrush BgInputBrush = Frz(BgInput);
    public static readonly SolidColorBrush FgBrush = Frz(Fg);
    public static readonly SolidColorBrush FgDimBrush = Frz(FgDim);
    public static readonly SolidColorBrush FgAccentBrush = Frz(FgAccent);
    public static readonly SolidColorBrush FgLinkBrush = Frz(FgLink);
    public static readonly SolidColorBrush RedBrush = Frz(Red);
    public static readonly SolidColorBrush GreenBrush = Frz(Green);
    public static readonly SolidColorBrush BorderBrush = Frz(Border);
    public static readonly SolidColorBrush BorderCardBrush = Frz(BorderCard);
    public static readonly SolidColorBrush RangeFillBrush = Frz(RangeFill);
    public static readonly SolidColorBrush LogBgBrush = Frz(LogBg);
    public static readonly SolidColorBrush VideoBgBrush = Frz(VideoBg);
    public static readonly SolidColorBrush LogFgBrush = Frz(LogFg);
    public static readonly SolidColorBrush LogDimBrush = Frz(LogDim);
    public static readonly SolidColorBrush DarkTextBrush = Frz(DarkText);
    public static readonly SolidColorBrush HoverRedBrush = Frz(HoverRed);
    public static readonly SolidColorBrush HoverAccentBrush = Frz(HoverAccent);
    public static readonly SolidColorBrush DisabledBgBrush = Frz(DisabledBg);
    public static readonly SolidColorBrush DisabledFgBrush = Frz(DisabledFg);
    public static readonly SolidColorBrush DisabledHandleBrush = Frz(DisabledHandle);
    public static readonly SolidColorBrush IconHoverBgBrush = Frz(IconHoverBg);
    public static readonly SolidColorBrush ScrollThumbBrush = Frz(ScrollThumb);
    public static readonly SolidColorBrush ScrollThumbHoverBrush = Frz(ScrollThumbHover);
    public static readonly SolidColorBrush TransparentBrush = Frz(Colors.Transparent);

    public static readonly SolidColorBrush FgFaintBrush = Frz(FgFaint);
    public static readonly SolidColorBrush TimelineTrackBrush = Frz(TimelineTrack);
    public static readonly SolidColorBrush RangeAccentFillBrush = Frz(RangeAccentFill);
    public static readonly SolidColorBrush RangeFillSoftBrush = Frz(RangeFillSoft);
    public static readonly SolidColorBrush RangeAccentSoftBrush = Frz(RangeAccentSoft);
    public static readonly SolidColorBrush AccentDarkBrush = Frz(AccentDark);
    public static readonly SolidColorBrush ViewportWashBrush = Frz(ViewportWash);
    public static readonly SolidColorBrush ViewportEdgeBrush = Frz(ViewportEdge);
    public static readonly SolidColorBrush OverlayPillBrush = Frz(OverlayPill);
    public static readonly SolidColorBrush FilmScrimBrush = Frz(FilmScrim);
    public static readonly SolidColorBrush KeyframeTickBrush = Frz(KeyframeTick);

    // -- Gradients -----------------------------------------------------------

    /// <summary>The primary-action fill: gold-300 to gold-500 on a 135 degree
    /// diagonal (top-left to bottom-right), per the token file. Used by the
    /// primary button variant and the index badge.</summary>
    public static readonly LinearGradientBrush AccentGradientBrush =
        FrzGradient(new Point(0, 0), new Point(1, 1), AccentGold300, AccentGold500);

    /// <summary>Badge fill: the same diagonal, run all the way down to
    /// gold-700 so a 22x22 chip still shows a readable gradient over its much
    /// shorter travel.</summary>
    public static readonly LinearGradientBrush BadgeGradientBrush =
        FrzGradient(new Point(0, 0), new Point(1, 1), AccentGold300, AccentGold700);

    /// <summary>Progress fill: gold to cyan, left to right - the bar starts in
    /// the action accent and lands in the information accent.</summary>
    public static readonly LinearGradientBrush ProgressGradientBrush =
        FrzGradient(new Point(0, 0), new Point(1, 0), AccentGold500, AccentCyan400);

    /// <summary>A kept segment on a timeline: cyan-400 to cyan-300, left to
    /// right.</summary>
    public static readonly LinearGradientBrush KeptSegmentBrush =
        FrzGradient(new Point(0, 0), new Point(1, 0), AccentCyan400, AccentCyan300);

    /// <summary>
    /// The 1px elevation highlight along a card's top edge: white at 9%
    /// fading out to the right.
    ///
    /// Painted by a dedicated 1px-tall child, NOT folded into the card's own
    /// Background. Both were measured (200 cards, mean of 10 Measure+Arrange
    /// +Render passes at 144 dpi): the extra child costs 44.4 ms, a gradient
    /// covering the whole card background costs 57.6 ms - filling 42 rows of
    /// pixels through the gradient rasterizer is dearer than filling one. The
    /// child also wins on correctness, since the token file's fade runs
    /// ACROSS the card, which a single background brush cannot express while
    /// also carrying the card's own fill.
    ///
    /// For reference from the same measurement: a DropShadowEffect on every
    /// card costs 1744.5 ms - roughly 39x the chosen approach, because each
    /// effect forces its own intermediate render surface. That is why
    /// elevation in the file list is drawn, and why a real shadow is spent
    /// only on single elements (see <see cref="AccentGlow"/>, and
    /// FileRowControl's drag shadow, which exists on one row at a time).
    /// </summary>
    public static readonly LinearGradientBrush CardTopHighlightBrush =
        FrzGradient(new Point(0, 0), new Point(1, 0),
            Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));

    /// <summary>Height of that highlight line, and the inset that keeps it
    /// from poking out of the card's rounded corners (a Border does not clip
    /// its children to its own CornerRadius).</summary>
    public const double CardTopHighlightH = 1;
    public const double CardTopHighlightInset = CardRadius - 2;

    /// <summary>The primary button's glow: blur 11, gold-500 at 55%, thrown
    /// 5 DIP downward - half the token file's original blur/depth (22/10),
    /// trimmed down at Bartek's request because the full-size glow read as
    /// too heavy under the CTA buttons. Shared and frozen - one instance on a
    /// handful of CTA buttons is affordable; see
    /// <see cref="CardTopHighlightBrush"/> for why this must never go on a
    /// list row.</summary>
    public static readonly DropShadowEffect AccentGlow = FrzEffect(new DropShadowEffect
    {
        Color = AccentGold500,
        BlurRadius = 11,
        ShadowDepth = 5,
        Direction = 270,
        Opacity = 0.55,
    });

    /// <summary>Elevation shadow for the one thing in the app that is meant
    /// to float visibly above everything else: the center-screen dialogs
    /// (<see cref="ClipGlue.Dialogs.ModalDialogWindow"/>). Affordable for the
    /// same reason <see cref="AccentGlow"/> is - one instance on one element
    /// at a time, never on a list row (see AccentGlow's own note on the
    /// per-card cost this would otherwise carry).</summary>
    public static readonly DropShadowEffect DialogShadow = FrzEffect(new DropShadowEffect
    {
        Color = Colors.Black,
        BlurRadius = 28,
        ShadowDepth = 10,
        Direction = 270,
        Opacity = 0.5,
    });

    private static SolidColorBrush Frz(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush FrzGradient(Point start, Point end, Color from, Color to)
    {
        var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        b.GradientStops.Add(new GradientStop(from, 0));
        b.GradientStops.Add(new GradientStop(to, 1));
        b.Freeze();
        return b;
    }

    private static DropShadowEffect FrzEffect(DropShadowEffect e)
    {
        e.Freeze();
        return e;
    }

    // =====================================================================
    // TYPOGRAPHY
    // =====================================================================
    // tkinter font sizes are points (1/72"); WPF FontSize is device-independent
    // pixels (1/96") - multiplied by 96/72 (~1.333) below to land on a visually
    // equivalent size.
    //
    // The token file asks for three families (Sora for headings, Manrope for
    // UI text, JetBrains Mono for times and logs). Those are not installed on
    // this machine and WPF cannot fetch a webfont, so they arrive as embedded
    // resources in a later phase; the three roles below are already separated
    // so that lands as three assignments here rather than a sweep of the views.
    public static readonly FontFamily HeadFontFamily = new("Segoe UI Semibold");
    public static readonly FontFamily UiFontFamily = new("Segoe UI");
    public static readonly FontFamily MonoFontFamily = new("Consolas");

    public const double UiFontSize = 13;
    public const double HeadFontSize = 17;
    public const double MonoFontSize = 13;
    public const double IconFontLgSize = 20;
    public const double BarFontSize = 12;
    /// <summary>Small uppercase label above a value field.</summary>
    public const double LabelFontSize = 11;
    /// <summary>Times inside a range chip - a step below the general mono
    /// size, since a chip is a dense element. ~11.5 * 1.25, per Bartek's
    /// request to enlarge the range/add chips by 25%.</summary>
    public const double ChipFontSize = 14.5;

    // =====================================================================
    // SHAPE AND SPACING
    // =====================================================================
    // Cards and buttons share a radius family; inputs and progress bars sit
    // one step tighter; the two big containers (file list, console) sit at
    // the card radius so they read as the surface those cards live on.
    public const double CardRadius = 12;
    public const double ButtonRadius = 10;
    public const double InputRadius = 8;
    public const double BarRadius = 8;
    public const double PanelRadius = 12;
    /// <summary>Range/add chip corner radius. The token file called for a
    /// full pill (999, clamped by WPF), but the approved mockup actually
    /// draws a rounded RECTANGLE, not a stadium - Bartek flagged the pill
    /// shape as wrong once built. Set to the same radius as the inputs the
    /// chips sit alongside (<see cref="InputRadius"/>), since the token file
    /// groups them in the same "raised element" tier. Still large enough
    /// that the chip's own small round bits (the 6.25 DIP status dot, the 20
    /// DIP close button) stay fully circular - WPF clamps a corner radius
    /// larger than half an element's own size.</summary>
    public const double ChipRadius = InputRadius;
    /// <summary>The window's own corner. Only reachable with custom chrome
    /// (WindowStyle=None); the system chrome rounds at its own ~8.</summary>
    public const double WindowRadius = 18;
    /// <summary>Height of the custom title bar (see Controls/TitleBar.cs)
    /// that replaces the system one on WindowStyle=None windows - also the
    /// WindowChrome.CaptionHeight those windows set, so the draggable region
    /// and the drawn bar always agree.</summary>
    public const double TitleBarHeight = 44;

    // -- Component metrics from the token file's "Komponenty" section --------
    /// <summary>Row index badge: a 22x22 chip at radius 7, filled with
    /// <see cref="BadgeGradientBrush"/>.</summary>
    public const double BadgeSize = 22;
    public const double BadgeRadius = 7;
    /// <summary>Range chip: the cyan status dot on its left, and the round
    /// close button on its right. Both ~x1.25 along with the rest of the
    /// chip (rounded to whole DIPs - see the note on <see cref="RowInputH"/>
    /// for why fractional chip metrics are worth avoiding) - still small
    /// enough that <see cref="ChipRadius"/> rounds them fully circular.</summary>
    public const double ChipDotSize = 6;
    public const double ChipCloseSize = 20;

    /// <summary>Left/right window padding shared by every top-level section.</summary>
    public const double PagePad = 20;
    /// <summary>Vertical gap between major sections (heading, toolbar, file
    /// list, action buttons, progress bars, console).</summary>
    public const double SectionGap = 20;
    /// <summary>Horizontal padding inside a file card, and the gap between
    /// the elements in it.</summary>
    public const double CardPadH = 14;
    /// <summary>Vertical padding inside a FILE ROW card. Stays at 6, not the
    /// token file's 12: the row card's height is pinned to RowCardH (the list
    /// canvas positions rows at fixed RowH steps), so this is the padding
    /// budget left around a fixed-height entry, not a free choice. The token
    /// value applies to the roomier panel cards - see <see cref="PanelPadV"/>.</summary>
    public const double CardPadV = 8;
    /// <summary>Vertical padding inside a panel card, where height is free.</summary>
    public const double PanelPadV = 12;
    public const double CardGap = 10;
    /// <summary>Gap between two chips sitting side by side. ~x1.25 with the
    /// rest of the chip, rounded to a whole DIP - see the note on
    /// <see cref="RowInputH"/>.</summary>
    public const double ChipGap = 6;

    // Row-list layout. RowH is the full slot a row occupies in the list
    // canvas (card height + the gap below it), RowPad is that gap, so the
    // card itself is RowH - RowPad tall.
    //
    // The row is TWO lines, per the mockup: identity on the first (handle,
    // badge, name, duration, edit, delete) and the range chips on the
    // second. It is still a FIXED height - the list canvas positions rows by
    // multiplying by RowH, so the two-line row is a bigger constant, not a
    // measured one, and none of the canvas gotchas (#12/#18/#20) are
    // reopened by it. FileRowControl's Grid.Row(1) - the chip line's own
    // cell - is (RowInputH + RowLineGap) tall, not just RowInputH: inside it,
    // _rangeBox (RangeChipList) sits Margin.Top = RowLineGap with its own
    // Height = RowInputH, so the cell needs BOTH to avoid the chip strip
    // overflowing past the Grid's own bottom edge. The full budget is
    // CardPadV + RowLine1H + RowLineGap + RowInputH + CardPadV = RowCardH.
    //
    // History, since this block has been wrong twice on the way here:
    // 1. The chip strip's 25% enlargement (Bartek's request) first landed as
    //    exact x1.25 fractional DIPs (RowInputH 31.5, chip height 27.5, dot
    //    6.25, gap 6.25). Every chip's bottom border edge sheared off in
    //    testing. Suspected cause at the time: RangeChipList's available
    //    arrange slot (RowInputH - RowLineGap) always exactly equals the
    //    chip's own height (RowInputH - 4, since RowLineGap is 4) with ZERO
    //    slack, and fractional DIPs on a SnapsToDevicePixels=true element
    //    push that zero-slack geometry into device-pixel rounding.
    // 2. Rounding every chip constant to a whole DIP (this block's values
    //    below) did NOT fix it, disproving that theory outright - confirmed
    //    by rendering the production FileRowControl class offscreen via
    //    RenderTargetBitmap (bypassing screen capture entirely): pixel-
    //    perfect rounded corners, on every side, at the whole-DIP sizes.
    //    The live window's on-screen rendering still showed a sheared bottom
    //    edge in BOTH CopyFromScreen and PrintWindow captures, which is a
    //    live-rendering/compositor question, not a layout-math one - the
    //    layout math was already correct.
    // 3. Separately, and regardless of (2): the zero-slack overflow described
    //    in (1) was real - RangeChipList genuinely does render past its own
    //    Grid.Row(1) cell by exactly RowLineGap, landing in _root's
    //    Padding.Bottom cushion (CardPadV) rather than clipping, but with no
    //    remaining gap of its own before that cushion. Bartek asked for that
    //    overlap gone rather than just cushioned, so FileRowControl's row-2
    //    RowDefinition is now sized RowInputH + RowLineGap (see the comment
    //    there), and RowCardH's formula picked up the + RowLineGap term this
    //    block always documented but that was never actually true of the
    //    Grid until now - this is what actually removes the overflow, on top
    //    of (2)'s unrelated whole-DIP cleanup.
    //
    // RowH/RowCardH/RowInputH carry a +6 DIP bump from the pre-redesign
    // values (RowH 78->84, RowInputH 26->32), all of it the chip strip's 25%
    // enlargement (chip height is RowInputH - 4, so 28 DIP tall, up from 22
    // - not quite +25% since it lands on a whole DIP, per (2) above).
    // RowLineGap itself did not change (still 4) and was already part of the
    // formula from the start - point 3 above closes a real 4 DIP overflow
    // gap, it does not add 4 DIP of new content. Nothing else in the row
    // list hardcodes the old numbers (everything reads Theme.RowH/
    // RowInputH), so the canvas step, viewport heights and card size all
    // pick up the new budget for free - only the constants below needed to
    // move.
    public const double RowH = 84;
    public const double RowPad = 8;
    public const double RowCardH = RowH - RowPad;
    /// <summary>The identity line: badge, file name, duration, buttons.</summary>
    public const double RowLine1H = 24;
    /// <summary>Gap between the two lines of a row card. Also spent as
    /// RangeChipList's own top margin inside its Grid.Row(1) cell - see the
    /// note on <see cref="RowInputH"/> for why the cell has to be sized
    /// RowInputH + RowLineGap, not just RowInputH, to actually contain
    /// it.</summary>
    public const double RowLineGap = 4;
    /// <summary>Height of the chip strip (and of the raw entry that replaces
    /// it) on the row's second line - fixed rather than content-derived so
    /// the card height stays exactly RowCardH. ~26 * 1.25, per Bartek's
    /// request to enlarge the range/add chips by 25% (chip height is
    /// RowInputH - 4, so this yields a chip 28 DIP tall, up from 22) - see
    /// the note above this block for the whole history behind landing on a
    /// whole DIP rather than literal 31.5, and for why RowLineGap on top of
    /// this is what the chip strip's own grid cell actually needs.</summary>
    public const double RowInputH = 32;
    /// <summary>Left indent of the chip line, so the chips start under the
    /// file name rather than under the drag handle.</summary>
    public const double RowChipIndent = 30;
    public const double BarH = 20;
}
