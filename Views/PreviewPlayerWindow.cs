using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipGlue.Controls;
using ClipGlue.Dialogs;
using ClipGlue.Engine;
using ClipGlue.Models;
using IoPath = System.IO.Path;

namespace ClipGlue.Views;

/// <summary>
/// The trim-range window: watch a loaded video and mark keep-ranges visually
/// instead of typing MM:SS:ff by hand. Grown well past preview_player.py's
/// PreviewPlayer, most recently by the redesign in
/// ClipGlue_Preview_Redesign_Spec.md.
///
/// TWO PICTURE SOURCES, and the difference matters. Playback runs through a
/// <see cref="MediaElement"/>, which decodes in-process and can therefore
/// actually play at speed and carry audio. The Python original had no
/// playback at all: it shot one ffmpeg process per frame, wrote a PPM to temp
/// and decoded that - roughly 0.1-0.4s per frame, fine for scrubbing and
/// hopeless for 25fps. That ffmpeg path is still here as the fallback,
/// because MediaElement leans on the codecs Windows itself has and will
/// refuse files this app otherwise handles perfectly well (MKV especially).
/// When MediaFailed fires, the window silently drops back to the
/// frame-at-a-time preview and disables the play button, keeping everything
/// else - scrubbing, grips, ranges - working exactly as before. The same
/// ffmpeg path also feeds the scrubber's filmstrip through
/// <see cref="ThumbnailStrip"/>, so it is load-bearing twice over.
///
/// TWO TIMELINES, which is the core of the redesign. The old single bar
/// covered the whole file at about 1.5 seconds per pixel on a long clip -
/// not enough resolution to land a cut by dragging - and showed nothing of
/// the ranges already added. Now <see cref="TimelineOverview"/> owns
/// navigation (whole file, every range marked, a window showing what is
/// zoomed in below) and <see cref="TimelineScrubber"/> owns precision (only
/// the current viewport, with a filmstrip and keyframe ticks behind the
/// grips).
///
/// THE RANGE LIST IS THE TRUTH. in/out is not a scratch selection that only
/// becomes real on "Add range" any more: it is always bound to one row of
/// <see cref="_ranges"/> (<see cref="_editIndex"/>), written through on every
/// change, so the list, both timelines and the fields can never disagree.
/// "Add range" closes the current row off and opens a fresh one; Apply emits
/// exactly the rows the list shows.
///
/// The subtitle overlay uses a real stroked text outline
/// (FormattedText.BuildGeometry + Path Fill/Stroke) instead of the Python
/// original's four-offset-copies halo hack, which Tkinter's Canvas needed
/// because it has no text-stroke option at all.
/// </summary>
public sealed class PreviewPlayerWindow : Window
{
    private const double SubtitleFontSize = 17;
    private const double MinRangeSeconds = 0.05;
    private const int MaxPreviewW = 720;
    private const int MaxPreviewH = 300;
    /// <summary>Floor the video card drops to once the window has been
    /// measured, so the window stays shrinkable in height.</summary>
    private const double VideoFloorH = 140;
    /// <summary>Narrowest slice of the file the scrubber will ever show -
    /// the cap on how far the zoom controls go in.</summary>
    private const double MinViewSpanSeconds = 2.0;
    private const double RangeListMaxH = 132;

    private readonly Action<string> _onApply;
    private readonly string _filePath;
    private readonly double _duration;
    private readonly double _frameStep;
    private readonly int _videoW, _videoH;
    private readonly int _naturalPreviewH;
    private readonly double _maxZoom;
    // Not readonly: populated later by LoadSubtitlesAsync (see N11 in
    // AUDIT_TODO.md) instead of synchronously in the constructor.
    private List<SubtitleCue> _subtitleCues;
    private readonly string? _subtitleCodec;
    private readonly FrameFetcher _fetcher;
    private readonly ThumbnailStrip _thumbs;
    private readonly DispatcherTimer _playTimer;

    /// <summary>Every row the range list shows, in insertion order. At most
    /// one of them is zero-length: the freshly opened row waiting to be
    /// marked out.</summary>
    private readonly List<TimeRange> _ranges = new();
    private int _editIndex = -1;

    private int _previewW, _previewH;
    private double _inT, _outT, _playheadT;
    private double _zoom = 1.0;
    private double _viewCenter;
    private double _volumeLevel = 1.0;
    private bool _muted;
    private bool _closed;
    private bool _mediaReady;
    private bool _playing;
    private string? _lastSubtitleText;
    private double _subtitleCenterX, _subtitleBottomY, _subtitleWrapWidth = 200;

    private Grid _contentRoot = null!;
    private Border _dimOverlay = null!;
    private TitleBar.Handles _titleBar = null!;
    private Grid _videoStage = null!;
    private Border _videoCard = null!;
    private Grid _frameHost = null!;
    private MediaElement _media = null!;
    private Image _frameImage = null!;
    private Border _playOverlay = null!;
    private Border _volumePill = null!;
    private System.Windows.Shapes.Path _speakerIcon = null!;
    private MiniSlider _volumeSlider = null!;
    private TextBlock _frameBadge = null!;
    private Canvas _subtitleCanvas = null!;
    private System.Windows.Shapes.Path _subtitlePath = null!;

    private TextBlock _timestamp = null!;
    private Border _playButton = null!;
    private System.Windows.Shapes.Path _playButtonIcon = null!;

    private TimelineOverview _overview = null!;
    private TimelineScrubber _scrubber = null!;
    private TextBlock _zoomLabel = null!;

    private TextBox _startBox = null!, _endBox = null!;
    private UIElement _startMagnet = null!, _endMagnet = null!;
    private TextBlock _lengthLabel = null!;
    private ToggleSwitch _snapToggle = null!;
    private Button _addButton = null!;

    private TextBlock _listHeader = null!, _listTotal = null!, _summary = null!;
    private StackPanel _rangeRows = null!;

    /// <summary>Probes the file and, if it can be previewed, shows the player
    /// modally. Returns once the dialog has closed. Probe failures are
    /// reported through the app's own styled dialog (not a native OS message
    /// box) so every popup in the app - including this one, raised before the
    /// player window itself exists - looks like the same window.</summary>
    public static void TryOpen(Window owner, Action<bool> ownerDimmer, string filePath,
        string initialRangesText, Action<string> onApply)
    {
        ProbeResult probe;
        try
        {
            probe = FfmpegEngine.ProbeFull(filePath, pad: false);
        }
        catch (Exception e)
        {
            ConfirmDialogs.ShowMessage(owner, ownerDimmer, DialogKind.Error,
                Loc.T("CouldNotOpenFileTitle"), string.Format(Loc.T("FailedToReadVideoInfoMessage"), e.Message), Loc.T("OK"));
            return;
        }
        if (probe.Duration <= 0)
        {
            ConfirmDialogs.ShowMessage(owner, ownerDimmer, DialogKind.Error,
                Loc.T("CouldNotOpenFileTitle"), Loc.T("NoDurationMessage"), Loc.T("OK"));
            return;
        }

        var window = new PreviewPlayerWindow(owner, filePath, initialRangesText, onApply, probe);
        ownerDimmer(true);
        try { window.ShowDialog(); }
        finally { ownerDimmer(false); }
    }

    private PreviewPlayerWindow(Window owner, string filePath, string initialRangesText,
        Action<string> onApply, ProbeResult probe)
    {
        _onApply = onApply;
        _filePath = filePath;
        _duration = probe.Duration;
        _frameStep = probe.Video.Fps > 1.0 ? 1.0 / probe.Video.Fps : 0.04;
        _viewCenter = _duration / 2.0;
        _maxZoom = Math.Pow(2, Math.Floor(Math.Log2(Math.Max(1.0, _duration / MinViewSpanSeconds))));

        _subtitleCues = new List<SubtitleCue>();
        _subtitleCodec = probe.Subtitle?.Codec;

        _videoW = probe.Video.Width;
        _videoH = probe.Video.Height;
        double scale = Math.Min((double)MaxPreviewW / _videoW, (double)MaxPreviewH / _videoH);
        scale = Math.Min(scale, 1.0);
        _previewW = Math.Max(160, (int)(_videoW * scale));
        _previewH = Math.Max(90, (int)(_videoH * scale));
        _naturalPreviewH = _previewH;

        _fetcher = new FrameFetcher(filePath, _previewW, OnFrameReady);
        _thumbs = new ThumbnailStrip(filePath, OnThumbnailReady);

        // 40ms rather than one-per-frame: this only moves a playhead and a
        // timestamp, so matching the video's frame rate would burn layout
        // passes for motion no one can see.
        _playTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _playTimer.Tick += (_, _) => SyncFromMedia();

        Title = string.Format(Loc.T("TrimRangeTitle"), IoPath.GetFileName(filePath));
        Background = Theme.BgBrush;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        // Never SizeToContent.WidthAndHeight - measuring width against
        // infinity is what locked MainWindow at a screen-filling size once
        // (gotcha #11 in CLAUDE.md). Only the height is measured here, and
        // every descendant therefore always sees a real, finite width.
        Width = 980;
        MinWidth = 780;
        SizeToContent = SizeToContent.Height;

        Icon = Paths.LoadAppIcon();

        // Same custom chrome as MainWindow (see its constructor and
        // PROJECT_STATE.md's redesign phase 0 decision 4) - this window was
        // still showing the native white title bar, which is what Bartek
        // flagged as missing here. WindowChrome gives drag/double-click
        // -maximize/Aero Snap for free; NativeChrome.Apply adds the rounded
        // corners, dark non-client frame and the WM_GETMINMAXINFO fix so
        // maximizing here doesn't cover the taskbar either.
        WindowStyle = WindowStyle.None;
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = Theme.TitleBarHeight,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
        SourceInitialized += (_, _) => NativeChrome.Apply(this);

        BuildUi();
        StartMedia(filePath);
        LoadKeyframesAsync();
        LoadSubtitlesAsync();

        _ranges.AddRange(ParseRangesLoosely(initialRangesText));
        BeginNewRange();
        UpdateSubtitleOverlay(_playheadT);
        RequestFrame(_playheadT);

        Loaded += OnWindowLoaded;
        StateChanged += (_, _) => TitleBar.RefreshMaximizeGlyph(_titleBar, WindowState);
        Closed += (_, _) =>
        {
            _closed = true;
            _playTimer.Stop();
            try { _media.Stop(); _media.Close(); } catch (InvalidOperationException) { /* never opened */ }
            _fetcher.Stop();
            _thumbs.Stop();
        };
        PreviewKeyDown += OnWindowKeyDown;
    }

    /// <summary>Freezes the auto-measured height as the starting size, then
    /// releases the video card's measuring floor so the window can still be
    /// dragged shorter afterwards - the video is the one section that can
    /// give up space without losing information.</summary>
    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        double h = ActualHeight;
        // Never taller than the display it opened on: MinHeight is exactly
        // what makes a window unshrinkable, so on a short screen the bottom
        // would hang off the desktop with no way to drag it back. The work
        // area is the one belonging to the monitor this window is on, not
        // the primary monitor's.
        double cap = WindowPlacement.WorkAreaFor(this).Height - 48;
        if (cap > 0 && h > cap) h = cap;
        Height = h;
        _videoStage.Height = double.NaN;
        _videoStage.MinHeight = VideoFloorH;
        MinHeight = Math.Max(520, h - Math.Max(0, _naturalPreviewH - VideoFloorH));
        SizeToContent = SizeToContent.Manual;
        // This window is ~865 DIP tall against a 912 DIP work area, so
        // CenterOwner leaves it barely half a title bar of slack and any
        // owner sitting even slightly low pushes the Apply / Add range /
        // Cancel row off the bottom. Pinning the top spends all of that
        // slack downwards instead. Must come after the height above is
        // settled - see WindowPlacement.PinToTop.
        WindowPlacement.PinToTop(this);
        _scrubber.RebuildFilmstrip();
    }

    // -- UI construction ---------------------------------------------------------

    private void BuildUi()
    {
        var grid = new Grid { Margin = new Thickness(Theme.PagePad, 14, Theme.PagePad, 16) };
        var rows = new UIElement[]
        {
            BuildVideoCard(),
            BuildTimestamp(),
            BuildTransport(),
            BuildOverviewHeader(),
            BuildOverviewBar(),
            BuildScrubber(),
            BuildRangeFields(),
            BuildRangePanel(),
            BuildHint(),
            BuildActionRow(),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });
            Grid.SetRow(rows[i], i);
            grid.Children.Add(rows[i]);
        }
        // The custom title bar (see MainWindow/Controls/TitleBar.cs) as its
        // own row above the margined content grid, not inside it - it has
        // to run edge-to-edge, unlike everything in "grid" which sits
        // inside Theme.PagePad margins.
        var (titleBarElement, titleBarHandles) = TitleBar.Build(this);
        _titleBar = titleBarHandles;
        int dash = Title.IndexOf(" - ", StringComparison.Ordinal);
        _titleBar.Title.Text = dash < 0 ? Title : Title[..dash];
        _titleBar.Subtitle.Text = dash < 0 ? "" : Title[(dash + 3)..];

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBarElement, 0);
        outer.Children.Add(titleBarElement);
        Grid.SetRow(grid, 1);
        outer.Children.Add(grid);

        // Same sibling arrangement as MainWindow's content/overlay pair (see
        // its constructor comment): "outer" (title bar + content) gets
        // blurred by SetDimmed for this window's own "No ranges added"
        // confirmation, and the overlay sits next to it, unblurred, on top.
        _contentRoot = outer;
        var contentHost = new Grid();
        contentHost.Children.Add(outer);
        _dimOverlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        contentHost.Children.Add(_dimOverlay);
        Content = contentHost;
    }

    private UIElement BuildVideoCard()
    {
        // The stage is the full-width Star row; the card inside it is only as
        // wide as the picture's own aspect ratio needs at the height the row
        // gives it. Letting the card stretch instead would put ~400 DIP of
        // dead black either side of a 16:9 source in a window this wide,
        // since the window has to be wide for the timelines and can only be
        // so tall.
        _videoStage = new Grid
        {
            // An EXPLICIT height, not a minimum, for the auto-measuring pass:
            // once MediaElement has opened the file it reports the video's
            // own natural size, and a Uniform stretch of a 1080p source
            // across this window measures ~530 DIP tall, which
            // SizeToContent.Height would bake into a window taller than the
            // screen. OnWindowLoaded clears this the moment the natural
            // height has been captured, after which the Star row governs and
            // the picture grows with the window.
            Height = _previewH,
        };
        _videoStage.SizeChanged += (_, e) =>
        {
            double h = e.NewSize.Height, w = e.NewSize.Width;
            if (h < 20 || w < 20) return;
            // Loop-free: the stage's size comes from the grid row, never from
            // this child, so writing the child's width back cannot resize it.
            _videoCard.Width = Math.Min(w, h * ((double)_videoW / _videoH));
        };

        _videoCard = new Border
        {
            Background = Theme.VideoBgBrush,
            CornerRadius = new CornerRadius(Theme.PanelRadius),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _frameHost = new Grid { ClipToBounds = true, Background = Theme.TransparentBrush };

        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            // Without this a paused MediaElement shows nothing at all after a
            // seek - the whole scrub-to-a-frame interaction depends on it.
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            Volume = 0,
            Visibility = Visibility.Collapsed,
        };
        _frameImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subtitleCanvas = new Canvas { IsHitTestVisible = false };
        _subtitlePath = new System.Windows.Shapes.Path
        {
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _subtitleCanvas.Children.Add(_subtitlePath);
        _playOverlay = BuildPlayOverlay();

        _frameHost.Children.Add(_media);
        _frameHost.Children.Add(_frameImage);
        _frameHost.Children.Add(_subtitleCanvas);
        _frameHost.Children.Add(BuildFrameBadge());
        _frameHost.Children.Add(BuildVolumePill());
        _frameHost.Children.Add(_playOverlay);
        _frameHost.SizeChanged += OnFrameHostResize;
        // Clicking the picture toggles playback the way every player does -
        // but only when the press did not start inside one of the floating
        // controls, which a bare check of the sender cannot tell apart.
        _frameHost.MouseLeftButtonDown += (_, e) =>
        {
            if (UiHelpers.IsInside(e.OriginalSource as DependencyObject, _volumePill)) return;
            e.Handled = true;
            TogglePlay();
        };

        _videoCard.Child = _frameHost;
        _videoStage.Children.Add(_videoCard);
        return _videoStage;
    }

    /// <summary>The big centered play disc shown over a paused picture.</summary>
    private Border BuildPlayOverlay()
    {
        var disc = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            Background = new SolidColorBrush(Color.FromArgb(0xd0, Theme.BorderCard.R, Theme.BorderCard.G, Theme.BorderCard.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new System.Windows.Shapes.Path
            {
                // Nudged right by a couple of units: a triangle's visual
                // center of mass sits left of its bounding box, so centering
                // the box makes the glyph look off to the left.
                Data = Geometry.Parse("M14,8 L34,20 L14,32 Z"),
                Fill = Theme.FgBrush,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 44,
                Height = 40,
                IsHitTestVisible = false,
            },
        };
        disc.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePlay(); };
        disc.ToolTip = Loc.T("PlayTooltip");
        System.Windows.Automation.AutomationProperties.SetName(disc, Loc.T("PlayTooltip"));
        return disc;
    }

    private UIElement BuildFrameBadge()
    {
        _frameBadge = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.BarFontSize,
        };
        return new Border
        {
            Background = Theme.OverlayPillBrush,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(12, 0, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = _frameBadge,
        };
    }

    private UIElement BuildVolumePill()
    {
        var vol = UiHelpers.BuildVolumeControl(22, 74, 8, Theme.FgBrush, _volumeLevel,
            () => _muted,
            () => { _muted = !_muted; ApplyVolume(); },
            v => { _volumeLevel = v; _muted = false; ApplyVolume(); });
        _speakerIcon = vol.Icon;
        _volumeSlider = vol.Slider;

        _volumePill = new Border
        {
            Background = Theme.OverlayPillBrush,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(8, 4, 12, 4),
            Margin = new Thickness(0, 12, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = vol.Row,
        };
        return _volumePill;
    }

    private UIElement BuildTimestamp()
    {
        _timestamp = new TextBlock
        {
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.MonoFontSize,
            Foreground = Theme.FgDimBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        return _timestamp;
    }

    /// <summary>
    /// The transport, as ONE segmented control rather than three separate
    /// pills with two round buttons floating between them.
    ///
    /// Mark in, play and Mark out are the three segments the mockup shows.
    /// The frame-step buttons are kept - stepping a frame at a time is how a
    /// cut actually gets placed, and dropping it would be a functional loss,
    /// not a simplification - but they join the same control as narrow
    /// icon-only segments flanking play instead of drifting beside it. The
    /// result is one shape with five parts, which is what "segmented" means;
    /// the mockup simply did not draw the two small ones.
    /// </summary>
    private UIElement BuildTransport()
    {
        var segments = new StackPanel { Orientation = Orientation.Horizontal };

        segments.Children.Add(MakeSegment(Loc.T("MarkInLabel"), Loc.T("MarkInTooltip"), MarkIn));
        segments.Children.Add(MakeSegmentDivider());
        segments.Children.Add(MakeIconSegment(Icons.SkipBack, Loc.T("PreviousFrameTooltip"), () => Skip(-_frameStep)));

        _playButtonIcon = new System.Windows.Shapes.Path
        {
            Data = Icons.PlaySolid,
            Fill = Theme.OnAccentBrush,
            Stretch = Stretch.None,
            Width = Icons.Box,
            Height = Icons.Box,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _playButton = new Border
        {
            Width = 56,
            Height = 38,
            CornerRadius = new CornerRadius(Theme.ButtonRadius),
            Background = Theme.AccentGradientBrush,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = _playButtonIcon,
        };
        _playButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePlay(); };
        _playButton.MouseEnter += (_, _) => { if (_mediaReady) _playButton.Background = Theme.AccentGold300Brush; };
        _playButton.MouseLeave += (_, _) => { if (_mediaReady) _playButton.Background = Theme.AccentGradientBrush; };
        segments.Children.Add(_playButton);

        segments.Children.Add(MakeIconSegment(Icons.SkipForward, Loc.T("NextFrameTooltip"), () => Skip(_frameStep)));
        segments.Children.Add(MakeSegmentDivider());
        segments.Children.Add(MakeSegment(Loc.T("MarkOutLabel"), Loc.T("MarkOutTooltip"), MarkOut));

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(Theme.ButtonRadius + 5),
            Background = Theme.BgCardBrush,
            BorderBrush = Theme.BorderSoftBrush,
            BorderThickness = new Thickness(1),
            Child = segments,
        };
    }

    /// <summary>
    /// A labelled segment. The label arrives from <see cref="Loc"/> with its
    /// keyboard hint attached across a "·" - and the two keys sit on OPPOSITE
    /// sides: "Mark in · I" but "O · Mark out", because the old pair mirrored
    /// outward from the play button. So the key is identified by being the
    /// short half rather than by which side it is on, and the segment always
    /// renders label-then-cap. The separator is a literal in all thirteen
    /// translations, which is what makes the split safe to do generically.
    /// </summary>
    private static Border MakeSegment(string label, string tip, Action onClick)
    {
        string text = label;
        string? key = null;
        int dot = label.IndexOf('·');
        if (dot > 0)
        {
            string left = label[..dot].Trim();
            string right = label[(dot + 1)..].Trim();
            bool keyIsLeft = left.Length <= 2 && right.Length > 2;
            key = keyIsLeft ? left : right;
            text = keyIsLeft ? right : left;
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Theme.TextHiBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(key))
        {
            content.Children.Add(new Border
            {
                Background = Theme.BgCardHl2Brush,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = key,
                    Foreground = Theme.TextLowBrush,
                    FontFamily = Theme.MonoFontFamily,
                    FontSize = Theme.LabelFontSize,
                },
            });
        }

        return MakeSegmentShell(content, tip, new Thickness(14, 0, 14, 0), onClick);
    }

    private static Border MakeIconSegment(Geometry icon, string tip, Action onClick) =>
        MakeSegmentShell(Icons.Filled(icon, Theme.TextMidBrush), tip, new Thickness(8, 0, 8, 0), onClick);

    private static Border MakeSegmentShell(UIElement content, string tip, Thickness padding, Action onClick)
    {
        var seg = new Border
        {
            Height = 38,
            CornerRadius = new CornerRadius(Theme.ButtonRadius - 2),
            Background = Theme.TransparentBrush,
            Padding = padding,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = content,
        };
        System.Windows.Automation.AutomationProperties.SetName(seg, tip);
        seg.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        // Transparent at rest, so the segments read as one bar; the hover
        // wash is what tells you the parts are separately clickable.
        seg.MouseEnter += (_, _) => seg.Background = Theme.BgCardHlBrush;
        seg.MouseLeave += (_, _) => seg.Background = Theme.TransparentBrush;
        return seg;
    }

    private static UIElement MakeSegmentDivider() => new Border
    {
        Width = 1,
        Height = 22,
        Background = Theme.BorderBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(2, 0, 2, 0),
    };

    private UIElement BuildOverviewHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = Loc.T("FullClipOverviewLabel"),
            Foreground = Theme.FgFaintBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.LabelFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(label);

        _zoomLabel = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.BarFontSize,
            MinWidth = 34,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };

        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal };
        zoomRow.Children.Add(MakeChipButton(Icons.Minus, null, Loc.T("ZoomOutTooltip"), () => ZoomStep(-1, _playheadT)));
        zoomRow.Children.Add(_zoomLabel);
        zoomRow.Children.Add(MakeChipButton(Icons.Plus, null, Loc.T("ZoomInTooltip"), () => ZoomStep(1, _playheadT)));
        zoomRow.Children.Add(MakeChipButton(null, Loc.T("FitLabel"), Loc.T("FitTooltip"), () => SetZoom(1.0, _viewCenter)));
        Grid.SetColumn(zoomRow, 1);
        grid.Children.Add(zoomRow);
        return grid;
    }

    /// <summary>Small icon-or-text chip used by the zoom controls.</summary>
    private static Border MakeChipButton(Geometry? icon, string? text, string tip, Action onClick)
    {
        // A bare icon chip stays the muted grey the zoom controls have always
        // been; a word-only chip takes the link color.
        UIElement? glyph = icon is not null
            ? Icons.Stroked(icon, Theme.FgDimBrush, 1.6)
            : null;
        TextBlock? label = text is null ? null : new TextBlock
        {
            Text = text,
            Foreground = Theme.FgLinkBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        UIElement child = (UIElement?)glyph ?? label!;

        // An icon on its own is centered in a square; anything with a label
        // needs breathing room either side of the text.
        double padH = label is null ? 0 : 8;
        var btn = new Border
        {
            MinWidth = 28,
            Height = 24,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Background = Theme.TransparentBrush,
            Padding = new Thickness(padH, 0, padH, 0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = child,
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, tip);
        btn.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        btn.MouseEnter += (_, _) => btn.Background = Theme.IconHoverBgBrush;
        btn.MouseLeave += (_, _) => btn.Background = Theme.TransparentBrush;
        return btn;
    }

    private UIElement BuildOverviewBar()
    {
        _overview = new TimelineOverview { Margin = new Thickness(0, 8, 0, 0) };
        _overview.ViewCenterRequested += t =>
        {
            _viewCenter = t;
            RefreshTimelines();
        };
        return _overview;
    }

    private UIElement BuildScrubber()
    {
        _scrubber = new TimelineScrubber(_thumbs, _videoW, _videoH) { Margin = new Thickness(0, 6, 0, 0) };
        _scrubber.Grabbed += () => { if (_playing) Pause(); };
        _scrubber.SeekRequested += SeekTo;
        _scrubber.RangeEdited += (a, b) =>
            SetRange(a, b, _scrubber.Selected == ScrubberGrip.In ? a : b);
        _scrubber.ZoomStepRequested += ZoomStep;
        _scrubber.PreviewKeyDown += OnScrubberKeyDown;
        return _scrubber;
    }

    private UIElement BuildRangeFields()
    {
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < 6; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });

        (_startBox, _startMagnet, var startField) = MakeTimeField();
        _startBox.LostFocus += (_, _) => CommitField(isStart: true);
        _startBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitField(isStart: true); e.Handled = true; } };
        AddLabeledColumn(grid, 0, Loc.T("FieldStartLabel"), startField, new Thickness(0, 0, 16, 0));

        (_endBox, _endMagnet, var endField) = MakeTimeField();
        _endBox.LostFocus += (_, _) => CommitField(isStart: false);
        _endBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitField(isStart: false); e.Handled = true; } };
        AddLabeledColumn(grid, 1, Loc.T("FieldEndLabel"), endField, new Thickness(0, 0, 22, 0));

        _lengthLabel = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.MonoFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 34,
        };
        AddLabeledColumn(grid, 2, Loc.T("FieldLengthLabel"), _lengthLabel, new Thickness(0));

        var snapRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 18, 4),
        };
        snapRow.Children.Add(new TextBlock
        {
            Text = Loc.T("SnapToKeyframesLabel"),
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        _snapToggle = new ToggleSwitch(true)
        {
            IsEnabled = false,
            ToolTip = Loc.T("ReadingKeyframesTooltip"),
        };
        _snapToggle.Toggled += on => { _scrubber.SnapEnabled = on; RefreshReadouts(); };
        snapRow.Children.Add(_snapToggle);
        Grid.SetColumn(snapRow, 4);
        grid.Children.Add(snapRow);

        _addButton = UiHelpers.CreateFlatButton(Loc.T("AddRangeLabel"), Theme.FgAccentBrush, Theme.DarkTextBrush,
            Theme.HoverAccentBrush, padH: 18, padV: 10, icon: Icons.Plus);
        _addButton.VerticalAlignment = VerticalAlignment.Bottom;
        _addButton.ToolTip = Loc.T("AddRangeTooltip");
        _addButton.Click += (_, _) => BeginNewRange();
        Grid.SetColumn(_addButton, 5);
        grid.Children.Add(_addButton);

        return grid;
    }

    private static void AddLabeledColumn(Grid grid, int column, string label, UIElement field, Thickness margin)
    {
        var stack = new StackPanel { Margin = margin, VerticalAlignment = VerticalAlignment.Bottom };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Theme.FgFaintBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.LabelFontSize,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 5),
        });
        stack.Children.Add(field);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    /// <summary>A time entry pill: the editable box plus the magnet badge
    /// that lights up when the value sits exactly on a keyframe. The badge
    /// lives INSIDE the pill and is hidden rather than collapsed, so showing
    /// and hiding it never reflows the row.</summary>
    private static (TextBox Box, UIElement Magnet, UIElement Field) MakeTimeField()
    {
        var box = new TextBox
        {
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.MonoFontSize,
            Background = Theme.TransparentBrush,
            Foreground = Theme.FgLinkBrush,
            CaretBrush = Theme.FgLinkBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam,
        };

        var magnet = Icons.Stroked(Icons.Magnet, Theme.FgLinkBrush, 1.4);
        magnet.Visibility = Visibility.Hidden;
        magnet.ToolTip = Loc.T("MagnetTooltip");
        magnet.IsHitTestVisible = true;

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.Children.Add(box);
        Grid.SetColumn(magnet, 1);
        inner.Children.Add(magnet);

        var pill = new Border
        {
            Width = 132,
            Height = 34,
            Background = Theme.LogBgBrush,
            BorderBrush = Theme.BorderCardBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Padding = new Thickness(11, 0, 8, 0),
            Child = inner,
        };
        return (box, magnet, pill);
    }

    private UIElement BuildRangePanel()
    {
        _listHeader = new TextBlock
        {
            Foreground = Theme.FgBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _listTotal = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.BarFontSize,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new Grid { Margin = new Thickness(4, 0, 4, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_listHeader);
        Grid.SetColumn(_listTotal, 1);
        header.Children.Add(_listTotal);

        _rangeRows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _rangeRows,
            MaxHeight = RangeListMaxH,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Theme.TransparentBrush,
            BorderThickness = new Thickness(0),
        };
        scroll.Resources.Add(typeof(ScrollBar), ScrollBarStyles.Thin);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(scroll);

        return new Border
        {
            Background = Theme.BgPanelBrush,
            BorderBrush = Theme.BorderCardBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.PanelRadius),
            Padding = new Thickness(12, 12, 12, 12),
            Margin = new Thickness(0, 16, 0, 0),
            Child = stack,
        };
    }

    private UIElement BuildHint()
    {
        return new TextBlock
        {
            Text = Loc.T("TrimHintText"),
            Foreground = Theme.FgFaintBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 12, 0, 0),
        };
    }

    private UIElement BuildActionRow()
    {
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _summary = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(_summary);

        var cancel = UiHelpers.CreateFlatButton(Loc.T("Cancel"), Theme.BgRowBrush, Theme.FgBrush, Theme.BgRowHlBrush);
        cancel.Click += (_, _) => OnCancel();
        Grid.SetColumn(cancel, 1);
        grid.Children.Add(cancel);

        var apply = UiHelpers.CreateFlatButton(Loc.T("ApplyLabel"), Theme.FgAccentBrush, Theme.DarkTextBrush, Theme.HoverAccentBrush);
        apply.Margin = new Thickness(10, 0, 0, 0);
        apply.Click += (_, _) => OnApplyClicked();
        Grid.SetColumn(apply, 2);
        grid.Children.Add(apply);

        return grid;
    }

    // -- playback -------------------------------------------------------------------

    private void StartMedia(string filePath)
    {
        _media.MediaOpened += (_, _) =>
        {
            if (_closed) return;
            _mediaReady = true;
            _media.Pause();
            ApplyVolume();
            _media.Position = TimeSpan.FromSeconds(_playheadT);
            _media.Visibility = Visibility.Visible;
            _frameImage.Visibility = Visibility.Collapsed;
            // The ffmpeg-per-frame fallback is dead weight for the big
            // picture once the decoder is up. The filmstrip's own strip
            // keeps running - that one always goes through ffmpeg.
            _fetcher.Stop();
            UpdatePlayAffordances();
        };
        _media.MediaFailed += (_, _) =>
        {
            if (_closed) return;
            // Windows has no decoder for this file. Everything except
            // playback still works through the ffmpeg frame path.
            _mediaReady = false;
            _playing = false;
            _playTimer.Stop();
            _media.Visibility = Visibility.Collapsed;
            _frameImage.Visibility = Visibility.Visible;
            RequestFrame(_playheadT);
            UpdatePlayAffordances();
        };
        _media.MediaEnded += (_, _) => Pause();

        try
        {
            _media.Source = new Uri(filePath);
            // Manual LoadedBehavior means the source is not opened until
            // playback is asked for, so this Play() is what triggers the
            // decode; MediaOpened pauses it again before a frame is shown.
            // Volume starts at 0 so nothing is audible in that window.
            _media.Play();
        }
        catch (Exception)
        {
            _mediaReady = false;
        }
        UpdatePlayAffordances();
    }

    private void ApplyVolume()
    {
        _speakerIcon.Data = _muted || _volumeLevel <= 0.001 ? Icons.SpeakerMuted : Icons.Speaker;
        _volumeSlider.Value = _volumeLevel;
        if (_mediaReady) _media.Volume = _muted ? 0.0 : _volumeLevel;
    }

    private void TogglePlay()
    {
        if (!_mediaReady) return;
        if (_playing) Pause(); else Play();
    }

    private void Play()
    {
        if (!_mediaReady || _closed) return;
        if (_playheadT >= _duration - 0.05) SeekTo(0);
        _media.Play();
        _playing = true;
        _playTimer.Start();
        UpdatePlayAffordances();
    }

    private void Pause()
    {
        if (!_playing && !_mediaReady) return;
        _playTimer.Stop();
        _playing = false;
        if (_mediaReady)
        {
            _media.Pause();
            SyncFromMedia();
        }
        UpdatePlayAffordances();
    }

    private void SyncFromMedia()
    {
        if (_closed || !_mediaReady) return;
        _playheadT = Math.Clamp(_media.Position.TotalSeconds, 0.0, _duration);
        UpdateSubtitleOverlay(_playheadT);
        FollowPlayhead();
        RefreshReadouts();
        RefreshTimelines();
    }

    /// <summary>Scrolls the zoomed track to keep up with playback. Before the
    /// redesign the one timeline always showed the whole file, so the
    /// playhead could never leave it; now that the scrubber is a window onto
    /// the file, playing past its right edge would leave the track showing
    /// somewhere the video no longer is. Jumps rather than creeps, and only
    /// once the playhead reaches the outer tenth, so a viewport that is
    /// already following does not shuffle on every tick.</summary>
    private void FollowPlayhead()
    {
        double span = ViewSpan;
        if (span >= _duration) return;
        double margin = span * 0.1;
        if (_playheadT > ViewStart + margin && _playheadT < ViewEnd - margin) return;
        _viewCenter = _playheadT + span * 0.3;
    }

    private void Skip(double delta) => SeekTo(Math.Clamp(_playheadT + delta, 0.0, _duration));

    /// <summary>Moves the playhead and shows the picture there, through
    /// whichever source is live.</summary>
    private void SeekTo(double t)
    {
        _playheadT = Math.Clamp(t, 0.0, _duration);
        if (_mediaReady) _media.Position = TimeSpan.FromSeconds(_playheadT);
        else RequestFrame(_playheadT);
        UpdateSubtitleOverlay(_playheadT);
        RefreshReadouts();
        RefreshTimelines();
    }

    /// <summary>Keeps the play button, the overlay disc and the button glyph
    /// telling the same story about what playback can currently do.</summary>
    private void UpdatePlayAffordances()
    {
        _playButtonIcon.Data = _playing ? Icons.PauseBars : Icons.PlaySolid;
        _playButton.Background = _mediaReady ? Theme.AccentGradientBrush : Theme.DisabledBgBrush;
        _playButtonIcon.Fill = _mediaReady ? Theme.OnAccentBrush : Theme.DisabledFgBrush;
        _playButton.Cursor = _mediaReady ? Cursors.Hand : Cursors.Arrow;
        _playButton.ToolTip = _mediaReady
            ? (_playing ? Loc.T("PauseTooltip") : Loc.T("PlayTooltip"))
            : Loc.T("PlaybackUnavailableTooltip");
        System.Windows.Automation.AutomationProperties.SetName(_playButton, _playing ? Loc.T("PauseTooltip") : Loc.T("PlayTooltip"));
        _playOverlay.Visibility = _mediaReady && !_playing ? Visibility.Visible : Visibility.Collapsed;
        _volumePill.Visibility = _mediaReady ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { OnCancel(); return; }
        // None of these may steal the keystroke while a time field is being
        // typed into, or the user cannot enter a value containing them and
        // loses the caret to a transport action instead.
        if (_startBox.IsKeyboardFocusWithin || _endBox.IsKeyboardFocusWithin) return;

        switch (e.Key)
        {
            case Key.Space: TogglePlay(); e.Handled = true; break;
            case Key.I: MarkIn(); e.Handled = true; break;
            case Key.O: MarkOut(); e.Handled = true; break;
        }
    }

    // -- zoom / viewport -----------------------------------------------------------

    private double ViewSpan => Math.Min(_duration, _duration / _zoom);
    private double ViewStart => Math.Clamp(_viewCenter - ViewSpan / 2.0, 0, Math.Max(0, _duration - ViewSpan));
    private double ViewEnd => ViewStart + ViewSpan;

    /// <summary>Changes the zoom level while keeping <paramref name="anchorT"/>
    /// under the same pixel, so wheel-zooming homes in on what the pointer is
    /// over instead of on the middle of the track.</summary>
    private void SetZoom(double zoom, double anchorT)
    {
        double clamped = Math.Clamp(zoom, 1.0, _maxZoom);
        double oldSpan = ViewSpan;
        double frac = oldSpan > 0 ? Math.Clamp((anchorT - ViewStart) / oldSpan, 0, 1) : 0.5;
        _zoom = clamped;
        double newSpan = ViewSpan;
        _viewCenter = anchorT - frac * newSpan + newSpan / 2.0;
        RefreshTimelines();
    }

    private void ZoomStep(int direction, double anchorT) =>
        SetZoom(direction > 0 ? _zoom * 2.0 : _zoom / 2.0, anchorT);

    /// <summary>Brings a range fully into the zoomed track, with a little
    /// room either side so its grips are reachable.</summary>
    private void CenterOn(TimeRange range)
    {
        double want = Math.Max(range.Length * 1.4, MinViewSpanSeconds * 2);
        double z = Math.Clamp(_duration / want, 1.0, _maxZoom);
        _zoom = Math.Pow(2, Math.Floor(Math.Log2(z)));
        _viewCenter = (range.Start + range.End) / 2.0;
    }

    private void EnsureVisible(double t)
    {
        if (t >= ViewStart && t <= ViewEnd) return;
        _viewCenter = t;
    }

    // -- range model ------------------------------------------------------------------

    private static List<TimeRange> ParseRangesLoosely(string text)
    {
        var ranges = new List<TimeRange>();
        foreach (var chunkRaw in text.Split(','))
        {
            var chunk = chunkRaw.Trim();
            int dash = chunk.IndexOf('-');
            if (chunk.Length == 0 || dash < 0) continue;
            try
            {
                double start = TimeUtils.ParseTime(chunk[..dash]);
                double end = TimeUtils.ParseTime(chunk[(dash + 1)..]);
                if (end > start) ranges.Add(new TimeRange(start, end));
            }
            catch (FormatException) { /* silently skipped, matches the Python original */ }
        }
        return ranges;
    }

    /// <summary>Rows that count: everything except the zero-length slot the
    /// current selection may still be sitting on.</summary>
    private IEnumerable<int> RealRangeIndexes()
    {
        for (int i = 0; i < _ranges.Count; i++)
            if (_ranges[i].Length > MinRangeSeconds)
                yield return i;
    }

    /// <summary>Closes the current row off and opens a fresh one. Per the
    /// spec this never guesses a length: it starts where the last range ends
    /// (or at the playhead when there are none) and is zero-length until the
    /// user marks or drags an out point, instead of the old behaviour of
    /// selecting the entire file and making them shrink it every time.</summary>
    private void BeginNewRange()
    {
        DropEmptyRanges();
        double start = _ranges.Count > 0 ? _ranges.Max(r => r.End) : _playheadT;
        _ranges.Add(new TimeRange(start, start));
        _editIndex = _ranges.Count - 1;
        _inT = _outT = start;
        _scrubber.Selected = ScrubberGrip.Out;
        EnsureVisible(start);
        SeekTo(start);
        RefreshAll();
    }

    /// <summary>Drops every zero-length row, fixing up the edit pointer.
    /// There is normally at most one - the slot a previous "Add range" or a
    /// row click left behind.</summary>
    private void DropEmptyRanges()
    {
        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            if (_ranges[i].Length > MinRangeSeconds) continue;
            _ranges.RemoveAt(i);
            if (i < _editIndex) _editIndex--;
            else if (i == _editIndex) _editIndex = -1;
        }
    }

    /// <summary>Binds in/out to an existing row and brings it into view.</summary>
    private void SelectRange(int index)
    {
        if (index < 0 || index >= _ranges.Count) return;
        var range = _ranges[index];

        int target = index;
        for (int i = _ranges.Count - 1; i >= 0; i--)
        {
            if (i == index || _ranges[i].Length > MinRangeSeconds) continue;
            _ranges.RemoveAt(i);
            if (i < target) target--;
        }

        _editIndex = target;
        _inT = range.Start;
        _outT = range.End;
        _scrubber.Selected = ScrubberGrip.None;
        CenterOn(range);
        SeekTo(range.Start);
        RefreshAll();
    }

    private void RemoveRange(int index)
    {
        if (index < 0 || index >= _ranges.Count) return;
        bool wasEdit = index == _editIndex;
        _ranges.RemoveAt(index);
        if (index < _editIndex) _editIndex--;
        if (wasEdit || _editIndex < 0 || _editIndex >= _ranges.Count)
        {
            _editIndex = -1;
            BeginNewRange();
            return;
        }
        RefreshAll();
    }

    /// <summary>The single write path for in/out. Everything - grips, Mark
    /// in/out, the typed fields - goes through here, so the row being edited
    /// is always exactly what the timelines are drawing.</summary>
    private void SetRange(double newIn, double newOut, double? follow)
    {
        _inT = Math.Clamp(newIn, 0.0, _duration);
        _outT = Math.Clamp(Math.Max(newOut, _inT), 0.0, _duration);
        if (_editIndex >= 0 && _editIndex < _ranges.Count)
            _ranges[_editIndex] = new TimeRange(_inT, _outT);

        if (follow.HasValue) SeekTo(follow.Value);
        RefreshAll();
    }

    /// <summary>Whether the current selection is still the zero-length marker
    /// a fresh range opens as. An empty marker has no direction, so I and O
    /// may both move it; a range that already has length must never be
    /// collapsed by a mark on the wrong side of it, so those clamp instead.</summary>
    private bool EditIsEmpty => (_outT - _inT) <= MinRangeSeconds;

    private void MarkIn()
    {
        double t = _scrubber.Snap(_playheadT);
        if (EditIsEmpty) SetRange(t, Math.Max(_outT, t), null);
        else SetRange(Math.Min(t, _outT), _outT, null);
        _scrubber.Selected = ScrubberGrip.In;
    }

    private void MarkOut()
    {
        double t = _scrubber.Snap(_playheadT);
        if (EditIsEmpty) SetRange(Math.Min(_inT, t), t, null);
        else SetRange(_inT, Math.Max(t, _inT), null);
        _scrubber.Selected = ScrubberGrip.Out;
    }

    /// <summary>Applies a hand-typed Start/End value. An unparseable entry
    /// silently snaps back to the current grip position rather than popping a
    /// dialog - the field is a nudge tool, not a form.</summary>
    private void CommitField(bool isStart)
    {
        var box = isStart ? _startBox : _endBox;
        double parsed;
        try { parsed = TimeUtils.ParseTime(box.Text.Trim()); }
        catch (FormatException) { RefreshReadouts(); return; }

        parsed = Math.Clamp(parsed, 0.0, _duration);
        if (isStart) SetRange(Math.Min(parsed, _outT), _outT, Math.Min(parsed, _outT));
        else SetRange(_inT, Math.Max(parsed, _inT), Math.Max(parsed, _inT));
    }

    private void OnScrubberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Left && e.Key != Key.Right) return;
        double step = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? 0.01
            : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.1 : 1.0;
        double delta = e.Key == Key.Left ? -step : step;

        switch (_scrubber.Selected)
        {
            case ScrubberGrip.In:
                SetRange(Math.Round(_inT + delta, 2), _outT, Math.Min(Math.Round(_inT + delta, 2), _outT));
                break;
            case ScrubberGrip.Out:
                SetRange(_inT, Math.Round(_outT + delta, 2), Math.Max(Math.Round(_outT + delta, 2), _inT));
                break;
            default:
                Skip(delta);
                break;
        }
        e.Handled = true;
    }

    // -- keyframes ----------------------------------------------------------------------

    /// <summary>Reads the file's keyframe list in the background. ffprobe has
    /// to walk every packet of the video stream for this, which is seconds of
    /// work on a long file - far too slow to block the window opening on, so
    /// the ticks, the snapping and the magnet badges all just appear when it
    /// lands.</summary>
    /// <summary>Extracts and parses the subtitle track in the background - see
    /// N11 in AUDIT_TODO.md. This used to happen synchronously in the
    /// constructor via FfmpegEngine.ExtractSubtitleText, which shells out to
    /// ffmpeg to copy the whole subtitle stream out of the file; on a large
    /// file or a network share that stalled the window's very first render
    /// (nothing else in the constructor blocks on I/O like this one did).
    /// Cues that arrive after the window has already been showing for a
    /// moment just start applying on the next overlay refresh - there's no
    /// user-visible "loading subtitles" state to wire up, the same way
    /// LoadKeyframesAsync's keyframes quietly show up once ready.</summary>
    private void LoadSubtitlesAsync()
    {
        string path = _filePath;
        string? codec = _subtitleCodec;
        if (codec is null || !FfmpegEngine.SupportedSubtitleCodecs.Contains(codec)) return;
        _ = Task.Run(() =>
        {
            string? text = null;
            try { text = FfmpegEngine.ExtractSubtitleText(path, codec); } catch { text = null; }
            if (string.IsNullOrEmpty(text)) return;
            var cues = codec == "subrip" ? SubtitleUtils.ParseSrt(text) : SubtitleUtils.ParseAss(text);

            Dispatcher.BeginInvoke(() =>
            {
                if (_closed) return;
                _subtitleCues = cues;
                UpdateSubtitleOverlay(_playheadT);
            });
        });
    }

    private void LoadKeyframesAsync()
    {
        string path = _filePath;
        _ = Task.Run(() =>
        {
            List<double>? keyframes = null;
            try { keyframes = FfmpegEngine.ProbeKeyframeTimes(path); }
            catch { keyframes = null; }

            Dispatcher.BeginInvoke(() =>
            {
                if (_closed) return;
                if (keyframes is null || keyframes.Count == 0)
                {
                    _snapToggle.IsOn = false;
                    _snapToggle.IsEnabled = false;
                    _snapToggle.ToolTip = Loc.T("NoKeyframesTooltip");
                    _scrubber.SnapEnabled = false;
                }
                else
                {
                    _scrubber.SetKeyframes(keyframes);
                    _scrubber.SnapEnabled = _snapToggle.IsOn;
                    _snapToggle.IsEnabled = true;
                    _snapToggle.ToolTip = Loc.T("SnapTooltip");
                }
                RefreshReadouts();
            });
        });
    }

    // -- refresh ---------------------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshReadouts();
        RefreshTimelines();
        RefreshRangeList();
        UpdateAddButtonState();
    }

    private void RefreshTimelines()
    {
        _overview.SetModel(_duration, _ranges, _editIndex, _playheadT, ViewStart, ViewEnd);
        _scrubber.SetModel(_duration, ViewStart, ViewEnd, _ranges, _editIndex, _inT, _outT, _playheadT);
        _zoomLabel.Text = $"{Math.Round(_zoom).ToString(CultureInfo.InvariantCulture)}x";
    }

    private void RefreshReadouts()
    {
        _timestamp.Text = $"{TimeUtils.FormatTime(_playheadT)} / {TimeUtils.FormatTime(_duration)}";
        _frameBadge.Text = $"frame @ {TimeUtils.FormatTime(_playheadT)}";
        _lengthLabel.Text = TimeUtils.FormatTime(Math.Max(0, _outT - _inT));
        // Never overwrite a field the user is in the middle of typing into.
        if (!_startBox.IsKeyboardFocusWithin) _startBox.Text = TimeUtils.FormatTime(_inT);
        if (!_endBox.IsKeyboardFocusWithin) _endBox.Text = TimeUtils.FormatTime(_outT);
        _startMagnet.Visibility = _scrubber.IsOnKeyframe(_inT) ? Visibility.Visible : Visibility.Hidden;
        _endMagnet.Visibility = _scrubber.IsOnKeyframe(_outT) ? Visibility.Visible : Visibility.Hidden;
    }

    private void UpdateAddButtonState() =>
        UiHelpers.SetButtonEnabled(_addButton, (_outT - _inT) > MinRangeSeconds);

    /// <summary>Rebuilds the range rows. Displayed in chronological order but
    /// each row remembers the index it came from, because the list itself is
    /// kept in insertion order - re-sorting the backing list would move the
    /// row out from under the edit pointer mid-drag.</summary>
    private void RefreshRangeList()
    {
        _rangeRows.Children.Clear();

        var shown = RealRangeIndexes().OrderBy(i => _ranges[i].Start).ToList();
        double total = shown.Sum(i => _ranges[i].Length);

        _listHeader.Text = string.Format(Loc.T("RangesCountHeader"), shown.Count);
        _listTotal.Text = string.Format(Loc.T("TotalSuffix"), TimeUtils.FormatTime(total));
        _summary.Text = shown.Count == 1
            ? string.Format(Loc.T("RangeSummarySingular"), TimeUtils.FormatTime(total))
            : string.Format(Loc.T("RangeSummaryPlural"), shown.Count, TimeUtils.FormatTime(total));

        if (shown.Count == 0)
        {
            _rangeRows.Children.Add(new TextBlock
            {
                Text = Loc.T("NoRangesYetText"),
                Foreground = Theme.FgFaintBrush,
                FontFamily = Theme.UiFontFamily,
                FontSize = Theme.UiFontSize,
                Margin = new Thickness(4, 4, 0, 6),
            });
            return;
        }

        foreach (int index in shown)
            _rangeRows.Children.Add(MakeRangeRow(index));
    }

    private UIElement MakeRangeRow(int index)
    {
        var range = _ranges[index];
        bool editing = index == _editIndex;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The SAME chip the main window's file rows use - one component, so
        // the dot, the mono timecode and the pill shape cannot drift between
        // the two lists. The row keeps its own play / edit / delete icons
        // beside the chip rather than putting a ✕ inside it, which would be
        // a second way to do what the delete icon already does.
        var chip = new RangeChip(
            $"{TimeUtils.FormatTime(range.Start)}–{TimeUtils.FormatTime(range.End)}",
            editing ? RangeChipTone.Active : RangeChipTone.Info)
        {
            Margin = new Thickness(0, 0, 10, 0),
        };
        grid.Children.Add(chip);

        if (editing)
        {
            var tag = new TextBlock
            {
                Text = Loc.T("EditingTag"),
                Foreground = Theme.FgFaintBrush,
                FontFamily = Theme.UiFontFamily,
                FontSize = Theme.UiFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(tag, 1);
            grid.Children.Add(tag);
        }

        var length = new TextBlock
        {
            Text = TimeUtils.FormatTime(range.Length),
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.BarFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 10, 0),
        };
        Grid.SetColumn(length, 2);
        grid.Children.Add(length);

        var icons = new StackPanel { Orientation = Orientation.Horizontal };
        icons.Children.Add(MakeRowIcon(Icons.PlaySolid, true, Loc.T("PlayFromHereTooltip"), Theme.FgAccentBrush,
            () => { SeekTo(range.Start); if (_mediaReady) Play(); }));
        icons.Children.Add(MakeRowIcon(Icons.Pencil, false, Loc.T("EditRangeTooltip"), Theme.FgAccentBrush,
            () => SelectRange(index)));
        icons.Children.Add(MakeRowIcon(Icons.Trash, false, Loc.T("RemoveRangeTooltip"), Theme.HoverRedBrush,
            () => RemoveRange(index)));
        Grid.SetColumn(icons, 3);
        grid.Children.Add(icons);

        var row = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            // The left accent stripe is the border, so an editing row picks
            // it up without changing its own height or indent.
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = editing ? Theme.FgAccentBrush : Theme.TransparentBrush,
            Background = editing ? Theme.BgRowHlBrush : Theme.TransparentBrush,
            Padding = new Thickness(9, 0, 6, 0),
            Margin = new Thickness(0, 0, 0, 3),
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("LoadRangeTooltip"),
            Child = grid,
        };
        System.Windows.Automation.AutomationProperties.SetName(row,
            $"{Loc.T("LoadRangeTooltip")}: {chip.Text}");
        if (!editing)
        {
            row.MouseEnter += (_, _) => row.Background = Theme.BgRowBrush;
            row.MouseLeave += (_, _) => row.Background = Theme.TransparentBrush;
        }
        row.MouseLeftButtonDown += (_, e) => { e.Handled = true; SelectRange(index); };
        return row;
    }

    private static Border MakeRowIcon(Geometry geometry, bool filled, string tip, Brush hover, Action onClick)
    {
        var glyph = filled
            ? Icons.Filled(geometry, Theme.FgDimBrush)
            : Icons.Stroked(geometry, Theme.FgDimBrush, 1.4);
        var slot = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = Theme.TransparentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = glyph,
        };
        System.Windows.Automation.AutomationProperties.SetName(slot, tip);
        slot.MouseEnter += (_, _) =>
        {
            slot.Background = Theme.IconHoverBgBrush;
            if (filled) glyph.Fill = hover; else glyph.Stroke = hover;
        };
        slot.MouseLeave += (_, _) =>
        {
            slot.Background = Theme.TransparentBrush;
            if (filled) glyph.Fill = Theme.FgDimBrush; else glyph.Stroke = Theme.FgDimBrush;
        };
        slot.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return slot;
    }

    // -- frame fetching / display ---------------------------------------------------------

    private void RequestFrame(double t)
    {
        if (_mediaReady) return;
        double clamped = Math.Max(0.0, Math.Min(t, Math.Max(0.0, _duration - 0.05)));
        _fetcher.Request(clamped);
    }

    private void OnFrameReady(byte[] data)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _mediaReady) return;
            var bmp = PpmDecoder.Decode(data);
            if (bmp is not null) _frameImage.Source = bmp;
        });
    }

    /// <summary>Called from the thumbnail worker thread whenever one more
    /// filmstrip cell finishes decoding.</summary>
    private void OnThumbnailReady()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _scrubber.InvalidateVisual();
        });
    }

    /// <summary>Keeps the preview picture filling the host, aspect-preserved,
    /// as the window grows or shrinks - matches preview_player.py's
    /// _on_frame_canvas_resize, but additionally positions the subtitle
    /// overlay against the actual displayed picture rectangle instead of the
    /// raw container, so it stays anchored to the bottom of the picture even
    /// when letterboxed (a small inaccuracy the Python original had, since
    /// Tkinter's Canvas doesn't track Image stretch geometry for it
    /// automatically).</summary>
    private void OnFrameHostResize(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width, h = e.NewSize.Height;
        if (w < 40 || h < 40) return;

        // A Border's CornerRadius does not clip what is inside it, and both
        // picture sources are hard rectangles, so the rounding has to be
        // applied here as an explicit clip or the video paints over it.
        _frameHost.Clip = new RectangleGeometry(new Rect(0, 0, w, h), Theme.PanelRadius, Theme.PanelRadius);

        double scale = Math.Min(w / _videoW, h / _videoH);
        int newW = Math.Max(40, (int)(_videoW * scale));
        int newH = Math.Max(40, (int)(_videoH * scale));

        PositionSubtitleOverlay(w, h, newW, newH);

        if (newW == _previewW && newH == _previewH) return;
        _previewW = newW;
        _previewH = newH;
        _fetcher.Width = newW;
        RequestFrame(_playheadT);
    }

    private void PositionSubtitleOverlay(double containerW, double containerH, double imageW, double imageH)
    {
        _subtitleCanvas.Width = containerW;
        _subtitleCanvas.Height = containerH;
        double imgX = (containerW - imageW) / 2.0;
        double imgY = (containerH - imageH) / 2.0;
        _subtitleCenterX = imgX + imageW / 2.0;
        _subtitleBottomY = imgY + imageH - 14;
        _subtitleWrapWidth = Math.Max(60, imageW - 24);
        RenderSubtitleGeometry(_lastSubtitleText);
    }

    private void UpdateSubtitleOverlay(double t)
    {
        if (_subtitleCues.Count == 0) return;
        var text = SubtitleUtils.ActiveCueText(_subtitleCues, t) ?? "";
        if (text == _lastSubtitleText) return;
        _lastSubtitleText = text;
        RenderSubtitleGeometry(text);
    }

    private void RenderSubtitleGeometry(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            _subtitlePath.Data = null;
            return;
        }
        var typeface = new Typeface(Theme.UiFontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double boxW = Math.Max(10, _subtitleWrapWidth);
        var ft = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
            SubtitleFontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = boxW,
            TextAlignment = TextAlignment.Center,
        };
        var origin = new Point(_subtitleCenterX - boxW / 2.0, _subtitleBottomY - ft.Height);
        _subtitlePath.Data = ft.BuildGeometry(origin);
    }

    // -- closing ---------------------------------------------------------------------------

    /// <summary>Exactly the rows the list showed, in chronological order -
    /// including the one still marked "(editing)", because a selection the
    /// user can see on screen going missing on Apply would read as a bug.
    /// The zero-length slot a fresh "Add range" leaves behind is not a row,
    /// so it never reaches this.</summary>
    private string BuildRangesText()
    {
        var ordered = RealRangeIndexes().Select(i => _ranges[i]).OrderBy(r => r.Start);
        return string.Join(", ", ordered.Select(r => $"{TimeUtils.FormatTime(r.Start)}-{TimeUtils.FormatTime(r.End)}"));
    }

    private void SetDimmed(bool on)
    {
        _dimOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _contentRoot.Effect = on ? new BlurEffect { Radius = 16 } : null;
    }

    private void OnApplyClicked()
    {
        if (!RealRangeIndexes().Any())
        {
            bool confirmed = ConfirmDialogs.AskYesNo(this, SetDimmed, DialogKind.Question,
                Loc.T("NoRangesAddedTitle"), Loc.T("NoRangesAddedMessage"), Loc.T("Yes"), Loc.T("No"));
            if (!confirmed) return;
        }
        _onApply(BuildRangesText());
        Close();
    }

    private void OnCancel() => Close();
}
