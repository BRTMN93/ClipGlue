using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClipGlue.Controls;
using ClipGlue.Models;
using IoPath = System.IO.Path;

namespace ClipGlue.Views;

/// <summary>One input file and the ranges kept from it, in list order.</summary>
public sealed record ProjectClip(string Path, IReadOnlyList<TimeRange> Ranges);

/// <summary>
/// Plays the whole project back as if it had already been cut and joined:
/// every kept range of every file, in list order, end to end. It exists so
/// the joins can be judged BEFORE committing to an encode - until now the
/// only way to find out that a cut landed badly was to run the job and watch
/// the output.
///
/// Nothing is encoded and nothing is written to disk. There is no
/// intermediate file at all: one <see cref="MediaElement"/> is steered
/// through the segments, seeking to the next range when the current one runs
/// out and swapping its Source when the next range belongs to a different
/// file. The timeline the user sees and scrubs is the OUTPUT timeline -
/// segment starts are cumulative, so position 00:00:00 is the first frame of
/// the finished clip, not of the first input file.
///
/// Two consequences of steering a real decoder rather than playing a real
/// file, both accepted deliberately:
/// - crossing into a different file reloads the decoder, so there is a short
///   hitch at those joins that the encoded output will not have;
/// - a file Windows has no decoder for (MKV is the usual one - see
///   PreviewPlayerWindow's note on the same problem) cannot be previewed at
///   all. Those segments are skipped with a visible notice instead of
///   stalling the run, since the rest of the project still previews fine.
/// </summary>
public sealed class ProjectPreviewPanel : Border
{
    private const double VideoFloorH = 120;

    /// <summary>Panel MinHeight with the kept/cut strip and its legend
    /// shown, and how much of that is theirs to give back when hidden (strip
    /// margin 6 + height 12, legend margin 6 + its own ~22) - see
    /// SetStripVisible.</summary>
    private const double StripFullMinHeight = 290;
    private const double StripBlockHeight = 46;

    /// <summary>A single stretch of playback: where it lives in a source
    /// file, and where it lands on the output timeline. <paramref
    /// name="EntryIndex"/> is this segment's position in the SegmentStrip's
    /// per-file model (see LoadProject), so playback progress can be mapped
    /// onto that bar's source-time axis.</summary>
    private sealed record Segment(string Path, double Start, double End, double VirtualStart, int EntryIndex)
    {
        public double Length => Math.Max(0, End - Start);
        public double VirtualEnd => VirtualStart + Length;
    }

    private readonly List<Segment> _segments = new();
    private readonly HashSet<string> _undecodable = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _timer;

    private double _total;
    private double _virtualT;
    /// <summary>Current position within the playing segment's SOURCE file,
    /// in seconds - what SegmentStrip.SetProgress needs, as opposed to
    /// _virtualT which is on the output timeline ProjectScrubber uses.</summary>
    private double _currentSourcePos;
    private int _index = -1;
    private string? _loadedPath;
    private double _pendingPosition;
    private bool _pendingPlay;
    private bool _mediaReady;
    private bool _playing;
    private bool _released;
    /// <summary>True while a job is running: a stronger form of _released
    /// that also refuses reloads, so nothing can quietly re-open a decoder
    /// on a file the encoder is reading. See SetLocked.</summary>
    private bool _locked;
    private double _volumeLevel = 1.0;
    private bool _muted;
    private double _aspect = 16.0 / 9.0;

    private readonly Grid _videoStage;
    private readonly Border _videoCard;
    private readonly MediaElement _media;
    private readonly Border _playOverlay;
    private readonly TextBlock _nowPlaying;
    private readonly Border _nowPlayingPill;
    private readonly TextBlock _notice;
    private readonly Border _noticePill;
    private readonly TextBlock _summary;
    private readonly TextBlock _timeLabel;
    private readonly ProjectScrubber _scrubber;
    private readonly SegmentStrip _segmentStrip;
    private UIElement _stripLegend = null!;
    private TextBlock _legendKept = null!, _legendCut = null!;
    /// <summary>Probe generation, so a late duration from a project that has
    /// since been reloaded is dropped instead of repainting the new one.</summary>
    private int _durationEpoch;
    private readonly Border _playButton;
    private readonly System.Windows.Shapes.Path _playIcon;
    private readonly System.Windows.Shapes.Path _speakerIcon;
    private readonly MiniSlider _volumeSlider;
    private readonly Border _volumeSlot;

    // Chrome that needs to be re-pulled from Loc.T(...) on a language
    // switch - unlike PreviewPlayerWindow (a modal rebuilt fresh every open,
    // so it can just read Loc.T at construction time), this panel is built
    // ONCE and stays alive for the app's lifetime while MainWindow - and its
    // language button - remain interactive, so it has to listen for changes.
    private TextBlock _headerLabel = null!;
    private Border _reloadChip = null!, _closeChip = null!;
    private readonly Border _prevSegBtn, _nextSegBtn;
    private int _lastClipCount;

    /// <summary>Raised by the panel's own close button.</summary>
    public event Action? CloseRequested;

    /// <summary>Raised by the panel's reload button - the host owns the
    /// project, so it re-reads the rows and calls <see cref="LoadProject"/>.</summary>
    public event Action? ReloadRequested;

    public ProjectPreviewPanel()
    {
        Background = Theme.BgPanelBrush;
        BorderBrush = Theme.BorderBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Theme.PanelRadius);
        Padding = new Thickness(12, 10, 12, 12);
        // Raised with the kept/cut strip and its legend (bar 12 + gaps 12 +
        // legend ~16). Pitfall #20: the panel is given its share of a Star
        // row, and a child MinHeight does not make the row any taller, so
        // MainWindow.OpenProjectPreview carries a matching RowDefinition
        // MinHeight - both have to move together. Set for real once the strip
        // and legend exist, via SetStripVisible below - StripFullMinHeight is
        // the floor with both shown, StripHiddenMinHeight without.
        MinHeight = StripFullMinHeight;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _summary = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.BarFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        grid.Children.Add(BuildHeader());

        // -- picture ------------------------------------------------------
        _media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            // Same reason as the trim window: without this a paused element
            // shows nothing after a seek, and every segment change is a seek.
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
            Volume = 0,
        };
        _media.MediaOpened += OnMediaOpened;
        _media.MediaFailed += OnMediaFailed;
        _media.MediaEnded += (_, _) => Advance();

        _playOverlay = BuildPlayOverlay();
        _nowPlaying = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.BarFontSize,
        };
        _nowPlayingPill = new Border
        {
            Background = Theme.OverlayPillBrush,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(10, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = _nowPlaying,
        };
        _notice = new TextBlock
        {
            Foreground = Theme.FgBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        };
        _noticePill = new Border
        {
            Background = Theme.OverlayPillBrush,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _notice,
        };

        var host = new Grid { ClipToBounds = true, Background = Theme.TransparentBrush };
        host.Children.Add(_media);
        host.Children.Add(_nowPlayingPill);
        host.Children.Add(_playOverlay);
        host.Children.Add(_noticePill);
        host.SizeChanged += (_, e) =>
        {
            // A Border's CornerRadius does not clip its child and the video
            // is a hard rectangle, so the rounding has to be an explicit
            // clip - same as the trim window's video card.
            if (e.NewSize.Width > 8 && e.NewSize.Height > 8)
                host.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
                    Theme.CardRadius, Theme.CardRadius);
        };
        host.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePlay(); };

        _videoCard = new Border
        {
            Background = Theme.VideoBgBrush,
            CornerRadius = new CornerRadius(Theme.CardRadius),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = host,
        };
        _videoStage = new Grid { Margin = new Thickness(0, 10, 0, 0), MinHeight = VideoFloorH };
        _videoStage.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height < 20 || e.NewSize.Width < 20) return;
            // Aspect-fitted and centred rather than stretched: the main
            // window is much wider than this row is tall, so a stretched
            // card would be mostly dead black either side of the picture.
            _videoCard.Width = Math.Min(e.NewSize.Width, e.NewSize.Height * _aspect);
        };
        _videoStage.Children.Add(_videoCard);
        Grid.SetRow(_videoStage, 1);
        grid.Children.Add(_videoStage);

        // -- seek bar + kept/cut strip ---------------------------------------
        // The two bars answer different questions and are stacked so the
        // difference is visible: the scrubber's axis is the OUTPUT clip (a
        // position you can seek to), the strip's is each INPUT file (how
        // much of it survives). Neither can stand in for the other.
        _scrubber = new ProjectScrubber { Margin = new Thickness(0, 12, 0, 0) };
        _scrubber.SeekRequested += SeekVirtual;

        _segmentStrip = new SegmentStrip { Margin = new Thickness(0, 6, 0, 0) };

        var stripStack = new StackPanel();
        stripStack.Children.Add(_scrubber);
        stripStack.Children.Add(_segmentStrip);
        _stripLegend = BuildStripLegend();
        stripStack.Children.Add(_stripLegend);
        Grid.SetRow(stripStack, 2);
        grid.Children.Add(stripStack);

        // -- transport ------------------------------------------------------
        _timeLabel = new TextBlock
        {
            Foreground = Theme.FgDimBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _playIcon = new System.Windows.Shapes.Path
        {
            Data = Icons.PlaySolid,
            Fill = Theme.DarkTextBrush,
            Stretch = Stretch.None,
            Width = Icons.Box,
            Height = Icons.Box,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _playButton = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = Theme.FgAccentBrush,
            Margin = new Thickness(12, 0, 12, 0),
            Cursor = Cursors.Hand,
            Child = _playIcon,
        };
        _playButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePlay(); };

        var transport = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _prevSegBtn = MakeRoundButton(Icons.SkipBack, Loc.T("PreviousSegmentTooltip"), PreviousSegment);
        _nextSegBtn = MakeRoundButton(Icons.SkipForward, Loc.T("NextSegmentTooltip"), NextSegment);
        transport.Children.Add(_prevSegBtn);
        transport.Children.Add(_playButton);
        transport.Children.Add(_nextSegBtn);

        var vol = UiHelpers.BuildVolumeControl(24, 70, 6, Theme.FgDimBrush, _volumeLevel,
            () => _muted,
            () => { _muted = !_muted; ApplyVolume(); },
            v => { _volumeLevel = v; _muted = false; ApplyVolume(); });
        _speakerIcon = vol.Icon;
        _volumeSlider = vol.Slider;
        _volumeSlot = vol.Slot;
        var volumeRow = vol.Row;
        volumeRow.HorizontalAlignment = HorizontalAlignment.Right;
        volumeRow.VerticalAlignment = VerticalAlignment.Center;

        // Auto | Star | Auto, not Star | Auto | Star. The panel now lives in
        // a ~345 DIP column rather than the full window width, and two equal
        // Star columns left the timecode about 97 DIP - enough to clip
        // "00:00:00 / 00:00:00" mid-word. Auto ends give the readout and the
        // volume exactly what they need and let the transport centre itself
        // in whatever is left.
        var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_timeLabel);
        transport.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(transport, 1);
        bottom.Children.Add(transport);
        Grid.SetColumn(volumeRow, 2);
        bottom.Children.Add(volumeRow);
        Grid.SetRow(bottom, 3);
        grid.Children.Add(bottom);

        Child = grid;
        SetStripVisible(AppSettings.SegmentStripVisible);
        UpdateAffordances();
        UpdateReadouts();

        // Not unsubscribed: this panel is built once in MainWindow's
        // constructor and lives for the whole process, same lifetime as the
        // static Loc event itself, so there is nothing to leak.
        Loc.LanguageChanged += RefreshLanguage;
    }

    /// <summary>Re-pulls this panel's own static chrome from Loc.T(...) -
    /// unlike PreviewPlayerWindow (rebuilt fresh on every open, so it just
    /// reads the current language at construction time), this panel is a
    /// long-lived singleton that stays interactive - and switchable - while
    /// visible.</summary>
    private void RefreshLanguage()
    {
        _headerLabel.Text = Loc.T("ProjectPreviewLabel");
        _reloadChip.ToolTip = Loc.T("ReloadTooltip");
        System.Windows.Automation.AutomationProperties.SetName(_reloadChip, Loc.T("ReloadTooltip"));
        _closeChip.ToolTip = Loc.T("ClosePreviewTooltip");
        System.Windows.Automation.AutomationProperties.SetName(_closeChip, Loc.T("ClosePreviewTooltip"));
        _prevSegBtn.ToolTip = Loc.T("PreviousSegmentTooltip");
        System.Windows.Automation.AutomationProperties.SetName(_prevSegBtn, Loc.T("PreviousSegmentTooltip"));
        _nextSegBtn.ToolTip = Loc.T("NextSegmentTooltip");
        System.Windows.Automation.AutomationProperties.SetName(_nextSegBtn, Loc.T("NextSegmentTooltip"));
        _volumeSlot.ToolTip = _muted ? Loc.T("UnmuteTooltip") : Loc.T("MuteTooltip");
        System.Windows.Automation.AutomationProperties.SetName(_volumeSlot,
            _muted ? Loc.T("UnmuteTooltip") : Loc.T("MuteTooltip"));
        _legendKept.Text = Loc.T("SegmentsKeptLabel");
        _legendCut.Text = Loc.T("SegmentsCutLabel");

        if (_segments.Count > 0)
        {
            _summary.Text = $"{_lastClipCount} {Loc.T(_lastClipCount == 1 ? "FileWordSingular" : "FileWordPlural")} · " +
                             $"{_segments.Count} {Loc.T(_segments.Count == 1 ? "SegmentWordSingular" : "SegmentWordPlural")} · " +
                             $"{TimeUtils.FormatTime(_total)}";
        }
        else if (_noticePill.Visibility == Visibility.Visible)
        {
            // Only the stable "nothing to preview yet" notice is safe to
            // regenerate generically here - the undecodable-file notices
            // (see MarkUndecodable) carry a specific filename and only ever
            // show while segments.Count > 0, so they never reach this branch.
            ShowNotice(Loc.T("NothingToPreviewMessage"));
        }

        UpdateAffordances();
    }

    /// <summary>Shows or hides the kept/cut strip and its legend - see
    /// AppSettings.SegmentStripVisible. Row 1 of the inner Grid (the video)
    /// is Star and row 2 (this stack) is Auto, so collapsing these two just
    /// hands their space straight to the video above; the panel's own
    /// MinHeight is lowered to match so the outer layout does not keep
    /// reserving room for a strip that is not there.</summary>
    public void SetStripVisible(bool visible)
    {
        _segmentStrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _stripLegend.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MinHeight = visible ? StripFullMinHeight : StripFullMinHeight - StripBlockHeight;
    }

    private void UpdateSegmentStrip(IReadOnlyList<ProjectClip> clips)
    {
        var entries = clips
            .Where(c => c.Ranges.Any(r => r.Length > 0))
            .Select(c => new SegmentStrip.Entry(
                c.Path,
                c.Ranges.Where(r => r.Length > 0).ToList(),
                DurationCache.Get(c.Path)))
            .ToList();
        _segmentStrip.SetModel(entries);
    }

    /// <summary>
    /// Fills in each file's real duration in the background, then repaints
    /// the strip. Without it the strip can only reach as far as the last kept
    /// moment, which would hide a trailing cut entirely - and a trailing cut
    /// (the ending credits) is one of the two Bartek makes on every episode.
    ///
    /// One ffprobe per file, off the UI thread, results cached by path so
    /// reopening or reloading the preview does not pay for it again. Failures
    /// are simply not cached, leaving that file drawn as unprobed rather than
    /// wrong.
    /// </summary>
    private async void LoadDurationsAsync(IReadOnlyList<ProjectClip> clips)
    {
        int epoch = ++_durationEpoch;
        var paths = clips.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Shared with the file rows, which show the same number next to each
        // file name - so twelve files cost twelve ffprobes once, not once per
        // consumer per reload.
        bool learned = await DurationCache.RequestAsync(paths).ConfigureAwait(false);

        // Marshalled explicitly: DurationCache resumes on a pool thread by
        // design, and the epoch check plus the repaint both touch this panel.
        await Dispatcher.BeginInvoke(() =>
        {
            if (epoch != _durationEpoch || _released) return;
            if (learned) UpdateSegmentStrip(clips);
        });
    }

    /// <summary>Two swatches naming the strip's two colors. Without it the
    /// cyan/dark split is a decoration; with it, it is a reading.</summary>
    private UIElement BuildStripLegend()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _legendKept = AddLegendItem(row, Theme.KeptSegmentBrush, Loc.T("SegmentsKeptLabel"));
        _legendCut = AddLegendItem(row, Theme.BgCardHl2Brush, Loc.T("SegmentsCutLabel"));
        return row;
    }

    private static TextBlock AddLegendItem(Panel host, Brush swatch, string text)
    {
        var box = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(3),
            Background = swatch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(host.Children.Count == 0 ? 0 : 16, 0, 6, 0),
        };
        host.Children.Add(box);
        var label = new TextBlock
        {
            Text = text,
            Foreground = Theme.TextLowBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.LabelFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(label);
        return label;
    }

    private UIElement BuildHeader()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _headerLabel = new TextBlock
        {
            Text = Loc.T("ProjectPreviewLabel"),
            Foreground = Theme.FgFaintBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.LabelFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(_headerLabel);

        Grid.SetColumn(_summary, 1);
        header.Children.Add(_summary);

        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        _reloadChip = MakeIconChip(Icons.Reload, Loc.T("ReloadTooltip"), Theme.FgAccentBrush,
            () => ReloadRequested?.Invoke());
        _closeChip = MakeIconChip(Icons.Close, Loc.T("ClosePreviewTooltip"), Theme.HoverRedBrush,
            () => CloseRequested?.Invoke());
        chips.Children.Add(_reloadChip);
        chips.Children.Add(_closeChip);
        Grid.SetColumn(chips, 2);
        header.Children.Add(chips);

        Grid.SetRow(header, 0);
        return header;
    }

    private Border BuildPlayOverlay()
    {
        var disc = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromArgb(0xd0, Theme.BorderCard.R, Theme.BorderCard.G, Theme.BorderCard.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M13,7 L30,17.5 L13,28 Z"),
                Fill = Theme.FgBrush,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 38,
                Height = 35,
                IsHitTestVisible = false,
            },
        };
        disc.ToolTip = Loc.T("PlayWholeProjectTooltip");
        System.Windows.Automation.AutomationProperties.SetName(disc, Loc.T("PlayWholeProjectTooltip"));
        disc.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePlay(); };
        return disc;
    }


    private static Border MakeRoundButton(Geometry icon, string tip, Action onClick)
    {
        var btn = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = Theme.BgRowBrush,
            BorderBrush = Theme.BorderCardBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = Icons.Filled(icon, Theme.FgBrush),
        };
        System.Windows.Automation.AutomationProperties.SetName(btn, tip);
        btn.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        btn.MouseEnter += (_, _) => btn.Background = Theme.BgRowHlBrush;
        btn.MouseLeave += (_, _) => btn.Background = Theme.BgRowBrush;
        return btn;
    }

    private static Border MakeIconChip(Geometry icon, string tip, Brush hover, Action onClick)
    {
        var glyph = Icons.Stroked(icon, Theme.FgDimBrush, 1.5);
        var chip = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Background = Theme.TransparentBrush,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Child = glyph,
        };
        System.Windows.Automation.AutomationProperties.SetName(chip, tip);
        chip.MouseEnter += (_, _) => { chip.Background = Theme.IconHoverBgBrush; glyph.Stroke = hover; };
        chip.MouseLeave += (_, _) => { chip.Background = Theme.TransparentBrush; glyph.Stroke = Theme.FgDimBrush; };
        chip.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return chip;
    }

    // -- project loading -------------------------------------------------------

    /// <summary>True once there is at least one range to play.</summary>
    public bool HasContent => _segments.Count > 0;

    /// <summary>Rebuilds the output timeline from the current file list and
    /// rewinds to its start. Ignored while a job is running - see <see
    /// cref="SetLocked"/>: opening a decoder on a file ffmpeg is reading
    /// right now is exactly what the lock exists to prevent, and the reload
    /// chip, a structural change to the list and an Apply from the trim
    /// window all arrive through here.</summary>
    public void LoadProject(IReadOnlyList<ProjectClip> clips)
    {
        if (_locked) return;
        PausePlayback();
        _segments.Clear();
        _undecodable.Clear();
        _released = false;

        double cursor = 0;
        int clipCount = 0;
        // Kept in lockstep with UpdateSegmentStrip's own filter below (both
        // skip clips with no positive-length range), so this index lines up
        // exactly with that entry's position in the strip's model.
        int entryIndex = 0;
        foreach (var clip in clips)
        {
            bool used = false;
            foreach (var range in clip.Ranges)
            {
                if (range.Length <= 0) continue;
                _segments.Add(new Segment(clip.Path, range.Start, range.End, cursor, entryIndex));
                cursor += range.Length;
                used = true;
            }
            if (used) { clipCount++; entryIndex++; }
        }
        _total = cursor;
        _virtualT = 0;
        _index = -1;
        _lastClipCount = clipCount;
        _currentSourcePos = 0;

        UpdateSegmentStrip(clips);
        LoadDurationsAsync(clips);

        _summary.Text = _segments.Count == 0
            ? ""
            : $"{clipCount} {Loc.T(clipCount == 1 ? "FileWordSingular" : "FileWordPlural")} · " +
              $"{_segments.Count} {Loc.T(_segments.Count == 1 ? "SegmentWordSingular" : "SegmentWordPlural")} · " +
              $"{TimeUtils.FormatTime(_total)}";

        if (_segments.Count == 0)
        {
            CloseMedia();
            ShowNotice(Loc.T("NothingToPreviewMessage"));
        }
        else
        {
            HideNotice();
            // Opened paused on the first frame, so the panel shows the start
            // of the finished clip rather than a black rectangle. LoadSegment
            // itself skips straight to a seek, with no decoder reopen, when
            // segment 0 is still the same file that was already loaded (its
            // own _loadedPath/_mediaReady check) - this used to be
            // unreachable because LoadProject closed the decoder before ever
            // reaching it, so every reload (including a plain click-release
            // on a file row while the preview was open) re-opened segment
            // 0's file from scratch even when it had not changed.
            LoadSegment(0, 0, autoPlay: false);
        }
        UpdateAffordances();
        UpdateReadouts();
    }

    /// <summary>Full teardown for when the panel is hidden. The decoder is
    /// closed rather than just paused, so a hidden preview is not holding a
    /// file handle open against the very file the job is about to read.</summary>
    public void Release()
    {
        PausePlayback();
        CloseMedia();
        _released = true;
        _index = -1;
        _virtualT = 0;
        UpdateAffordances();
    }

    /// <summary>Takes the panel out of service for the duration of a job and
    /// puts it back afterwards.
    ///
    /// Releasing the decoder alone is not enough: the panel stays visible
    /// and clickable while the job runs, and every transport entry point
    /// (play, seek, prev/next segment) would happily re-open a file ffmpeg
    /// is in the middle of reading - and land in a state nothing could stop
    /// from the UI, because the release guard in OnMediaOpened skips the
    /// Pause() that would have paired with that Play(). So the lock is a
    /// state of its own: the decoder goes, the transport goes dead and dim,
    /// the scrubber stops taking clicks, and a notice says why.
    ///
    /// Unlocking deliberately does NOT reload here. The host owns the
    /// project, so it re-reads the rows and calls <see cref="LoadProject"/>
    /// itself - and only when the panel is actually open, which is what
    /// keeps a hidden preview from quietly opening a decoder the moment a
    /// job ends.</summary>
    public void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        _scrubber.IsHitTestVisible = !locked;
        if (locked)
        {
            Release();
            ShowNotice(Loc.T("PreviewPausedDuringJobMessage"));
        }
        UpdateAffordances();
    }

    // -- segment steering --------------------------------------------------------

    private void LoadSegment(int index, double offsetInSegment, bool autoPlay)
    {
        // The one place the decoder is actually opened, so the release guard
        // belongs here rather than on each of the paths that reach it.
        if (_released) return;
        if (index < 0 || index >= _segments.Count)
        {
            Finish();
            return;
        }

        var seg = _segments[index];
        if (_undecodable.Contains(seg.Path))
        {
            int next = NextDecodableFrom(index);
            if (next < 0) { Finish(); return; }
            LoadSegment(next, 0, autoPlay);
            return;
        }

        _index = index;
        _virtualT = seg.VirtualStart + Math.Clamp(offsetInSegment, 0, Math.Max(0, seg.Length));
        double target = seg.Start + Math.Clamp(offsetInSegment, 0, Math.Max(0, seg.Length));
        _currentSourcePos = target;

        if (_loadedPath == seg.Path && _mediaReady)
        {
            _media.Position = TimeSpan.FromSeconds(target);
            if (autoPlay) StartPlayback(); else PausePlayback();
        }
        else
        {
            // Manual LoadedBehavior means Source alone does not open the
            // file - Play() is what starts the decode, and MediaOpened is
            // where the seek and the pause actually land. Volume is dropped
            // to zero across that window so the first frames of the file do
            // not blurt out before the seek takes effect.
            _mediaReady = false;
            _loadedPath = seg.Path;
            _pendingPosition = target;
            _pendingPlay = autoPlay;
            _media.Volume = 0;
            // Settle any pending layout FIRST. The first segment of a
            // freshly opened preview is loaded from MainWindow.
            // OpenProjectPreview, in the same call as the Visibility flip
            // that takes this panel's column from collapsed to visible -
            // that flip only INVALIDATES layout, it does not run it, so
            // without this call Play() starts the decode while the video
            // stage still measures at its stale (zero) size. The video
            // session opens but never shows a frame; only a LATER Play()
            // call, issued once something else (MainWindow.RefitHeight's own
            // UpdateLayout, or a later reload) has forced a real layout
            // pass, actually renders anything. That read as "the preview
            // does not load until I click a file" - any file row press
            // reloads the preview on release (EndDrag), which happened to
            // be the first thing that forced a layout pass after the panel
            // opened.
            UpdateLayout();
            try
            {
                _media.Source = new Uri(seg.Path);
                _media.Play();
            }
            catch (Exception)
            {
                MarkUndecodable(seg.Path);
            }
        }
        UpdateAffordances();
        UpdateReadouts();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_released) return;
        _mediaReady = true;
        if (_media.NaturalVideoHeight > 0)
        {
            _aspect = Math.Clamp((double)_media.NaturalVideoWidth / _media.NaturalVideoHeight, 0.4, 4.0);
            _videoCard.Width = Math.Min(_videoStage.ActualWidth, _videoStage.ActualHeight * _aspect);
        }
        _media.Position = TimeSpan.FromSeconds(_pendingPosition);
        if (_pendingPlay) StartPlayback(); else PausePlayback();
        ApplyVolume();
        UpdateAffordances();
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (_released) return;
        MarkUndecodable(_loadedPath);
    }

    /// <summary>Takes a file Windows cannot decode out of the run and
    /// carries on with the rest, rather than letting one bad file stall the
    /// whole preview.</summary>
    private void MarkUndecodable(string? path)
    {
        if (path is not null) _undecodable.Add(path);
        _mediaReady = false;
        _loadedPath = null;

        int next = _index < 0 ? -1 : NextDecodableFrom(_index);
        string name = path is null ? Loc.T("ThisFileFallbackName") : IoPath.GetFileName(path);
        if (next < 0)
        {
            PausePlayback();
            ShowNotice(string.Format(Loc.T("NoDecoderAnyMessage"), name));
            UpdateAffordances();
            return;
        }
        ShowNotice(string.Format(Loc.T("SkippingFileMessage"), name));
        LoadSegment(next, 0, _playing);
    }

    private int NextDecodableFrom(int index)
    {
        for (int i = index + 1; i < _segments.Count; i++)
            if (!_undecodable.Contains(_segments[i].Path))
                return i;
        return -1;
    }

    private void Advance()
    {
        if (_index < 0) return;
        if (_index + 1 >= _segments.Count) { Finish(); return; }
        HideNotice();
        LoadSegment(_index + 1, 0, autoPlay: _playing);
    }

    /// <summary>End of the project: park on the last frame rather than
    /// looping, and let the play button restart from the top.</summary>
    private void Finish()
    {
        PausePlayback();
        _virtualT = _total;
        UpdateAffordances();
        UpdateReadouts();
    }

    private void NextSegment()
    {
        if (_released) return;
        if (_segments.Count == 0) return;
        HideNotice();
        LoadSegment(Math.Min(_index + 1, _segments.Count - 1), 0, autoPlay: _playing);
    }

    /// <summary>Back to the start of this segment, or to the previous one if
    /// already near the start - the convention every music player uses.</summary>
    private void PreviousSegment()
    {
        if (_released) return;
        if (_segments.Count == 0 || _index < 0) return;
        HideNotice();
        bool nearStart = _virtualT - _segments[_index].VirtualStart < 1.0;
        LoadSegment(nearStart ? Math.Max(0, _index - 1) : _index, 0, autoPlay: _playing);
    }

    private void SeekVirtual(double t)
    {
        if (_released) return;
        if (_segments.Count == 0) return;
        t = Math.Clamp(t, 0, _total);
        int index = _segments.Count - 1;
        for (int i = 0; i < _segments.Count; i++)
        {
            if (t < _segments[i].VirtualEnd) { index = i; break; }
        }
        HideNotice();
        LoadSegment(index, t - _segments[index].VirtualStart, autoPlay: _playing);
    }

    // -- playback ------------------------------------------------------------------

    private void TogglePlay()
    {
        // Dead while released or locked: the transport is dimmed to match,
        // but the video surface and the overlay disc route here too.
        if (_released) return;
        if (_segments.Count == 0) return;
        if (_playing) { PausePlayback(); return; }
        // Restart from the top once the run has finished.
        if (_virtualT >= _total - 0.05) { LoadSegment(0, 0, autoPlay: true); return; }
        if (_index < 0) { LoadSegment(0, 0, autoPlay: true); return; }
        StartPlayback();
    }

    private void StartPlayback()
    {
        if (_released || _segments.Count == 0) return;
        _media.Play();
        _playing = true;
        _timer.Start();
        UpdateAffordances();
    }

    private void PausePlayback()
    {
        _timer.Stop();
        _playing = false;
        try { _media.Pause(); } catch (InvalidOperationException) { /* never opened */ }
        UpdateAffordances();
    }

    private void CloseMedia()
    {
        _mediaReady = false;
        _loadedPath = null;
        try { _media.Stop(); _media.Close(); } catch (InvalidOperationException) { /* never opened */ }
        _media.Source = null;
    }

    private void Tick()
    {
        if (_released || !_playing || _index < 0 || !_mediaReady) return;
        var seg = _segments[_index];
        double pos = _media.Position.TotalSeconds;
        // A tolerance of one tick: Position lands on a decoded frame
        // boundary, so waiting for an exact match would overrun the range.
        if (pos >= seg.End - 0.04)
        {
            Advance();
            return;
        }
        _virtualT = seg.VirtualStart + Math.Clamp(pos - seg.Start, 0, seg.Length);
        _currentSourcePos = pos;
        UpdateReadouts();
    }

    private void ApplyVolume()
    {
        _speakerIcon.Data = _muted || _volumeLevel <= 0.001 ? Icons.SpeakerMuted : Icons.Speaker;
        _volumeSlider.Value = _volumeLevel;
        if (_mediaReady) _media.Volume = _muted ? 0.0 : _volumeLevel;
    }

    // -- readouts --------------------------------------------------------------------

    private void UpdateAffordances()
    {
        // Released counts as dead, not just empty: while a job holds the
        // lock the transport must LOOK as inert as it now behaves, or the
        // buttons read as broken rather than as unavailable.
        bool live = _segments.Count > 0 && !_released;
        _playIcon.Data = _playing ? Icons.PauseBars : Icons.PlaySolid;
        _playButton.Background = live ? Theme.FgAccentBrush : Theme.DisabledBgBrush;
        _playIcon.Fill = live ? Theme.DarkTextBrush : Theme.DisabledFgBrush;
        _playButton.Cursor = live ? Cursors.Hand : Cursors.Arrow;
        _playButton.ToolTip = _playing ? Loc.T("PauseTooltip") : Loc.T("PlayWholeProjectTooltip");
        System.Windows.Automation.AutomationProperties.SetName(_playButton, _playing ? Loc.T("PauseTooltip") : Loc.T("PlayWholeProjectTooltip"));
        _playOverlay.Visibility = live && !_playing ? Visibility.Visible : Visibility.Collapsed;
        _nowPlayingPill.Visibility = live && _index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var btn in new[] { _prevSegBtn, _nextSegBtn })
        {
            btn.Opacity = live ? 1.0 : 0.4;
            btn.Cursor = live ? Cursors.Hand : Cursors.Arrow;
        }
        _scrubber.Opacity = live ? 1.0 : 0.5;
    }

    private void UpdateReadouts()
    {
        _timeLabel.Text = $"{TimeUtils.FormatTime(_virtualT)} / {TimeUtils.FormatTime(_total)}";
        if (_index >= 0 && _index < _segments.Count)
            _nowPlaying.Text = $"{_index + 1}/{_segments.Count} · {IoPath.GetFileName(_segments[_index].Path)}";
        _scrubber.SetModel(
            _segments.Select(s => new TimeRange(s.VirtualStart, s.VirtualEnd)).ToList(),
            Math.Max(0.001, _total), _virtualT);
        _segmentStrip.SetProgress(_index >= 0 && _index < _segments.Count ? _segments[_index].EntryIndex : -1,
            _currentSourcePos);
    }

    private void ShowNotice(string text)
    {
        _notice.Text = text;
        _noticePill.Visibility = Visibility.Visible;
    }

    private void HideNotice() => _noticePill.Visibility = Visibility.Collapsed;
}
