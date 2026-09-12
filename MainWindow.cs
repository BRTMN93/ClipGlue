using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipGlue.Controls;
using ClipGlue.Dialogs;
using ClipGlue.Engine;
using ClipGlue.Models;
using ClipGlue.Views;
using Microsoft.Win32;

using ShapePath = System.Windows.Shapes.Path;

namespace ClipGlue;

/// <summary>
/// The main window: UI construction, drag &amp; drop, validation, and the
/// worker-task orchestration of the cut/join job. Direct port of
/// clipglue/app.py's App class.
///
/// The Python original drove its worker thread through a manual
/// Queue&lt;Tuple&gt; polled every 100ms via root.after() - a workaround for
/// Tkinter having no cross-thread dispatch primitive of its own. WPF's
/// Dispatcher plus async/await replaces that entirely: the worker runs via
/// Task.Run, progress/log updates marshal back with Dispatcher.BeginInvoke,
/// and completion (done/error/cancelled) is just the code that follows an
/// `await` - no queue, no poll timer.
/// </summary>
public sealed class MainWindow : Window
{
    private sealed record ParsedFile(string FilePath, List<(double Start, double End)> Ranges);
    private sealed record QueuedJob(List<ParsedFile> Parsed, string Output);

    private sealed class ProjectFileEntry
    {
        public string Path { get; set; } = "";
        public string Ranges { get; set; } = "";
    }

    private sealed class ProjectFile
    {
        public int Version { get; set; } = 1;
        public List<ProjectFileEntry> Files { get; set; } = new();
    }

    private readonly Dictionary<string, string> _config;
    private readonly List<FileRowControl> _rows = new();
    private readonly List<QueuedJob> _jobQueue = new();
    // How many jobs of the currently-running batch have finished - written
    // only by ProcessAllJobs on the background task, read only after that
    // task has stopped (awaited or faulted), so there's no concurrent access.
    private int _completedJobCount;
    private readonly PauseController _pauseController = new();

    private CancellationTokenSource? _cts;
    private bool _processing;
    private bool _closing;
    private FileRowControl? _dragRow;
    private double _dragOffset;
    private bool _animRunning;
    /// <summary>True while the window is maximized, the only state where the
    /// row list has spare height to give a row that grows downward instead of
    /// folding its range chips into a "+N" pill - see OnWindowStateChanged
    /// and FileRowControl.SetWrapMode.</summary>
    private bool _wrapRows;

    private Canvas _rowsCanvas = null!;
    private ScrollViewer _listScroll = null!;
    private Border _listWrap = null!;
    private TextBlock _emptyLabel = null!;
    private Border _dimOverlay = null!;
    /// <summary>The window's real content grid - kept so <see cref="SetDimmed"/>
    /// can put a <see cref="BlurEffect"/> on exactly this element (and not on
    /// <see cref="_dimOverlay"/>, its sibling), see the constructor's own
    /// comment on why the two need to be siblings.</summary>
    private Grid _contentRoot = null!;
    private Button _addButton = null!, _saveProjectButton = null!, _loadProjectButton = null!;
    private Button _startButton = null!, _stopButton = null!, _queueButton = null!, _clearQueueButton = null!;
    private Button _previewProjectButton = null!;
    private Button _settingsButton = null!;
    private Button _languageButton = null!;
    private ProjectPreviewPanel _projectPreview = null!;
    private ColumnDefinition _previewColumn = null!;
    private Grid _rightColumn = null!;
    private UIElement _actionBar = null!, _consoleSection = null!;
    private TextBlock _queueLabel = null!;
    private LabeledProgressBar _progress = null!, _stepProgress = null!;
    private Border _statsPanel = null!;
    private TextBlock _statSpeedValue = null!;
    private TextBlock _statEtaValue = null!;
    private TextBlock _statCpuLabel = null!, _statCpuValue = null!;
    private TextBlock _statMemLabel = null!, _statMemValue = null!;
    private JobStatsTracker? _jobStats;
    private RichTextBox _logBox = null!;
    private bool _stopRequested;

    // Header strings that change with the language - see ApplyLanguage().
    private TextBlock _hdrTitle = null!, _hdrLine1 = null!, _hdrLine2 = null!;
    private TextBlock _hdrExamplePrefix = null!, _hdrLine4 = null!, _hdrLine5 = null!;
    private TextBlock _consoleHeaderText = null!;
    private Button _copyLogButton = null!;
    private Grid _headerGrid = null!;
    private StackPanel _hdrExampleRow = null!;

    // -- custom title bar (see Controls/TitleBar.cs) -------------------------
    private TitleBar.Handles _titleBar = null!;

    // -- collapsible instruction -------------------------------------------
    /// <summary>Key in the shared %APPDATA%\ClipGlue\config.json. Persisted
    /// because the panel is a first-run explainer: someone who has already
    /// read it should not have to collapse it again on every launch, and
    /// someone who has not should not lose it because they once collapsed it
    /// by accident.</summary>
    private const string HelpExpandedKey = "help_expanded";
    /// <summary>Set when the user dismisses the bar outright with its ✕,
    /// which is a stronger statement than collapsing it. Reversible from the
    /// settings popup, so it is not a one-way door.</summary>
    private TextBlock _hdrSummary = null!, _hdrSummaryTail = null!;
    private Border _hdrSummaryExample = null!;
    private UIElement _helpBar = null!;
    private StackPanel _helpDetails = null!;
    private Button _helpToggle = null!;
    private bool _helpExpanded;

    // -- console drawer -----------------------------------------------------
    private Border _consoleBody = null!;
    private Border _consoleHeaderBar = null!;
    private ShapePath _consoleChevron = null!;
    private Border _consoleBadge = null!;
    private TextBlock _consoleBadgeText = null!;
    private bool _consoleOpen;
    /// <summary>Log lines written since the drawer was last open. The drawer
    /// starts closed, so this is the only signal that anything is happening
    /// down there.</summary>
    private int _unreadLogLines;

    // -- progress card (detailed view, see AppSettings.DetailedProgressView) -
    private TextBlock _totalPercent = null!, _stepPercent = null!;
    private TextBlock _progressHeaderLabel = null!, _progressFileName = null!;
    private Border _progressCardBorder = null!;

    // -- mini progress bar (default view) ------------------------------------
    private Border _miniProgressBar = null!;
    private LabeledProgressBar _miniProgress = null!;
    private TextBlock _miniPercent = null!, _miniEta = null!;

    public MainWindow()
    {
        _config = ConfigStore.Load();

        Background = Theme.BgBrush;
        // Width is fixed up front rather than auto-measured: several
        // children (RichTextBox's internal FlowDocument page-width pass in
        // particular, but this affects any Stretch-aligned content in an
        // unconstrained row) compute a wildly inflated desired width when
        // measured against the double.PositiveInfinity that a
        // SizeToContent.WidthAndHeight pass hands out - RichTextBox still
        // does this even with HorizontalScrollBarVisibility=Disabled, since
        // that only suppresses the scrollbar chrome, not FlowDocument's own
        // internal infinite test-format pass. That inflated width used to
        // get captured as MinWidth in FitInitialSize, permanently locking
        // the window at a near-screen-filling size it could never be
        // resized narrower than (height was unaffected, since only the
        // width-side measurement blew up). Auto-sizing only the height here
        // sidesteps the whole class of bug: every descendant is always
        // measured against this window's real, finite Width.
        Width = 1040;
        MinWidth = 940;
        SizeToContent = SizeToContent.Height;
        // Only the horizontal half of this survives: FitInitialSize hands
        // the window straight to WindowPlacement.PinToTop afterwards, which
        // overwrites Top with the top of the work area. Centering is still
        // what decides Left, and it also beats Windows' cascade placement,
        // which drifts a window further down-right on every launch - the
        // exact drift that used to push the action bar off the bottom.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Icon = Paths.LoadAppIcon();

        // Custom chrome (Bartek's choice - see PROJECT_STATE.md redesign
        // phase 0 decision 4 - over the safer DWM-only route, which cannot
        // give custom min/max/close glyphs or Theme.WindowRadius). The
        // native title bar is gone; TitleBar.Build below draws its
        // replacement as row 0 of this same grid, and NativeChrome.Apply
        // (hooked to SourceInitialized, since it needs the HWND) rounds the
        // window's corners and dark-themes its non-client frame at the OS
        // level - neither of which WindowChrome itself can do.
        WindowStyle = WindowStyle.None;
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = Theme.TitleBarHeight,
            ResizeBorderThickness = new Thickness(6),
            // Not 0: a real zero here also removes DWM's drop shadow around
            // the window, which is what used to tell it apart from the
            // desktop behind it. This is the standard "keep the shadow,
            // lose the glass" value - only the bottom 1px counts as glass.
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);
        SourceInitialized += (_, _) => NativeChrome.Apply(this);

        // Six rows now: the custom title bar sits above the
        // header/rule/toolbar/content stack the redesign already built. The
        // mockup puts the file list, console and action bar in a left
        // column and a permanently visible preview in a right one - which
        // also retires the height gymnastics the stacked layout needed,
        // since a side-by-side preview costs width rather than competing
        // for the same vertical budget. The sixth row, at the very bottom,
        // holds the minimal progress bar (see BuildMiniProgressBar) - a
        // status-bar-style strip that is its own row rather than living
        // inside either column, exactly so neither column's visibility
        // (the preview toggle, wrap mode, anything) can ever take it down.
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < 4; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var (titleBarElement, titleBarHandles) = TitleBar.Build(this);
        _titleBar = titleBarHandles;
        Grid.SetRow(titleBarElement, 0);
        root.Children.Add(titleBarElement);

        var header = BuildHeader();
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        var sep = new Border { Background = Theme.BorderBrush, Height = 1, Margin = new Thickness(Theme.PagePad, 10, Theme.PagePad, 0) };
        Grid.SetRow(sep, 2);
        root.Children.Add(sep);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 3);
        root.Children.Add(toolbar);

        var content = new Grid { Margin = new Thickness(Theme.PagePad, Theme.SectionGap, Theme.PagePad, 16) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58, GridUnitType.Star), MinWidth = 470 });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42, GridUnitType.Star) });
        _previewColumn = content.ColumnDefinitions[1];

        // -- left column ----------------------------------------------------
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var listArea = BuildFileListArea();
        Grid.SetRow(listArea, 0);
        left.Children.Add(listArea);

        _consoleSection = BuildConsole();
        Grid.SetRow(_consoleSection, 1);
        left.Children.Add(_consoleSection);

        _actionBar = BuildActionBar();
        Grid.SetRow(_actionBar, 2);
        left.Children.Add(_actionBar);

        Grid.SetColumn(left, 0);
        content.Children.Add(left);

        // -- right column ---------------------------------------------------
        // Holds only the project preview now. The progress card used to live
        // here too, under the preview - but that meant closing the preview
        // (which collapses this whole column to zero width) took the
        // progress card down with it. The card reports on the cutting job,
        // not on the preview, so it belongs somewhere that isn't collapsed
        // by the preview toggle - see BuildActionBar, where it lives now.
        _rightColumn = new Grid { Margin = new Thickness(Theme.SectionGap, 0, 0, 0) };
        _rightColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 300 });

        var preview = BuildProjectPreview();
        Grid.SetRow(preview, 0);
        _rightColumn.Children.Add(preview);

        Grid.SetColumn(_rightColumn, 1);
        content.Children.Add(_rightColumn);

        Grid.SetRow(content, 4);
        root.Children.Add(content);

        _miniProgressBar = BuildMiniProgressBar();
        Grid.SetRow(_miniProgressBar, 5);
        root.Children.Add(_miniProgressBar);

        // The backdrop treatment behind every modal (Done/Error/Stop-confirm
        // dialogs, the Trim range window) used to be a flat translucent
        // Border painted over "root". Bartek asked for a real blur instead
        // of plain dimming, which BlurEffect can only give by rendering onto
        // an intermediate surface - and that surface has to be "root" alone,
        // not the overlay itself, otherwise the overlay would blur along
        // with everything behind it. Hence the extra wrapping grid:
        // "contentHost" hosts "root" (which gets the effect) and the overlay
        // as two SIBLINGS, so the overlay stays perfectly crisp on top of
        // the blurred content instead of being blurred itself.
        var contentHost = new Grid();
        _contentRoot = root;
        contentHost.Children.Add(root);

        _dimOverlay = new Border
        {
            // A thin scrim on top of the blur, not a replacement for it -
            // pure blur left a paused bright video frame behind the dialog
            // distractingly vivid, so a light wash keeps the dialog readable
            // without going back to the old flat dimming look.
            Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        contentHost.Children.Add(_dimOverlay);

        Content = contentHost;

        // Dropping anywhere in the window works because none of the
        // descendants set AllowDrop - WPF's drop-target hit test walks up
        // the visual tree from whatever was under the cursor until it finds
        // an element with AllowDrop=true, so the Window itself ends up
        // being the target no matter which child the file was released
        // over (a button, the file list, the console, ...).
        AllowDrop = true;
        DragEnter += OnWindowDragOver;
        DragOver += OnWindowDragOver;
        DragLeave += OnWindowDragLeave;
        Drop += OnWindowDrop;

        UpdatePreviewButtonState();
        // No RefitHeight yet needed here - the window has not been measured
        // for the first time (that happens in FitInitialSize below), so this
        // just sets which of the two progress views starts visible.
        SetProgressViewMode(AppSettings.DetailedProgressView);
        ApplyLanguage();
        Loc.LanguageChanged += ApplyLanguage;

        // Loaded, not SourceInitialized: a window is measured and positioned
        // before Loaded fires, so this is the first point at which the final
        // auto-measured height is known and can be pinned against the screen.
        Loaded += (_, _) =>
        {
            FitInitialSize();
            WindowPlacement.PinToTop(this);
        };
        Closing += OnClosing;
        Closed += (_, _) => Loc.LanguageChanged -= ApplyLanguage;
        StateChanged += OnWindowStateChanged;
    }

    /// <summary>
    /// Turns row wrapping on/off as the window is maximized/restored - see
    /// FileRowControl.SetWrapMode and RowSlotHeight for why maximized is the
    /// only state that can afford it. Deferred to DispatcherPriority.Loaded
    /// because every row needs an actual layout pass under its NEW mode
    /// before RowSlotHeight can trust its ActualHeight; reading it in the
    /// same tick as SetWrapMode would still see the old, single-line size.
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        TitleBar.RefreshMaximizeGlyph(_titleBar, WindowState);

        bool wrap = WindowState == WindowState.Maximized;
        if (wrap == _wrapRows) return;
        _wrapRows = wrap;
        foreach (var row in _rows) row.SetWrapMode(wrap);
        Dispatcher.BeginInvoke(() => LayoutRows(animate: false), DispatcherPriority.Loaded);
    }

    /// <summary>Re-walks the row canvas after something changed a row's own
    /// height while wrapped (a chip added/removed, or the raw/inline editor
    /// swapped in). A no-op outside wrap mode, where every row is always
    /// Theme.RowH regardless of content. Deferred for the same reason as
    /// OnWindowStateChanged - the row's new height only exists after its own
    /// layout pass runs.</summary>
    private void ReflowRowsForHeightChange()
    {
        if (!_wrapRows) return;
        Dispatcher.BeginInvoke(() => LayoutRows(animate: true), DispatcherPriority.Loaded);
    }

    /// <summary>The vertical slot one row currently occupies in the list
    /// canvas. A fixed Theme.RowH normally - the canvas positions rows by
    /// multiplying by it - but while wrapped (see _wrapRows) a row is free to
    /// be taller, so its own measured height is used instead, floored at
    /// Theme.RowH so a row with few enough chips to fit on one line still
    /// gets the usual slot rather than shrinking.</summary>
    private double RowSlotHeight(FileRowControl row)
    {
        if (!_wrapRows) return Theme.RowH;
        double measured = row.ActualHeight > 0 ? row.ActualHeight : row.DesiredSize.Height;
        return Math.Max(measured, Theme.RowH);
    }

    /// <summary>Re-pulls every translatable piece of chrome from
    /// <see cref="Loc"/> - called once at startup and again every
    /// time the language switcher changes <see cref="Loc.Current"/>.
    /// Only the main window's own static text is covered; dialogs built
    /// on-demand (MessageBox/file dialogs/ConfirmDialogs) instead pull
    /// <see cref="Loc.T"/> fresh at the point they are shown, so
    /// they need no entry here.</summary>
    private void ApplyLanguage()
    {
        Title = Loc.T("WindowTitle");
        // The custom title bar splits the same localized string in two: the
        // brand name (never translated, always "ClipGlue" and always the
        // part before the first " - ") bold on its own line, and the
        // tagline that follows it dimmer underneath - see Localization.cs's
        // "WindowTitle" entries, which all follow exactly that shape in
        // every one of the 13 languages. Splitting the existing string
        // instead of adding a second key keeps one source of truth for the
        // tagline text.
        int dash = Title.IndexOf(" - ", StringComparison.Ordinal);
        _titleBar.Title.Text = dash < 0 ? Title : Title[..dash];
        _titleBar.Subtitle.Text = dash < 0 ? "" : Title[(dash + 3)..];
        _hdrTitle.Text = Loc.T("HeaderTitle");
        _hdrLine1.Text = Loc.T("HeaderLine1");
        _hdrLine2.Text = Loc.T("HeaderLine2");
        _hdrExamplePrefix.Text = Loc.T("HeaderExamplePrefix");
        _hdrLine4.Text = Loc.T("HeaderLine4");
        _hdrLine5.Text = Loc.T("HeaderLine5");

        // Arabic/Urdu: the description text right-aligns and its example
        // row's child order reverses, but the header GRID itself is never
        // touched - the language button must stay in the exact same corner
        // for every language, and mirroring the Grid's own FlowDirection
        // (the previous approach) moved it. Grid/StackPanel FlowDirection
        // mirroring is also where the earlier attempt went visibly wrong:
        // WPF applies an inherited mirror to a RightToLeft-flowed
        // container's rendered content, and TextAlignment interacts with
        // that in ways that produced inconsistent-looking lines. Plain
        // TextAlignment on an otherwise-untouched LeftToRight TextBlock is
        // unambiguous - it right-justifies against the box's physical right
        // edge, nothing more - and Arabic/Urdu glyphs shape and order
        // correctly within that box regardless of FlowDirection, since
        // WPF's text layer does its own bidi analysis of the text content.
        var headerAlign = Loc.IsRtl ? TextAlignment.Right : TextAlignment.Left;
        _hdrTitle.TextAlignment = headerAlign;
        _hdrLine1.TextAlignment = headerAlign;
        _hdrLine2.TextAlignment = headerAlign;
        _hdrLine4.TextAlignment = headerAlign;
        _hdrLine5.TextAlignment = headerAlign;
        // The example row (StackPanel) is the one place that DOES still use
        // FlowDirection, deliberately: it's a content-sized horizontal row
        // (not a stretched paragraph), so TextAlignment does nothing for
        // it - reversing its own FlowDirection is what puts "Example:"
        // first (i.e. on the reading-start side) with the literal time
        // codes following it, without affecting anything outside this row.
        _hdrExampleRow.FlowDirection = Loc.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        UiHelpers.SetButtonText(_addButton, Loc.T("BtnAddFiles"));
        UiHelpers.SetButtonText(_saveProjectButton, Loc.T("BtnSaveProject"));
        UiHelpers.SetButtonText(_loadProjectButton, Loc.T("BtnLoadProject"));
        UiHelpers.SetButtonText(_previewProjectButton,
            Loc.T(IsProjectPreviewOpen ? "BtnHidePreview" : "BtnPreviewProject"));
        _previewProjectButton.ToolTip = Loc.T("PreviewProjectTooltip");

        _emptyLabel.Text = string.Format(Loc.T("EmptyListLabel"), Loc.T("BtnAddFiles"));
        _consoleHeaderText.Text = Loc.T("ConsoleHeader");

        _hdrSummary.Text = Loc.T("HeaderSummaryLead");
        _hdrSummaryTail.Text = Loc.T("HeaderSummaryTail");
        UiHelpers.SetButtonText(_helpToggle, Loc.T(_helpExpanded ? "CollapseLabel" : "ExpandLabel"));
        // SetButtonText rewrites the accessible name from the label, so the
        // chevron has to be re-applied after it - the glyph is state, not
        // decoration, and SetButtonText knows nothing about it.
        UiHelpers.SetButtonIcon(_helpToggle, _helpExpanded ? Icons.ChevronDown : Icons.ChevronRight);
        _progressHeaderLabel.Text = Loc.T("FileProgressHeader");
        UpdateConsoleBadge();

        UiHelpers.SetButtonText(_startButton, Loc.T("BtnStart"));
        UiHelpers.SetButtonText(_stopButton, Loc.T(_stopRequested ? "BtnStopping" : "BtnStop"));
        UiHelpers.SetButtonText(_queueButton, Loc.T("BtnAddToQueue"));
        UiHelpers.SetButtonText(_clearQueueButton, Loc.T("BtnClearQueue"));
        UpdateQueueLabel();

        _languageButton.ToolTip = Loc.T("LanguageButtonTooltip");
        _settingsButton.ToolTip = Loc.T("SettingsButtonTooltip");
        _copyLogButton.ToolTip = Loc.T("CopyLogTooltip");

        _statCpuLabel.Text = Loc.T("StatCpuLabel");
        _statMemLabel.Text = Loc.T("StatMemoryLabel");
    }

    /// <summary>Measures the window's natural size once everything is built
    /// and uses that as BOTH the starting size and the minimum size, so the
    /// console/list area can never be resized away or hidden at startup -
    /// port of app.py's _fit_initial_size.</summary>
    private void FitInitialSize()
    {
        // The constructor left SizeToContent on so the very first layout
        // could find the natural height. From here the height is managed by
        // hand - see RefitHeight for why it is never handed back.
        SizeToContent = SizeToContent.Manual;
        RefitHeight();
    }

    /// <summary>Rows the file list is guaranteed to show, and therefore the
    /// only part of it that counts towards the window's natural height. Three
    /// two-line rows: the preview no longer takes height from the list, so
    /// this no longer varies with whether it is open.</summary>
    private double ListViewportH => 3 * Theme.RowH;

    /// <summary>
    /// Recomputes the window's minimum height after a section has been shown
    /// or hidden, and grows the window only if the new arrangement genuinely
    /// needs more room than it currently has.
    ///
    /// Two things here are deliberate, both from bugs Bartek hit:
    ///
    /// It does NOT resize the window to the new natural height. Toggling the
    /// preview rearranges the window's sections; it is not a request to
    /// resize the window. Writing Height unconditionally is what made the
    /// window shorten when the preview opened and stretch when it closed.
    ///
    /// It measures with the file list PINNED to its viewport height. A
    /// ScrollViewer measured against an infinite height reports its whole
    /// content extent rather than a viewport, so the natural height grew
    /// with the number of files and the window stretched to show all of
    /// them - the height-side twin of gotcha #11, which is the same mistake
    /// on the width axis. Measuring the content directly rather than going
    /// through SizeToContent also keeps the window from visibly bouncing to
    /// its natural height and back on every toggle.
    /// </summary>
    private void RefitHeight()
    {
        if (WindowState != WindowState.Normal) return;
        if (Content is not FrameworkElement root || root.ActualHeight <= 0 || root.ActualWidth <= 0) return;

        _listScroll.MinHeight = ListViewportH;
        _listScroll.MaxHeight = ListViewportH;
        // Settle the pending layout FIRST. Callers reach here immediately
        // after flipping a section's Visibility, and without this the probe
        // measure below reports the tree as it was BEFORE that flip - so
        // every toggle produced the previous toggle's MinHeight, one step
        // behind forever. Caught by the sizing probe: collapsing the
        // instruction panel left MinHeight at the expanded 807, and
        // expanding it again then reported the collapsed 709.
        root.UpdateLayout();
        root.Measure(new Size(root.ActualWidth, double.PositiveInfinity));
        double naturalContent = root.DesiredSize.Height;
        _listScroll.MaxHeight = double.PositiveInfinity;
        // The probe measured against a constraint the real layout does not
        // use, so the tree has to be told to work it out again properly.
        root.InvalidateMeasure();

        double chrome = Math.Max(0, ActualHeight - root.ActualHeight);
        double natural = naturalContent + chrome;

        // Never lock the window taller than the display it opened on:
        // MinHeight is exactly what makes it unshrinkable, so on a screen
        // shorter than the natural height it would sit with its bottom
        // hanging off the desktop and no way to drag it back into view.
        // The work area comes from the monitor this window is actually on
        // (SystemParameters.WorkArea only ever describes the primary one)
        // and is already in DIPs, the same unit as Window.Height.
        double cap = WindowPlacement.WorkAreaFor(this).Height - 48;
        double floor = cap > 0 ? Math.Min(natural, cap) : natural;
        MinHeight = floor;

        double target = Math.Max(ActualHeight, floor);
        if (cap > 0 && target > cap) target = cap;
        Height = target;

        // Showing a section makes the window taller, and it grows downwards
        // from wherever the user has since dragged it - so the action bar it
        // just made room for can end up under the taskbar. This only nudges
        // the window up when that has actually happened; a window that still
        // fits is not moved at all.
        WindowPlacement.KeepBottomOnScreen(this);
    }

    // -- UI construction ------------------------------------------------------

    private UIElement BuildHeader()
    {
        _headerGrid = new Grid();
        _headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stack = new StackPanel { Margin = new Thickness(Theme.PagePad, 16, Theme.PagePad, 14) };

        // The one-line bar that is all that shows once the panel is
        // collapsed. It replaces a block that took a fixed ~157 DIP of a 912
        // DIP work area whether or not the user still needed it.
        _hdrTitle = new TextBlock
        {
            Foreground = Theme.FgBrush, FontFamily = Theme.HeadFontFamily,
            FontSize = Theme.UiFontSize + 1, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _hdrSummary = new TextBlock
        {
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 0, 0),
        };
        _hdrSummaryTail = new TextBlock
        {
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Created before the bar so the bar's card can host it: the expanded
        // text belongs INSIDE the panel it expands, not floating on the
        // window background under it.
        _helpDetails = new StackPanel { Margin = new Thickness(2, 10, 0, 2) };
        _helpBar = BuildHelpBar();
        stack.Children.Add(_helpBar);
        _hdrLine1 = new TextBlock
        {
            Foreground = Theme.FgBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            Margin = new Thickness(0, 6, 0, 0),
        };
        _helpDetails.Children.Add(_hdrLine1);
        _hdrLine2 = new TextBlock
        {
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _helpDetails.Children.Add(_hdrLine2);

        _hdrExampleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _hdrExamplePrefix = new TextBlock
        {
            Foreground = Theme.FgLinkBrush, FontFamily = Theme.MonoFontFamily, FontSize = Theme.MonoFontSize,
        };
        _hdrExampleRow.Children.Add(_hdrExamplePrefix);
        // The example time codes themselves are a literal format demo, not
        // language-dependent prose - only the "Example:" lead-in is translated.
        // FlowDirection is pinned LeftToRight regardless of the picked
        // language (independent of _hdrExampleRow's own FlowDirection, set
        // in ApplyLanguage): the bidi algorithm would otherwise be free to
        // reorder the neutral ':' and '-' punctuation around the digit runs
        // under an RTL row, which would make the format demo misleading.
        _hdrExampleRow.Children.Add(new TextBlock
        {
            Text = "  00:10:00-00:35:78, 00:43:00-01:12:47",
            Foreground = Theme.FgLinkBrush, FontFamily = Theme.MonoFontFamily, FontSize = Theme.MonoFontSize,
            FlowDirection = FlowDirection.LeftToRight,
        });
        _helpDetails.Children.Add(_hdrExampleRow);

        _hdrLine4 = new TextBlock
        {
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _helpDetails.Children.Add(_hdrLine4);
        _hdrLine5 = new TextBlock
        {
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _helpDetails.Children.Add(_hdrLine5);
        // NOT added to the outer stack - BuildHelpBar already put it inside
        // the instruction card.

        // Restored before the first layout so the window's one-time natural
        // height measurement in FitInitialSize sees the state the user
        // actually left it in, rather than measuring expanded and then
        // collapsing afterwards.
        SetHelpExpanded(_config.TryGetValue(HelpExpandedKey, out var saved) ? saved != "0" : true, persist: false);

        Grid.SetColumn(stack, 0);
        _headerGrid.Children.Add(stack);

        // Column 1 (Auto-width, top-right) - deliberately never touched by
        // language/FlowDirection changes, so this corner stays in the exact
        // same place for every language (see ApplyLanguage). Both buttons
        // sit in one vertical stack rather than being placed independently,
        // so "settings above language" is a layout fact instead of two
        // separately-tuned margins that could drift apart.
        var cornerStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 16, Theme.PagePad, 0),
        };
        _settingsButton = BuildSettingsButton();
        _settingsButton.Margin = new Thickness(0, 0, 0, 8);
        cornerStack.Children.Add(_settingsButton);

        _languageButton = BuildLanguageButton();
        cornerStack.Children.Add(_languageButton);

        Grid.SetColumn(cornerStack, 1);
        _headerGrid.Children.Add(cornerStack);

        return _headerGrid;
    }

    /// <summary>
    /// The always-visible one-liner: a hint icon, "How it works", a summary
    /// of what to type, and the expand/collapse control on the right.
    ///
    /// The whole bar is clickable, not just the button - a one-line strip
    /// with a disclosure control on it reads as a thing you click, and
    /// making only the last 90 DIP of it live would be a small trap.
    /// </summary>
    private UIElement BuildHelpBar()
    {
        var bar = new DockPanel { LastChildFill = true };

        _helpToggle = UiHelpers.CreateFlatButton("", ButtonVariant.Ghost,
            fontSize: Theme.UiFontSize - 1, bold: false, padH: 12, padV: 5, icon: Icons.ChevronDown);
        _helpToggle.VerticalAlignment = VerticalAlignment.Center;
        _helpToggle.Click += (_, _) => SetHelpExpanded(!_helpExpanded, persist: true);
        DockPanel.SetDock(_helpToggle, Dock.Right);
        bar.Children.Add(_helpToggle);

        // The example timecode is a mono chip embedded in the sentence, not
        // more prose: it is a literal format demo, and setting it as a chip
        // is what makes "type it exactly like this" legible at a glance.
        // FlowDirection is pinned LeftToRight for the same reason the
        // expanded panel's demo is - the bidi algorithm would otherwise be
        // free to reorder the neutral ':' and '-' around the digit runs.
        _hdrSummaryExample = new Border
        {
            Background = Theme.BgCardHlBrush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.ChipRadius),
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "00:10:00-00:35:78",
                Foreground = Theme.AccentCyan300Brush,
                FontFamily = Theme.MonoFontFamily,
                FontSize = Theme.ChipFontSize,
                FlowDirection = FlowDirection.LeftToRight,
            },
        };

        var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_hdrTitle);
        text.Children.Add(_hdrSummary);
        text.Children.Add(_hdrSummaryExample);
        text.Children.Add(_hdrSummaryTail);
        bar.Children.Add(text);

        // The clickable strip is the BAR, not the whole card: the card also
        // holds the expanded text, and a click anywhere in a paragraph you
        // are reading should not fold it away.
        bar.Background = Theme.TransparentBrush;
        bar.Cursor = Cursors.Hand;
        bar.MouseLeftButtonUp += (_, e) =>
        {
            // The button raises its own handler; without this the bar would
            // toggle a second time and land back where it started.
            if (e.OriginalSource is DependencyObject src && UiHelpers.IsInside(src, _helpToggle)) return;
            SetHelpExpanded(!_helpExpanded, persist: true);
        };

        var body = new StackPanel();
        body.Children.Add(bar);
        body.Children.Add(_helpDetails);

        return new Border
        {
            Background = Theme.BgCardBrush,
            BorderBrush = Theme.BorderSoftBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.CardRadius),
            Padding = new Thickness(Theme.CardPadH, 8, 8, 8),
            Child = body,
        };
    }

    /// <summary>Shows or hides the instruction body, updates the toggle, and
    /// re-measures the window. <paramref name="persist"/> is false for the
    /// restore at construction time, which must not write back what it just
    /// read - and must not call RefitHeight either, since the window has not
    /// been laid out yet.</summary>
    private void SetHelpExpanded(bool expanded, bool persist)
    {
        _helpExpanded = expanded;
        _helpDetails.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        // All THREE pieces of the one-line summary go together - the lead
        // text, the mono example chip between them and the tail. Hiding only
        // the lead (the first version) left the chip and the tail sitting
        // next to the title while the full text was already showing below.
        var summary = expanded ? Visibility.Collapsed : Visibility.Visible;
        _hdrSummary.Visibility = summary;
        _hdrSummaryExample.Visibility = summary;
        _hdrSummaryTail.Visibility = summary;
        UiHelpers.SetButtonText(_helpToggle, Loc.T(expanded ? "CollapseLabel" : "ExpandLabel"));
        UiHelpers.SetButtonIcon(_helpToggle, expanded ? Icons.ChevronDown : Icons.ChevronRight);
        if (persist)
        {
            _config[HelpExpandedKey] = expanded ? "1" : "0";
            ConfigStore.Save((HelpExpandedKey, expanded ? "1" : "0"));
            RefitHeight();
        }
    }

    /// <summary>The gear icon above the language switcher - opens a small
    /// settings popup. Currently holds just the file-association toggle,
    /// but the popup exists as a general home for whatever settings get
    /// added later, so new ones are just another row in <see cref="ShowSettingsPopup"/>.</summary>
    private Button BuildSettingsButton()
    {
        var btn = UiHelpers.CreateIconButton(Icons.Gear, Theme.BgRowBrush, Theme.FgBrush, Theme.BgRowHlBrush,
            "Settings", size: 34);
        btn.Click += (_, _) => ShowSettingsPopup(btn);
        return btn;
    }

    /// <summary>The globe icon - opens a popup listing every language in
    /// <see cref="Loc.Languages"/>. Lives in the header row (not the
    /// toolbar) so it stays in a fixed corner of the window regardless of
    /// which lower sections are shown or hidden.</summary>
    private Button BuildLanguageButton()
    {
        var btn = UiHelpers.CreateIconButton(Icons.Globe, Theme.BgRowBrush, Theme.FgBrush, Theme.BgRowHlBrush,
            "Language", size: 34);
        btn.Click += (_, _) => ShowLanguagePopup(btn);
        return btn;
    }

    /// <summary>Builds and opens the settings popup as the same lightweight,
    /// self-closing Popup the language picker uses. One row today (the
    /// .clipglue file-association toggle, added per Bartek's request); more
    /// rows are meant to be appended here as future settings arrive.</summary>
    private void ShowSettingsPopup(Button anchor)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -220,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        var list = new StackPanel { Width = 260 };
        list.Children.Add(new TextBlock
        {
            Text = Loc.T("SettingsPanelTitle"),
            Foreground = Theme.FgBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            FontWeight = FontWeights.Bold, Margin = new Thickness(10, 6, 10, 8),
        });

        bool registered = FileAssociation.IsRegistered();
        var row = new Border
        {
            Padding = new Thickness(12, 8, 14, 8),
            Background = Theme.TransparentBrush,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Cursor = Cursors.Hand,
        };
        var rowStack = new StackPanel { Orientation = Orientation.Horizontal };
        rowStack.Children.Add(Icons.Stroked(registered ? Icons.Trash : Icons.Reload, Theme.FgBrush, 1.6));
        rowStack.Children.Add(new TextBlock
        {
            Text = Loc.T(registered ? "RemoveFileAssociationAction" : "RestoreFileAssociationAction"),
            Foreground = Theme.FgBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, Width = 200,
        });
        row.Child = rowStack;
        row.MouseEnter += (_, _) => row.Background = Theme.BgRowHlBrush;
        row.MouseLeave += (_, _) => row.Background = Theme.TransparentBrush;
        row.MouseLeftButtonDown += (_, _) =>
        {
            if (registered) FileAssociation.Unregister();
            else FileAssociation.Reregister();
            popup.IsOpen = false;
        };
        list.Children.Add(row);

        list.Children.Add(new TextBlock
        {
            Text = Loc.T("FileAssociationDescription"),
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize - 2,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 12, 6),
        });

        AddToggleRow(list, AppSettings.SegmentStripVisible, on =>
        {
            AppSettings.SetSegmentStripVisible(on);
            _projectPreview.SetStripVisible(on);
            RefitHeight();
        }, "SegmentStripToggleAction", "SegmentStripToggleDescription");

        AddToggleRow(list, AppSettings.VerboseLogging, AppSettings.SetVerboseLogging,
            "VerboseLoggingAction", "VerboseLoggingDescription");

        AddToggleRow(list, AppSettings.DetailedProgressView, on =>
        {
            AppSettings.SetDetailedProgressView(on);
            SetProgressViewMode(on);
        }, "DetailedProgressToggleAction", "DetailedProgressToggleDescription");

        popup.Child = new Border
        {
            Background = Theme.BgPanelBrush, BorderBrush = Theme.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.PanelRadius), Padding = new Thickness(6),
            Child = list,
        };
        popup.IsOpen = true;
    }

    /// <summary>One settings-panel row: a toggle switch docked right of its
    /// action label, followed by a dimmer description line below. Shared by
    /// every on/off setting in <see cref="ShowSettingsPopup"/> instead of
    /// each repeating the same Border/DockPanel/ToggleSwitch scaffolding.</summary>
    private void AddToggleRow(Panel list, bool initial, Action<bool> onToggle, string actionKey, string descKey)
    {
        var row = new Border { Padding = new Thickness(12, 8, 14, 8), Background = Theme.TransparentBrush };
        var dock = new DockPanel();
        var toggle = new ToggleSwitch(initial);
        toggle.Toggled += onToggle;
        DockPanel.SetDock(toggle, Dock.Right);
        dock.Children.Add(toggle);
        dock.Children.Add(new TextBlock
        {
            Text = Loc.T(actionKey),
            Foreground = Theme.FgBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
        });
        row.Child = dock;
        list.Children.Add(row);

        list.Children.Add(new TextBlock
        {
            Text = Loc.T(descKey),
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize - 2,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 12, 6),
        });
    }

    /// <summary>Builds and opens the language picker as a lightweight,
    /// self-closing Popup rather than a WPF ContextMenu - keeps the same
    /// flat-card visual language as the rest of the app (rounded panel,
    /// Theme brushes) instead of the OS's native menu chrome.</summary>
    private void ShowLanguagePopup(Button anchor)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -160,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        var list = new StackPanel();
        foreach (var lang in Loc.Languages)
        {
            bool selected = lang.Code == Loc.Current;
            var row = new Border
            {
                Padding = new Thickness(12, 8, 14, 8),
                Background = selected ? Theme.BgRowHlBrush : Theme.TransparentBrush,
                CornerRadius = new CornerRadius(Theme.InputRadius),
                Cursor = Cursors.Hand,
            };
            var rowStack = new StackPanel { Orientation = Orientation.Horizontal };
            rowStack.Children.Add(new Border
            {
                Background = Theme.BgInputBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 2, 5, 2),
                Width = 30,
                Child = new TextBlock
                {
                    Text = lang.Badge, Foreground = Theme.FgDimBrush, FontFamily = Theme.MonoFontFamily,
                    FontSize = Theme.UiFontSize - 2, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            });
            rowStack.Children.Add(new TextBlock
            {
                Text = lang.NativeName, Foreground = selected ? Theme.FgAccentBrush : Theme.FgBrush,
                FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
                FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            });
            row.Child = rowStack;
            row.MouseEnter += (_, _) => { if (!selected) row.Background = Theme.BgRowHlBrush; };
            row.MouseLeave += (_, _) => { if (!selected) row.Background = Theme.TransparentBrush; };
            row.MouseLeftButtonDown += (_, _) =>
            {
                Loc.SetLanguage(lang.Code);
                popup.IsOpen = false;
            };
            list.Children.Add(row);
        }

        var scroller = new ScrollViewer
        {
            Content = list,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        scroller.Resources.Add(typeof(ScrollBar), ScrollBarStyles.Thin);

        popup.Child = new Border
        {
            Background = Theme.BgPanelBrush, BorderBrush = Theme.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.PanelRadius), Padding = new Thickness(6),
            Child = scroller,
        };
        popup.IsOpen = true;
    }

    /// <summary>
    /// The toolbar carries the file-level buttons on the left and the project
    /// preview toggle pushed to the right. The toggle lives HERE rather than
    /// down with START/STOP because opening the preview hides that whole
    /// action bar - a button that hides itself is a button you cannot press
    /// again to undo what it did. The toolbar row survives in both states.
    /// </summary>
    private UIElement BuildToolbar()
    {
        var bar = new Grid { Margin = new Thickness(Theme.PagePad, Theme.SectionGap, Theme.PagePad, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        _addButton = UiHelpers.CreateFlatButton("Add video files", ButtonVariant.Primary, icon: Icons.Plus);
        _addButton.Click += OnAddFilesClicked;
        row.Children.Add(_addButton);

        _saveProjectButton = UiHelpers.CreateFlatButton("Save project", ButtonVariant.Ghost, icon: Icons.Save);
        _saveProjectButton.Margin = new Thickness(10, 0, 0, 0);
        _saveProjectButton.Click += OnSaveProjectClicked;
        row.Children.Add(_saveProjectButton);

        _loadProjectButton = UiHelpers.CreateFlatButton("Load project", ButtonVariant.Ghost, icon: Icons.FolderOpen);
        _loadProjectButton.Margin = new Thickness(10, 0, 0, 0);
        _loadProjectButton.Click += OnLoadProjectClicked;
        row.Children.Add(_loadProjectButton);

        bar.Children.Add(row);

        _previewProjectButton = UiHelpers.CreateFlatButton("Preview project", ButtonVariant.Ghost, icon: Icons.Eye);
        // A ghost carrying the accent in its TEXT, not its fill: the token
        // file reserves the gradient for the actions that start something,
        // and two gold blocks in one toolbar would make neither of them read
        // as the primary one.
        UiHelpers.UpdateButtonColors(_previewProjectButton, Theme.BgCardBrush, Theme.FgAccentBrush, Theme.BgCardHlBrush);
        _previewProjectButton.Margin = new Thickness(10, 0, 0, 0);
        _previewProjectButton.ToolTip = "Play every range of every file back to back, as the output will be";
        _previewProjectButton.Click += OnPreviewProjectClicked;
        Grid.SetColumn(_previewProjectButton, 1);
        bar.Children.Add(_previewProjectButton);

        return bar;
    }

    private UIElement BuildFileListArea()
    {
        _listWrap = new Border
        {
            Background = Theme.BgPanelBrush, BorderBrush = Theme.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.PanelRadius),
            Margin = new Thickness(0),
        };
        var overlayGrid = new Grid();

        // VerticalAlignment must be pinned to Top: ScrollViewer's content
        // presenter only anchors content to the top-left when the content's
        // extent actually exceeds the viewport (so there's something to
        // scroll to). Once the list area's Star-sized row grows taller than
        // the row list itself - e.g. the window gets resized/maximized with
        // only a couple of files loaded - the Canvas's explicit Height is
        // smaller than the viewport, and a bare FrameworkElement in that
        // situation falls back to the default Stretch alignment behavior,
        // which centers content that's smaller than the space available
        // instead of leaving it flush at the top.
        _rowsCanvas = new Canvas { Background = Theme.BgPanelBrush, VerticalAlignment = VerticalAlignment.Top };
        _listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // A whole number of row slots, so the viewport never cuts a card
            // in half: the next one starts precisely at the bottom edge and
            // only appears once you scroll. Four of them now that the cards
            // are slimmer - that keeps the list about the height it already
            // was, and spends the height saved on showing another file
            // rather than on empty window.
            MinHeight = 4 * Theme.RowH,
            Margin = new Thickness(10, 10, 4, 10),
            Content = _rowsCanvas,
        };
        // Thin rounded scrollbar, scoped to this ScrollViewer's subtree
        // rather than applied window-wide, so nothing else (the preview
        // player's range ListBox in particular) is restyled by accident.
        _listScroll.Resources.Add(typeof(ScrollBar), ScrollBarStyles.Thin);
        // Only the row children get an explicit Width - the Canvas itself
        // already stretches to the viewport via its default Stretch
        // alignment (HorizontalScrollBarVisibility is Disabled, so nothing
        // clips it). Setting Canvas.Width too used to feed that same
        // viewport-derived number back into a live layout pass; during the
        // window's one-time SizeToContent measurement (see FitInitialSize)
        // that turned into a runaway feedback loop - an oversized interim
        // viewport reading got baked into Canvas.Width, which then inflated
        // the *next* measurement, and so on until the window converged on a
        // near-screen-filling size that FitInitialSize then locked in as
        // MinWidth/MinHeight, leaving the window stuck and unresizable.
        //
        // Each row's Width is bound (not just assigned once) to
        // ScrollViewer.ViewportWidth rather than copied from it in
        // AddFileRows: ViewportWidth reads back as 0 until the ScrollViewer
        // completes its first real layout pass, and a plain assignment made
        // before that pass silently left the row at its Auto/unconstrained
        // size - a Grid whose last column is a Star (the range-text box)
        // resolves a Star column to 0 width under an unconstrained/Auto
        // measure, so the row rendered just wide enough for the delete/
        // handle/index columns and the name, preview button and range box
        // were clipped by the ScrollViewer's own viewport clip. A binding
        // re-evaluates itself once ViewportWidth actually changes to a real
        // value, so it self-corrects with no dependency on a later resize
        // or scroll event ever happening.
        overlayGrid.Children.Add(_listScroll);

        _emptyLabel = new TextBlock
        {
            Text = "No files yet - click “Add video files” to start.",
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 46, 0, 0), IsHitTestVisible = false,
        };
        overlayGrid.Children.Add(_emptyLabel);

        _listWrap.Child = overlayGrid;
        return _listWrap;
    }

    private UIElement BuildActionBar()
    {
        var outer = new StackPanel { Margin = new Thickness(0, Theme.SectionGap, 0, 0) };
        var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal };

        _startButton = UiHelpers.CreateFlatButton("START", ButtonVariant.Primary, icon: Icons.PlaySolid);
        _startButton.Click += OnStartClicked;
        buttonsRow.Children.Add(_startButton);

        _stopButton = UiHelpers.CreateFlatButton("STOP", ButtonVariant.Danger);
        _stopButton.Margin = new Thickness(10, 0, 0, 0);
        _stopButton.Click += OnStopClicked;
        buttonsRow.Children.Add(_stopButton);
        UiHelpers.SetButtonEnabled(_stopButton, false);

        _queueButton = UiHelpers.CreateFlatButton("Add to queue", ButtonVariant.Ghost);
        _queueButton.Margin = new Thickness(18, 0, 0, 0);
        _queueButton.Click += OnAddToQueueClicked;
        buttonsRow.Children.Add(_queueButton);

        _clearQueueButton = UiHelpers.CreateFlatButton("Clear queue", ButtonVariant.Ghost);
        _clearQueueButton.Margin = new Thickness(10, 0, 0, 0);
        _clearQueueButton.Click += OnClearQueueClicked;
        buttonsRow.Children.Add(_clearQueueButton);

        _queueLabel = new TextBlock
        {
            Foreground = Theme.FgDimBrush, FontFamily = Theme.UiFontFamily, FontSize = Theme.UiFontSize,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        buttonsRow.Children.Add(_queueLabel);
        UpdateQueueLabel();

        outer.Children.Add(buttonsRow);

        // The progress card lives here, in the action bar, rather than next
        // to the preview panel - it reports on the cutting job, which has
        // nothing to do with whether the preview happens to be open, and it
        // must stay visible when the preview is closed (see the right-column
        // comment in the constructor). Built once, here, so there is only
        // ever one instance.
        outer.Children.Add(BuildProgressCard());

        return outer;
    }

    /// <summary>
    /// One card carrying both progress bars, the currently-processed file
    /// name and the four job statistics - the "szczegółowy" (detailed) of
    /// the two progress views, opt-in from the settings popup via
    /// <see cref="AppSettings.DetailedProgressView"/>; see
    /// <see cref="BuildMiniProgressBar"/> for the default one.
    ///
    /// The bars are labelled and stacked ("This file" over "All files")
    /// rather than distinguished only by color, which is what the old pair
    /// relied on and which said nothing about which was which. Both use the
    /// gold-to-cyan gradient from the token file; they no longer need
    /// different fills to be told apart.
    /// </summary>
    private UIElement BuildProgressCard()
    {
        var content = new StackPanel { Margin = new Thickness(Theme.CardPadH, Theme.PanelPadV, Theme.CardPadH, Theme.PanelPadV) };

        _progressHeaderLabel = new TextBlock
        {
            Foreground = Theme.TextLowBrush,
            FontFamily = Theme.HeadFontFamily,
            FontSize = Theme.LabelFontSize,
            Margin = new Thickness(0, 0, 0, 10),
        };
        content.Children.Add(_progressHeaderLabel);

        // Each bar carries its readings BELOW it rather than inside it: the
        // mockup puts the percentage on the left and the matching metric on
        // the right, which leaves the bar itself a clean shape and gives the
        // speed and ETA a place that is tied to the thing they describe.
        // Overall (all-files) progress leads and gets the full-size bar - it
        // is the more important of the two, and used to be drawn smaller and
        // second, which read backwards. The current-step bar follows,
        // slightly smaller, exactly as the overall bar used to be.
        _progress = new LabeledProgressBar { FillColor = Theme.ProgressGradientBrush, ShowLabel = false };
        content.Children.Add(_progress);
        (_totalPercent, _statEtaValue) = AddProgressReadout(content);

        _stepProgress = new LabeledProgressBar
        {
            FillColor = Theme.ProgressGradientBrush,
            ShowLabel = false,
            Height = Theme.BarH - 6,
            Margin = new Thickness(0, 10, 0, 0),
        };
        content.Children.Add(_stepProgress);
        (_stepPercent, _statSpeedValue) = AddProgressReadout(content);

        // The name of the file currently being cut used to sit in the card's
        // header, opposite "FILE PROGRESS" - moved here, directly under the
        // current-step bar's own readout, since it is that bar's step (not
        // the overall job) that the name identifies.
        _progressFileName = new TextBlock
        {
            Foreground = Theme.FgAccentBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize - 1,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(2, 4, 2, 0),
        };
        content.Children.Add(_progressFileName);

        var stats = BuildStatsPanel();
        content.Children.Add(stats);

        var card = new Border
        {
            Background = Theme.BgCardBrush,
            BorderBrush = Theme.BorderSoftBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.CardRadius),
            Margin = new Thickness(0, Theme.SectionGap, 0, 0),
        };
        var layers = new Grid();
        layers.Children.Add(new Border
        {
            Height = Theme.CardTopHighlightH,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Theme.CardTopHighlightInset, 0, Theme.CardTopHighlightInset, 0),
            Background = Theme.CardTopHighlightBrush,
            IsHitTestVisible = false,
        });
        layers.Children.Add(content);
        card.Child = layers;
        _progressCardBorder = card;
        return card;
    }

    /// <summary>
    /// The default ("minimalistyczny") progress view: a single status-bar
    /// strip pinned to the very bottom of the window (its own row in the
    /// root grid, not nested in either column - see the constructor), just
    /// the overall bar with its percentage and ETA, nothing else. Swapping
    /// to the detailed card in settings does not replace this instance;
    /// both exist at all times and both are kept in sync by every progress
    /// update, so toggling between them in settings shows the current state
    /// immediately with no extra plumbing - see SetProgressViewMode.
    /// </summary>
    private Border BuildMiniProgressBar()
    {
        var row = new Grid { Margin = new Thickness(Theme.PagePad, 8, Theme.PagePad, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _miniPercent = new TextBlock
        {
            Text = "0%",
            Foreground = Theme.TextMidBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(_miniPercent, 0);
        row.Children.Add(_miniPercent);

        _miniProgress = new LabeledProgressBar
        {
            FillColor = Theme.ProgressGradientBrush,
            ShowLabel = false,
            Height = Theme.BarH - 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_miniProgress, 1);
        row.Children.Add(_miniProgress);

        _miniEta = new TextBlock
        {
            Text = "—",
            Foreground = Theme.TextLowBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(_miniEta, 2);
        row.Children.Add(_miniEta);

        return new Border
        {
            Background = Theme.BgCardBrush,
            BorderBrush = Theme.BorderSoftBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = row,
        };
    }

    /// <summary>Switches between the minimal footer bar (default) and the
    /// detailed card in the action bar. Both instances always exist and are
    /// always kept up to date (see SetProgress/SetStepProgress/SetCurrentFile/
    /// UpdateStatsDisplay) - this only ever flips which one is on screen, so
    /// there is nothing to re-sync on switch.</summary>
    private void SetProgressViewMode(bool detailed)
    {
        _progressCardBorder.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
        _miniProgressBar.Visibility = detailed ? Visibility.Collapsed : Visibility.Visible;
        RefitHeight();
    }

    /// <summary>The line under a progress bar: percentage on the left, the
    /// metric that belongs to that bar on the right. Returns both so the
    /// caller can keep updating them.</summary>
    private static (TextBlock Percent, TextBlock Metric) AddProgressReadout(Panel host)
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 5, 2, 0) };

        var percent = new TextBlock
        {
            Text = "0%",
            Foreground = Theme.TextMidBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
        };
        DockPanel.SetDock(percent, Dock.Left);
        row.Children.Add(percent);

        var metric = new TextBlock
        {
            Text = "—",
            Foreground = Theme.TextLowBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
        };
        DockPanel.SetDock(metric, Dock.Right);
        row.Children.Add(metric);

        host.Children.Add(row);
        return (percent, metric);
    }

    /// <summary>Live speed/ETA/CPU/memory readout for the running job, per
    /// Bartek's request. Always present (not collapsed while idle) so its
    /// height is part of the window's one-time natural-size measurement in
    /// FitInitialSize - toggling it in and out at runtime, the way the
    /// project preview panel does, would need the same MinHeight dance
    /// that panel needs (see CLAUDE.md pitfalls #20-21) for a strip this
    /// small, which isn't worth it. Placeholder dashes show while idle or
    /// before the first sample of a job lands.</summary>
    private UIElement BuildStatsPanel()
    {
        // TWO chips, not four: the mockup keeps speed and ETA on the lines
        // under the bars they describe, which leaves only the two averages
        // that belong to the machine rather than to a bar.
        var grid = new Grid();
        for (int i = 0; i < 2; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        (_statCpuLabel, _statCpuValue) = AddStatTile(grid, 0, 0, Icons.Cpu);
        (_statMemLabel, _statMemValue) = AddStatTile(grid, 0, 1, Icons.Chart);

        _statsPanel = new Border
        {
            Background = Theme.TransparentBrush,
            Margin = new Thickness(0, 12, 0, 0),
            Child = grid,
        };
        return _statsPanel;
    }

    private static (TextBlock Label, TextBlock Value) AddStatTile(Grid grid, int row, int column, Geometry icon)
    {
        var label = new TextBlock
        {
            Foreground = Theme.TextMidBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.LabelFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var value = new TextBlock
        {
            Text = "—",
            Foreground = Theme.TextHiBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.UiFontSize,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = Icons.Stroked(icon, Theme.TextLowBrush, 1.3);
        glyph.Margin = new Thickness(0, 0, 6, 0);
        head.Children.Add(glyph);
        head.Children.Add(label);

        var box = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(head, Dock.Left);
        box.Children.Add(head);
        DockPanel.SetDock(value, Dock.Right);
        box.Children.Add(value);

        var chip = new Border
        {
            Background = Theme.BgCardHlBrush,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Padding = new Thickness(10, 7, 12, 7),
            Margin = new Thickness(column == 0 ? 0 : 5, row == 0 ? 0 : 8, column == 0 ? 5 : 0, 0),
            Child = box,
        };
        Grid.SetRow(chip, row);
        Grid.SetColumn(chip, column);
        grid.Children.Add(chip);
        return (label, value);
    }

    /// <summary>Pushes a fresh stats reading to the panel, or resets it to
    /// placeholders when <paramref name="snapshot"/> is null (idle). Always
    /// called on the UI thread - callers on the worker thread go through
    /// <see cref="SetStats"/> instead.
    ///
    /// Speed/CPU/memory and ETA are gated independently rather than all-or-
    /// nothing on HasData: ETA can have a real value (the first probe step
    /// already completed) before any ffmpeg process has been sampled yet
    /// (cutting hasn't started), and hiding a perfectly good ETA behind
    /// "no process sampled yet" would just be a second way to make it look
    /// broken.</summary>
    private void UpdateStatsDisplay(JobStatsSnapshot? snapshot)
    {
        if (snapshot is not { } s)
        {
            _statSpeedValue.Text = "—";
            _statEtaValue.Text = "—";
            _statCpuValue.Text = "—";
            _statMemValue.Text = "—";
            _miniEta.Text = "—";
            return;
        }
        // Speed and ETA now live on the readout line under their bar, where
        // there is no separate caption element - so each carries its own
        // label inline instead.
        _statSpeedValue.Text = s.HasData
            ? $"{Loc.T("StatSpeedLabel")} {s.SpeedMbps.ToString("F1", CultureInfo.InvariantCulture)} Mb/s"
            : "—";
        _statEtaValue.Text = s.Eta is { } eta ? $"{Loc.T("StatEtaLabel")} {FormatDuration(eta)}" : "—";
        _statCpuValue.Text = s.HasData ? s.AvgCpuPercent.ToString("F0", CultureInfo.InvariantCulture) + "%" : "—";
        _statMemValue.Text = s.HasData ? s.AvgMemoryMb.ToString("F0", CultureInfo.InvariantCulture) + " MB" : "—";
        _miniEta.Text = _statEtaValue.Text;
    }

    private static string FormatDuration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{t.Minutes:D2}:{t.Seconds:D2}";

    private void SetStats(JobStatsSnapshot snapshot) => Dispatcher.BeginInvoke(() => UpdateStatsDisplay(snapshot));

    /// <summary>The whole-project preview, collapsed until asked for. It has
    /// to be collapsible rather than always on: the window's natural height
    /// already sits close to this machine's work area, so a permanently
    /// visible player would leave nothing for the file list.</summary>
    private UIElement BuildProjectPreview()
    {
        _projectPreview = new ProjectPreviewPanel
        {
            Margin = new Thickness(0),
            // Visible by default - the mockup calls it a permanent panel, and in
            // a column of its own it no longer competes with the file list for
            // height, so there is nothing to be gained by hiding it.
        };
        _projectPreview.CloseRequested += CloseProjectPreview;
        _projectPreview.ReloadRequested += () => _projectPreview.LoadProject(CollectProjectLoosely());
        return _projectPreview;
    }

    private bool IsProjectPreviewOpen => _projectPreview.Visibility == Visibility.Visible;

    private void OnPreviewProjectClicked(object sender, RoutedEventArgs e)
    {
        if (IsProjectPreviewOpen) CloseProjectPreview();
        else OpenProjectPreview();
    }

    /// <summary>
    /// Shows the right-hand column: the preview panel and the progress card
    /// under it.
    ///
    /// It is visible by DEFAULT now ("stały panel podglądu" in the mockup),
    /// and the toolbar button hides it rather than summons it. That inverts
    /// the old arrangement, where the preview was stacked under the file list
    /// and had to fight it for vertical space - the reason the action bar and
    /// console used to be hidden for the duration, at ~318 DIP of a 912 DIP
    /// work area. In a column of its own the preview costs WIDTH, so nothing
    /// has to be taken away to make room for it and everything stays where
    /// the user left it.
    /// </summary>
    private void OpenProjectPreview()
    {
        _projectPreview.Visibility = Visibility.Visible;
        _rightColumn.Visibility = Visibility.Visible;
        _previewColumn.Width = new GridLength(42, GridUnitType.Star);
        _previewColumn.MinWidth = 340;
        UiHelpers.SetButtonText(_previewProjectButton, Loc.T("BtnHidePreview"));
        _projectPreview.LoadProject(CollectProjectLoosely());
        UpdatePreviewButtonState();
        RefitHeight();
    }

    private void CloseProjectPreview()
    {
        if (!IsProjectPreviewOpen) return;
        // Released, not just paused: a hidden preview must not sit on an open
        // handle to a file the next job is about to read.
        _projectPreview.Release();
        _projectPreview.Visibility = Visibility.Collapsed;
        _rightColumn.Visibility = Visibility.Collapsed;
        // Zero-width, not Auto: an Auto column would still reserve whatever
        // the hidden panel's own MinWidth asked for.
        _previewColumn.Width = new GridLength(0);
        _previewColumn.MinWidth = 0;
        UiHelpers.SetButtonText(_previewProjectButton, Loc.T("BtnPreviewProject"));
        UpdatePreviewButtonState();
        RefitHeight();
    }

    /// <summary>The toggle is dead with an empty file list - there is nothing
    /// to preview - but it stays live while the preview is OPEN even if the
    /// last file has just been deleted, or it would hide itself away with no
    /// way left to close the panel. It must never be disabled while a job is
    /// running either: the progress card lives in its own always-visible spot
    /// now (see BuildActionBar), so showing or hiding the preview no longer
    /// interferes with a running job, and Bartek needs to be able to toggle
    /// it regardless of whether cutting is in progress.</summary>
    private void UpdatePreviewButtonState() =>
        UiHelpers.SetButtonEnabled(_previewProjectButton, _rows.Count > 0 || IsProjectPreviewOpen);

    /// <summary>Re-reads the file list into the preview if it is open. Called
    /// on the structural changes (add / remove / reorder / a trim applied),
    /// not on every keystroke in a range box - reloading swaps the decoder,
    /// which is far too heavy to do per character. Hand-typed edits are
    /// picked up by the panel's own reload button.</summary>
    private void ReloadProjectPreviewIfOpen()
    {
        if (IsProjectPreviewOpen) _projectPreview.LoadProject(CollectProjectLoosely());
    }

    /// <summary>The file list as the preview wants it: unparseable chunks are
    /// dropped in silence rather than reported. This is deliberately NOT
    /// <see cref="CollectAndValidate"/> - that one is the gate in front of an
    /// encode and is right to stop with a message box, whereas a preview of a
    /// half-finished project should just show the half that is finished.</summary>
    private List<ProjectClip> CollectProjectLoosely()
    {
        var clips = new List<ProjectClip>();
        foreach (var row in _rows)
        {
            var ranges = new List<TimeRange>();
            foreach (var chunkRaw in row.RangesText.Split(','))
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
                catch (FormatException) { /* still being typed - skip it */ }
            }
            if (ranges.Count > 0) clips.Add(new ProjectClip(row.FilePath, ranges));
        }
        return clips;
    }

    private UIElement BuildConsole()
    {
        var stack = new StackPanel();

        // The header is now a drawer handle: chevron, label, and a badge
        // counting what has been written since it was last open. The body
        // starts closed - it used to hold a fixed 193 DIP of a 912 DIP work
        // area permanently, almost always showing nothing.
        var headerRow = new DockPanel { LastChildFill = false };

        _consoleChevron = Icons.Stroked(Icons.ChevronRight, Theme.TextMidBrush, 1.5);
        _consoleChevron.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(_consoleChevron, Dock.Left);
        headerRow.Children.Add(_consoleChevron);

        var termIcon = Icons.Stroked(Icons.Terminal, Theme.TextLowBrush, 1.4);
        termIcon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(termIcon, Dock.Left);
        headerRow.Children.Add(termIcon);

        _consoleHeaderText = new TextBlock
        {
            Foreground = Theme.FgDimBrush, FontFamily = Theme.HeadFontFamily,
            FontSize = Theme.UiFontSize, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(_consoleHeaderText, Dock.Left);
        headerRow.Children.Add(_consoleHeaderText);

        _consoleBadgeText = new TextBlock
        {
            Foreground = Theme.OnAccentBrush, FontFamily = Theme.UiFontFamily,
            FontSize = Theme.LabelFontSize, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _consoleBadge = new Border
        {
            Background = Theme.AccentGradientBrush,
            CornerRadius = new CornerRadius(Theme.ChipRadius),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _consoleBadgeText,
        };
        DockPanel.SetDock(_consoleBadge, Dock.Left);
        headerRow.Children.Add(_consoleBadge);

        _copyLogButton = UiHelpers.CreateIconButton(Icons.Copy, Theme.TransparentBrush, Theme.FgDimBrush,
            Theme.BgRowHlBrush, "Copy log", size: 28);
        _copyLogButton.Click += (_, _) => CopyLogToClipboard();
        DockPanel.SetDock(_copyLogButton, Dock.Right);
        headerRow.Children.Add(_copyLogButton);

        _consoleHeaderBar = new Border
        {
            Background = Theme.BgCardBrush,
            BorderBrush = Theme.BorderSoftBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.CardRadius),
            Padding = new Thickness(Theme.CardPadH, 6, 6, 6),
            Margin = new Thickness(Theme.PagePad, Theme.SectionGap, Theme.PagePad, 0),
            Cursor = Cursors.Hand,
            Child = headerRow,
        };
        AutomationProperties.SetName(_consoleHeaderBar, Loc.T("ConsoleHeader"));
        _consoleHeaderBar.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src && UiHelpers.IsInside(src, _copyLogButton)) return;
            SetConsoleOpen(!_consoleOpen);
        };
        stack.Children.Add(_consoleHeaderBar);

        var logWrap = new Border
        {
            Background = Theme.LogBgBrush, BorderBrush = Theme.BorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.PanelRadius),
            Margin = new Thickness(Theme.PagePad, 10, Theme.PagePad, 16), Height = 130,
            Visibility = Visibility.Collapsed,
        };
        _consoleBody = logWrap;
        _logBox = new RichTextBox
        {
            IsReadOnly = true,
            // Transparent, not LogBg: the RichTextBox is a square-cornered
            // rectangle sitting inside the rounded logWrap, so painting its
            // own background would fill the corners back in and undo the
            // rounding. The Border behind it supplies the color instead.
            Background = Theme.TransparentBrush,
            Foreground = Theme.LogFgBrush,
            BorderThickness = new Thickness(0),
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.MonoFontSize,
            Padding = new Thickness(10, 8, 10, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // RichTextBox's default HorizontalScrollBarVisibility (Hidden)
            // still measures its content against infinite available width
            // to know the scroll extent, even with the bar itself hidden -
            // and even an empty document then reports a huge desired width
            // (the WPF/window-sizing fallback for an "infinite" measurement
            // is roughly the current screen's work-area size). That value
            // fed straight into FitInitialSize's MinWidth, so the window
            // opened locked at a near-screen-filling size. Disabled forces
            // word-wrapped content to measure against the real available
            // width instead - the same fix already used for _listScroll,
            // and it matches the Python original's log Text widget, which
            // wraps ("word") rather than ever needing to scroll sideways.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _logBox.Resources.Add(typeof(ScrollBar), ScrollBarStyles.Thin);
        logWrap.Child = _logBox;
        stack.Children.Add(logWrap);
        return stack;
    }

    /// <summary>Opens or closes the console drawer. Opening clears the
    /// unread counter, which is the whole point of it: it answers "has
    /// anything happened down there since I last looked".</summary>
    private void SetConsoleOpen(bool open)
    {
        _consoleOpen = open;
        _consoleBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _consoleChevron.Data = open ? Icons.ChevronDown : Icons.ChevronRight;
        if (open)
        {
            _unreadLogLines = 0;
            UpdateConsoleBadge();
            _logBox.ScrollToEnd();
        }
        RefitHeight();
    }

    private void UpdateConsoleBadge()
    {
        _consoleBadge.Visibility = _unreadLogLines > 0 && !_consoleOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_unreadLogLines > 0)
            _consoleBadgeText.Text = string.Format(Loc.T("ConsoleNewLinesLabel"), _unreadLogLines);
    }

    // -- empty state ------------------------------------------------------------

    private void ShowEmptyState() => _emptyLabel.Visibility = Visibility.Visible;
    private void HideEmptyState() => _emptyLabel.Visibility = Visibility.Collapsed;

    // -- adding / removing files --------------------------------------------

    /// <summary>
    /// Applies a remembered folder from config as a file dialog's
    /// InitialDirectory, but only when one is actually known and still
    /// exists. Two Windows/WPF-specific traps here, both discovered by
    /// smoke-testing against the real, already-populated config.json this
    /// app shares with the original Python build:
    /// (1) WPF's OpenFileDialog/SaveFileDialog throw an ArgumentException
    /// from inside ShowDialog() if InitialDirectory is ever explicitly
    /// assigned null or a directory that no longer exists - unlike
    /// Tkinter's filedialog, which is happy to take initialdir=None - so
    /// the property must simply be left untouched rather than assigned a
    /// possibly-empty value.
    /// (2) The shared config.json can contain folder paths written by the
    /// Python/Tkinter original, which always returns forward-slash paths
    /// on Windows (e.g. "C:/Users/x/Videos"). WPF's dialog resolves
    /// InitialDirectory through the shell's SHCreateItemFromParsingName,
    /// which expects a native backslash path for a local file-system
    /// location and throws that same ArgumentException on a forward-slash
    /// one even though the directory genuinely exists - so the path must
    /// be normalized via Path.GetFullPath() before use.
    /// </summary>
    private static void ApplyInitialDir(FileDialog dlg, Dictionary<string, string> config, string key)
    {
        if (!config.TryGetValue(key, out var dir) || string.IsNullOrEmpty(dir)) return;
        string normalized;
        try { normalized = Path.GetFullPath(dir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        if (Directory.Exists(normalized))
            dlg.InitialDirectory = normalized;
    }

    /// <summary>Persists the folder <paramref name="path"/> lives in under
    /// <paramref name="key"/>, for <see cref="ApplyInitialDir"/> to restore
    /// next time a dialog for that same purpose opens.</summary>
    private void RememberDir(string key, string? path)
    {
        _config[key] = Path.GetDirectoryName(path) ?? "";
        ConfigStore.Save((key, _config[key]));
    }

    private void OnAddFilesClicked(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("SelectVideoFilesTitle"),
            Filter = "Video files|*.mp4;*.mkv|All files|*.*",
            Multiselect = true,
        };
        ApplyInitialDir(dlg, _config, "last_input_dir");
        if (dlg.ShowDialog(this) != true) return;
        RememberDir("last_input_dir", dlg.FileNames[0]);
        AddFileRows(dlg.FileNames);
    }

    private void AddFileRows(IEnumerable<string> paths)
    {
        HideEmptyState();
        foreach (var path in paths)
        {
            var row = new FileRowControl(path);
            row.SetBinding(WidthProperty, new Binding(nameof(ScrollViewer.ViewportWidth)) { Source = _listScroll });
            row.SetWrapMode(_wrapRows);
            row.DeleteRequested += (_, _) => RemoveRow(row);
            row.PreviewRequested += (_, _) => OpenPreview(row);
            // Adding or removing a chip is a discrete commit, not a
            // keystroke, so it is safe to rebuild the preview from it -
            // RangesEdited deliberately does not fire while typing in the
            // raw field, where reloading would swap the decoder per
            // character.
            row.RangesEdited += (_, _) => ReloadProjectPreviewIfOpen();
            row.RowHeightMayHaveChanged += (_, _) => ReflowRowsForHeightChange();
            row.DragStartRequested += (_, ev) => StartDrag(row, ev);
            row.DragMoveRequested += (_, ev) => DragMotion(row, ev);
            row.DragEndRequested += (_, _) => EndDrag(row);
            row.CurY = _rows.Count * Theme.RowH;
            row.TargetY = row.CurY;
            Canvas.SetTop(row, row.CurY);
            _rows.Add(row);
            _rowsCanvas.Children.Add(row);
        }
        LayoutRows(animate: true);
        ReloadProjectPreviewIfOpen();
        UpdatePreviewButtonState();
    }

    // -- drag-and-drop from Windows Explorer -----------------------------------

    // Matches the OpenFileDialog filter in OnAddFilesClicked - only files the
    // rest of the app can actually probe/cut are accepted, everything else
    // dropped onto the window (folders, images, random files) is ignored.
    private static readonly string[] DroppableVideoExtensions = { ".mp4", ".mkv" };

    private static List<string> GetDroppedVideoFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        var paths = (string[])data.GetData(DataFormats.FileDrop);
        return paths
            .Where(p => DroppableVideoExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase) && File.Exists(p))
            .ToList();
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        bool accept = !_processing && GetDroppedVideoFiles(e.Data).Count > 0;
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        SetFileListDropHighlight(accept);
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        SetFileListDropHighlight(false);
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        SetFileListDropHighlight(false);
        e.Handled = true;
        if (_processing) return;
        var files = GetDroppedVideoFiles(e.Data);
        if (files.Count == 0) return;

        AddFileRows(files);
        RememberDir("last_input_dir", files[0]);
    }

    private void SetFileListDropHighlight(bool active)
    {
        _listWrap.BorderBrush = active ? Theme.FgAccentBrush : Theme.BorderBrush;
        _listWrap.BorderThickness = new Thickness(active ? 2 : 1);
    }

    private void ClearAllRows()
    {
        _rowsCanvas.Children.Clear();
        _rows.Clear();
        ShowEmptyState();
        LayoutRows(animate: false);
        UpdatePreviewButtonState();
    }

    // -- project save/load -----------------------------------------------------

    private void OnSaveProjectClicked(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        if (_rows.Count == 0)
        {
            ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Warning, Loc.T("NoFilesTitle"), Loc.T("NoFilesFirstMessage"), Loc.T("OK"));
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("SaveProjectAsTitle"),
            DefaultExt = ".clipglue",
            Filter = "ClipGlue project|*.clipglue|All files|*.*",
        };
        ApplyInitialDir(dlg, _config, "last_project_dir");
        if (dlg.ShowDialog(this) != true) return;

        var data = new ProjectFile
        {
            Version = 1,
            Files = _rows.Select(r => new ProjectFileEntry { Path = r.FilePath, Ranges = r.RangesText }).ToList(),
        };
        try
        {
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Error, Loc.T("CouldNotSaveTitle"), ex.Message, Loc.T("OK"));
            return;
        }
        RememberDir("last_project_dir", dlg.FileName);
    }

    private void OnLoadProjectClicked(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("LoadProjectTitle"),
            Filter = "ClipGlue project|*.clipglue|All files|*.*",
        };
        ApplyInitialDir(dlg, _config, "last_project_dir");
        if (dlg.ShowDialog(this) != true) return;
        LoadProjectFromPath(dlg.FileName);
    }

    /// <summary>
    /// Loads a .clipglue project file into the row list. Shared by the Load
    /// project dialog above and by App.OnStartup, which calls this with a
    /// path handed to the app on the command line - the double-click /
    /// "Open with ClipGlue" path registered by FileAssociation.
    /// </summary>
    internal void LoadProjectFromPath(string path)
    {
        ProjectFile data;
        try
        {
            var json = File.ReadAllText(path);
            data = JsonSerializer.Deserialize<ProjectFile>(json) ?? throw new InvalidDataException("Empty or unreadable project file.");
            data.Files ??= new List<ProjectFileEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Error, Loc.T("CouldNotLoadTitle"), ex.Message, Loc.T("OK"));
            return;
        }
        RememberDir("last_project_dir", path);

        var missing = data.Files.Where(f => !File.Exists(f.Path)).Select(f => f.Path).ToList();
        var present = data.Files.Where(f => File.Exists(f.Path)).ToList();

        ClearAllRows();
        AddFileRows(present.Select(f => f.Path));
        for (int i = 0; i < present.Count && i < _rows.Count; i++)
            _rows[i].RangesText = present[i].Ranges;
        // RangesText is the programmatic path (see its own doc comment) and
        // deliberately does not raise RowHeightMayHaveChanged - or
        // RangesEdited - the way a chip edit does, so a project loaded
        // straight onto an already-maximized window needs an explicit nudge
        // to measure the real wrapped height of whatever ranges it just
        // restored, and an already-open preview needs an explicit nudge to
        // pick them up too. Without the second one, loading a project while
        // the preview panel is open left it showing whatever it had before
        // (typically the empty "nothing to preview yet" state, since
        // ClearAllRows just emptied the list above) - AddFileRows' own
        // reload ran too early to help, before this loop had put any ranges
        // on the rows it just added.
        ReflowRowsForHeightChange();
        ReloadProjectPreviewIfOpen();

        if (missing.Count > 0)
        {
            ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Warning, Loc.T("SomeFilesMissingTitle"),
                string.Format(Loc.T("SomeFilesMissingMessage"), string.Join("\n", missing)), Loc.T("OK"));
        }
    }

    private void SetRowsEnabled(bool enabled)
    {
        foreach (var row in _rows) row.SetLocked(!enabled);
    }

    private void OpenPreview(FileRowControl row)
    {
        if (_processing || row.Locked) return;
        PreviewPlayerWindow.TryOpen(this, SetDimmed, row.FilePath, row.RangesText, text =>
        {
            row.RangesText = text;
            // Same programmatic-path gap as LoadProjectFromPath: RangesText
            // does not raise RowHeightMayHaveChanged on its own.
            ReflowRowsForHeightChange();
            ReloadProjectPreviewIfOpen();
        });
    }

    private void RemoveRow(FileRowControl row)
    {
        if (row.Locked) return;
        _rowsCanvas.Children.Remove(row);
        _rows.Remove(row);
        if (_rows.Count == 0) ShowEmptyState();
        LayoutRows();
        ReloadProjectPreviewIfOpen();
        UpdatePreviewButtonState();
    }

    private void LayoutRows(bool animate = true)
    {
        double y = 0;
        for (int i = 0; i < _rows.Count; i++)
        {
            _rows[i].SetIndex(i + 1);
            _rows[i].TargetY = y;
            if (!animate && _rows[i] != _dragRow)
            {
                _rows[i].CurY = _rows[i].TargetY;
                Canvas.SetTop(_rows[i], _rows[i].CurY);
            }
            // RowSlotHeight already includes the gap below each card (either
            // as part of the fixed RowH step, or as the wrapped row's own
            // bottom margin), so summing it alone gives the extent plus a
            // trailing gap - no extra padding term.
            y += RowSlotHeight(_rows[i]);
        }
        _rowsCanvas.Height = Math.Max(y, 1);

        if (animate) StartAnimation();
    }

    private void StartAnimation()
    {
        if (_animRunning) return;
        _animRunning = true;
        CompositionTarget.Rendering += OnAnimateFrame;
    }

    /// <summary>Eases every row toward its target position each rendered
    /// frame - port of app.py's _animate (there driven by a fixed 16ms
    /// root.after() timer; CompositionTarget.Rendering ties it to the
    /// actual display refresh instead).</summary>
    private void OnAnimateFrame(object? sender, EventArgs e)
    {
        bool moving = false;
        foreach (var row in _rows)
        {
            if (row == _dragRow) continue;
            double delta = row.TargetY - row.CurY;
            if (Math.Abs(delta) > 0.5)
            {
                row.CurY += delta * 0.28;
                moving = true;
            }
            else
            {
                row.CurY = row.TargetY;
            }
            Canvas.SetTop(row, row.CurY);
        }
        if (!moving)
        {
            CompositionTarget.Rendering -= OnAnimateFrame;
            _animRunning = false;
        }
    }

    // -- drag & drop reordering -----------------------------------------------

    private void StartDrag(FileRowControl row, MouseEventArgs e)
    {
        if (row.Locked) return;
        _dragRow = row;
        _dragOffset = e.GetPosition(_rowsCanvas).Y - Canvas.GetTop(row);
        row.SetDragging(true);
        Canvas.SetZIndex(row, 1000);
    }

    private void DragMotion(FileRowControl row, MouseEventArgs e)
    {
        if (_dragRow != row) return;

        // Every OTHER row's slot height, in current list order - the
        // dragged row itself is excluded because it is the thing being
        // repositioned among them, not one of the slots it is being
        // compared against. Under the fixed RowH step (the non-wrapped
        // case) these are all equal, so everything below reduces to the
        // original round(y / RowH) math; RowSlotHeight is what makes it also
        // correct when rows have grown to different heights.
        var others = _rows.Where(r => r != row).ToList();
        double totalOthersHeight = others.Sum(RowSlotHeight);

        double y = e.GetPosition(_rowsCanvas).Y - _dragOffset;
        y = Math.Max(0, Math.Min(y, Math.Max(0, totalOthersHeight)));
        row.CurY = y;
        Canvas.SetTop(row, y);

        // The candidate insertion points are the cumulative top offset the
        // dragged row would land at for each possible index among the
        // others (0, after the first, after the second, ... after the
        // last) - pick whichever one the row's actual top is closest to.
        int newIdx = 0;
        double best = double.MaxValue;
        double boundary = 0;
        for (int i = 0; i <= others.Count; i++)
        {
            double d = Math.Abs(boundary - y);
            if (d < best) { best = d; newIdx = i; }
            if (i < others.Count) boundary += RowSlotHeight(others[i]);
        }

        int curIdx = _rows.IndexOf(row);
        if (newIdx != curIdx)
        {
            _rows.RemoveAt(curIdx);
            _rows.Insert(newIdx, row);
            LayoutRows();
        }
    }

    private void EndDrag(FileRowControl row)
    {
        if (_dragRow != row) return;
        row.SetDragging(false);
        Canvas.SetZIndex(row, 0);
        _dragRow = null;
        LayoutRows();
        ReloadProjectPreviewIfOpen();
    }

    // -- log ---------------------------------------------------------------

    /// <summary>Cap on retained console blocks - see N7 in AUDIT_TODO.md.</summary>
    private const int MaxLogBlocks = 2000;

    private void Log(string text, string? tag = null)
    {
        Dispatcher.BeginInvoke(() => AppendLogLine(text, tag));
    }

    private void AppendLogLine(string text, string? tag)
    {
        var brush = tag switch
        {
            "dim" => Theme.LogDimBrush,
            "accent" => Theme.FgAccentBrush,
            "warn" => Theme.FgAccentBrush,
            _ => Theme.LogFgBrush,
        };
        var paragraph = new Paragraph(new Run(text))
        {
            Margin = new Thickness(0), Foreground = brush,
            FontFamily = Theme.MonoFontFamily, FontSize = Theme.MonoFontSize,
        };
        _logBox.Document.Blocks.Add(paragraph);
        // Unbounded growth here (see N7 in AUDIT_TODO.md) meant a long job
        // with verbose logging on - every ffmpeg argv plus its stderr -
        // could pile up tens of thousands of Paragraphs, ballooning memory
        // and making FlowDocument layout progressively slower, plus turning
        // CopyLogToClipboard's TextRange into one enormous string. Trimming
        // from the oldest end keeps only the most recent MaxLogBlocks lines,
        // which is what a user scrolling a long-running console actually
        // cares about.
        while (_logBox.Document.Blocks.Count > MaxLogBlocks)
            _logBox.Document.Blocks.Remove(_logBox.Document.Blocks.FirstBlock);
        if (_consoleOpen)
        {
            _logBox.ScrollToEnd();
        }
        else
        {
            _unreadLogLines++;
            UpdateConsoleBadge();
        }
    }

    private void ClearLog()
    {
        _logBox.Document.Blocks.Clear();
        _unreadLogLines = 0;
        UpdateConsoleBadge();
    }

    /// <summary>Copies the whole console transcript as plain text, so Bartek
    /// can paste an error straight into a message instead of retyping or
    /// screenshotting it. <see cref="Clipboard.SetText(string)"/> can throw
    /// if another app is holding the clipboard - failing silently here beats
    /// popping a dialog over what's usually a one-off race. Swaps the button's
    /// icon to a checkmark for a moment as the only feedback, since a copy
    /// that succeeds has nothing else worth telling the user.</summary>
    private async void CopyLogToClipboard()
    {
        var text = new TextRange(_logBox.Document.ContentStart, _logBox.Document.ContentEnd).Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); }
        catch { return; }

        var original = _copyLogButton.Content;
        _copyLogButton.Content = Icons.Stroked(Icons.Check, Theme.FgAccentBrush);
        await Task.Delay(900);
        _copyLogButton.Content = original;
    }

    private void SetProgress(double v) => Dispatcher.BeginInvoke(() =>
    {
        _progress.Value = v;
        // Truncated, not rounded, for the same reason the bar's own label
        // used to be: int() always reports "at least this much is done".
        _totalPercent.Text = $"{(int)_progress.Value}% {Loc.T("OfTotalLabel")}";
        // The mini bar mirrors the overall bar, not the step bar - it is
        // the number that matters at a glance. Percentage only, no "of
        // total" suffix: there is no second bar next to it to disambiguate
        // from, so the qualifier would just be noise.
        _miniProgress.Value = v;
        _miniPercent.Text = $"{(int)_progress.Value}%";
    });

    private void SetStepProgress(double v) => Dispatcher.BeginInvoke(() =>
    {
        _stepProgress.Value = v;
        _stepPercent.Text = $"{(int)_stepProgress.Value}%";
    });

    /// <summary>Names the file the progress card is currently reporting on.
    /// Called from the worker thread, so it marshals like the rest.</summary>
    private void SetCurrentFile(string name) =>
        Dispatcher.BeginInvoke(() => _progressFileName.Text = name);

    private void SetDimmed(bool on)
    {
        _dimOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _contentRoot.Effect = on ? new BlurEffect { Radius = 16 } : null;
    }

    // -- validation and processing start ---------------------------------------

    private List<ParsedFile>? CollectAndValidate()
    {
        if (_rows.Count == 0)
        {
            ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Warning, Loc.T("NoFilesTitle"), Loc.T("NoFilesAddOneMessage"), Loc.T("OK"));
            return null;
        }

        var parsed = new List<ParsedFile>();
        foreach (var row in _rows)
        {
            var text = row.RangesText.Trim();
            var fname = Path.GetFileName(row.FilePath);
            if (text.Length == 0)
            {
                ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Warning, Loc.T("MissingRangesTitle"), string.Format(Loc.T("MissingRangesMessage"), fname), Loc.T("OK"));
                return null;
            }
            var ranges = new List<(double, double)>();
            foreach (var chunkRaw in text.Split(','))
            {
                var chunk = chunkRaw.Trim();
                if (chunk.Length == 0) continue;
                int dash = chunk.IndexOf('-');
                if (dash < 0)
                {
                    ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Error, Loc.T("InvalidFormatTitle"), string.Format(Loc.T("InvalidFormatMessage"), chunk, fname), Loc.T("OK"));
                    return null;
                }
                double start, end;
                try
                {
                    start = TimeUtils.ParseTime(chunk[..dash]);
                    end = TimeUtils.ParseTime(chunk[(dash + 1)..]);
                }
                catch (FormatException ex)
                {
                    ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Error, Loc.T("InvalidTimeFormatTitle"), ex.Message, Loc.T("OK"));
                    return null;
                }
                if (end <= start)
                {
                    ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Error, Loc.T("InvalidRangeTitle"), string.Format(Loc.T("InvalidRangeMessage"), chunk), Loc.T("OK"));
                    return null;
                }
                ranges.Add((start, end));
            }
            if (ranges.Count == 0)
            {
                ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Warning, Loc.T("MissingRangesTitle"), string.Format(Loc.T("MissingRangesAtLeastOneMessage"), fname), Loc.T("OK"));
                return null;
            }
            // Two overlapping ranges of the same file would silently
            // duplicate their shared footage in the output with no warning -
            // see N17 in AUDIT_TODO.md. O(n^2) is fine: a row's range count
            // is a handful of user-typed chips, never anywhere near enough
            // for this to matter.
            for (int i = 0; i < ranges.Count; i++)
            {
                for (int j = i + 1; j < ranges.Count; j++)
                {
                    var (s1, e1) = ranges[i];
                    var (s2, e2) = ranges[j];
                    if (s1 < e2 && s2 < e1)
                    {
                        string r1 = $"{TimeUtils.FormatTime(s1)}-{TimeUtils.FormatTime(e1)}";
                        string r2 = $"{TimeUtils.FormatTime(s2)}-{TimeUtils.FormatTime(e2)}";
                        ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Error, Loc.T("OverlappingRangesTitle"),
                            string.Format(Loc.T("OverlappingRangesMessage"), r1, r2, fname), Loc.T("OK"));
                        return null;
                    }
                }
            }
            parsed.Add(new ParsedFile(row.FilePath, ranges));
        }
        return parsed;
    }

    /// <summary>Shared output-file picker for both START and "Add to
    /// queue" - MKV is offered alongside MP4 so subtitle passthrough has
    /// somewhere to land; picking MP4 always drops subtitles.</summary>
    private string? AskOutputPath(IEnumerable<string> inputPaths)
    {
        // Re-shown on a source/target collision instead of just refusing
        // once - see N18 in AUDIT_TODO.md. The pieces cut from a source are
        // already sitting in a temp folder by the time ffmpeg would write
        // the final file, so overwriting the source wouldn't corrupt the
        // job's own output - it would just silently destroy the input with
        // no way back, which is worth blocking outright rather than merely
        // warning after the fact.
        var inputFullPaths = new HashSet<string>(
            inputPaths.Select(p => { try { return Path.GetFullPath(p); } catch { return p; } }),
            StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("SaveOutputAsTitle"),
                Filter = "MP4|*.mp4|MKV (keeps subtitles)|*.mkv",
            };
            ApplyInitialDir(dlg, _config, "last_output_dir");
            if (dlg.ShowDialog(this) != true) return null;
            var fileName = dlg.FileName;
            if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
            {
                // DefaultExt is intentionally left unset (see below); without it,
                // SaveFileDialog can still hand back a bare name with no
                // extension at all, so fall back to the filter the user picked.
                fileName += dlg.FilterIndex == 2 ? ".mkv" : ".mp4";
            }

            string fullOutput;
            try { fullOutput = Path.GetFullPath(fileName); }
            catch { fullOutput = fileName; }
            if (inputFullPaths.Contains(fullOutput))
            {
                ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Error, Loc.T("OutputMatchesInputTitle"),
                    string.Format(Loc.T("OutputMatchesInputMessage"), Path.GetFileName(fileName)), Loc.T("OK"));
                continue;
            }

            RememberDir("last_output_dir", fileName);
            return fileName;
        }
    }

    private void UpdateQueueLabel()
    {
        _queueLabel.Text = _jobQueue.Count > 0 ? string.Format(Loc.T("QueuedLabel"), _jobQueue.Count) : "";
    }

    private void OnAddToQueueClicked(object sender, RoutedEventArgs e)
    {
        if (_processing) return;
        var parsed = CollectAndValidate();
        if (parsed is null) return;
        var output = AskOutputPath(parsed.Select(p => p.FilePath));
        if (output is null) return;
        _jobQueue.Add(new QueuedJob(parsed, output));
        ClearAllRows();
        UpdateQueueLabel();
    }

    private void OnClearQueueClicked(object sender, RoutedEventArgs e)
    {
        if (_processing || _jobQueue.Count == 0) return;
        bool confirmed = ConfirmDialogs.AskYesNo(this, SetDimmed, DialogKind.Question, Loc.T("ClearQueueTitle"),
            string.Format(Loc.T("ClearQueueMessage"), _jobQueue.Count), Loc.T("Yes"), Loc.T("No"));
        if (confirmed)
        {
            _jobQueue.Clear();
            UpdateQueueLabel();
        }
    }

    /// <summary>Runs every queued job, plus - if the row list isn't empty -
    /// one more job built from the current rows. With an empty queue this
    /// is exactly the single-job behavior from before queuing existed.</summary>
    private async void OnStartClicked(object sender, RoutedEventArgs e)
    {
        var jobs = new List<QueuedJob>(_jobQueue);
        // How many of `jobs` came from the explicit queue, as opposed to the
        // one job (if any) built below from the current row list - see the
        // restore logic in the catch blocks: only the queued ones make sense
        // to put back, since a rows-based job's "source" is the still-intact
        // row list, not a queue slot.
        int queuedJobsCount = jobs.Count;
        if (_rows.Count > 0)
        {
            var parsed = CollectAndValidate();
            if (parsed is null) return;
            var output = AskOutputPath(parsed.Select(p => p.FilePath));
            if (output is null) return;
            jobs.Add(new QueuedJob(parsed, output));
        }
        else if (jobs.Count == 0)
        {
            ConfirmDialogs.ShowMessage(this, SetDimmed, DialogKind.Warning, Loc.T("NothingToDoTitle"), Loc.T("NothingToDoMessage"), Loc.T("OK"));
            return;
        }

        // Left in place (not cleared) until the run actually succeeds - see
        // N5 in AUDIT_TODO.md. Clearing here, before anything has run, is
        // what lost the definitions of not-yet-run queued jobs whenever an
        // earlier job in the same batch failed: rows were already gone
        // (OnAddToQueueClicked clears them when queuing), and the queue
        // itself had just been wiped, so there was nowhere left to recover
        // the failed run's remaining jobs from.
        UpdateQueueLabel();

        _cts = new CancellationTokenSource();
        _processing = true;
        SetProcessingUiState(true);
        _progress.Value = 0;
        _stepProgress.Value = 0;
        _miniProgress.Value = 0;
        ClearLog();

        try
        {
            var outputs = await Task.Run(() => ProcessAllJobs(jobs));
            _jobQueue.Clear();
            UpdateQueueLabel();
            OnProcessingFinished();
            ConfirmDialogs.ShowDone(this, SetDimmed, outputs,
                Loc.T("DoneTitle"), Loc.T("OutputFileSaved"), Loc.T("OutputFilesSaved"),
                Loc.T("OK"));
        }
        catch (OperationCanceledException)
        {
            RequeueUnfinishedJobs(jobs, queuedJobsCount);
            OnProcessingFinished();
            Log("Stopped by user.", "dim");
        }
        catch (Exception ex)
        {
            RequeueUnfinishedJobs(jobs, queuedJobsCount);
            Log("ERROR: " + ex.Message);
            Log(ex.ToString(), "dim");
            OnProcessingFinished();
            ConfirmDialogs.ShowError(this, SetDimmed, Loc.T("ErrorTitle"), ex.Message, Loc.T("OK"));
        }
    }

    /// <summary>Puts back into the queue whichever originally-queued jobs
    /// (the first <paramref name="queuedJobsCount"/> entries of
    /// <paramref name="jobs"/>) never got to run, per _completedJobCount as
    /// left by ProcessAllJobs. See N5 in AUDIT_TODO.md.</summary>
    private void RequeueUnfinishedJobs(List<QueuedJob> jobs, int queuedJobsCount)
    {
        _jobQueue.Clear();
        for (int i = _completedJobCount; i < queuedJobsCount; i++)
            _jobQueue.Add(jobs[i]);
        UpdateQueueLabel();
    }

    private void SetProcessingUiState(bool processing)
    {
        UiHelpers.SetButtonEnabled(_startButton, !processing);
        UiHelpers.SetButtonEnabled(_stopButton, processing);
        _stopRequested = false;
        UiHelpers.SetButtonText(_stopButton, Loc.T("BtnStop"));
        UiHelpers.SetButtonEnabled(_addButton, !processing);
        UiHelpers.SetButtonEnabled(_saveProjectButton, !processing);
        UiHelpers.SetButtonEnabled(_loadProjectButton, !processing);
        UiHelpers.SetButtonEnabled(_queueButton, !processing);
        UiHelpers.SetButtonEnabled(_clearQueueButton, !processing);
        UpdatePreviewButtonState();
        // The preview is NO LONGER closed when a job starts. It used to be,
        // because the stacked layout had it borrowing the console's space -
        // and the console is exactly what the user wants back at that
        // moment. In two columns they are side by side and neither has to
        // give way, so a running job leaves the panel alone; releasing the
        // decoder is still handled by the panel itself when it is hidden.
        //
        // Locked rather than merely released, because the panel STAYS
        // on-screen and clickable for the whole job: a bare release closed
        // the decoder but left every transport button live, so one press of
        // play re-opened a file ffmpeg was reading - and left it playing
        // with no way to stop it, since the release guard also skipped the
        // pause that press should have paired with. Unlocking reloads the
        // panel, which is what puts it back in a working state after the
        // job; before this, the half-released state survived the job and
        // stayed until the reload chip or a structural change cleared it.
        _projectPreview.SetLocked(processing);
        if (!processing) ReloadProjectPreviewIfOpen();
        SetRowsEnabled(!processing);
    }

    /// <summary>Requests an immediate stop. The worker notices at the next
    /// tick (at most ~150ms) and kills any running ffmpeg process.</summary>
    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        if (!_processing) return;
        // Paused for the whole time the confirmation is up - it's confusing
        // for the job to keep visibly grinding away while the user is being
        // asked whether to stop it. Always resumed afterwards, whichever
        // button is clicked: on Cancel the job just resumes where it left
        // off, on STOP the resumed loop immediately notices cancellation
        // via its own next tick() and unwinds normally.
        _pauseController.Pause();
        bool confirmed;
        try
        {
            confirmed = ConfirmDialogs.AskStopConfirm(this, SetDimmed, Loc.T("StopProcessingTitle"),
                Loc.T("StopProcessingMessage"), Loc.T("BtnStop"), Loc.T("Cancel"));
        }
        finally
        {
            _pauseController.Resume();
        }
        if (!confirmed) return;
        _cts?.Cancel();
        UiHelpers.SetButtonEnabled(_stopButton, false);
        _stopRequested = true;
        UiHelpers.SetButtonText(_stopButton, Loc.T("BtnStopping"));
        Log("Stop requested by user…", "dim");
    }

    /// <summary>Common cleanup after done / error / cancelled, restoring the
    /// UI to its idle, editable state.</summary>
    private void OnProcessingFinished()
    {
        _processing = false;
        SetProcessingUiState(false);
        _progress.Value = 0;
        _stepProgress.Value = 0;
        _miniProgress.Value = 0;
        _jobStats = null;
        UpdateStatsDisplay(null);
        // OnProcessingFinished only ever runs after the Task.Run in
        // OnStartClicked has been awaited (success, cancellation, or error),
        // so ProcessAllJobs is no longer touching _cts.Token here - safe to
        // dispose the run's CancellationTokenSource now instead of just
        // overwriting the reference on the next run and leaking its handle
        // (see N16 in AUDIT_TODO.md).
        _cts?.Dispose();
        _cts = null;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_processing) return;
        if (_closing) { e.Cancel = true; return; }
        bool confirmed = ConfirmDialogs.AskYesNo(this, SetDimmed, DialogKind.Question, Loc.T("StopAndQuitTitle"),
            Loc.T("StopAndQuitMessage"), Loc.T("Yes"), Loc.T("No"));
        if (!confirmed) { e.Cancel = true; return; }

        // Closing outright here would let the worker task get cut off
        // mid-cleanup (ffmpeg.exe left running, temp folder never removed),
        // so cancel it and wait for OnStartClicked's await-chain to observe
        // that before actually destroying the window.
        e.Cancel = true;
        _closing = true;
        _cts?.Cancel();
        UiHelpers.SetButtonEnabled(_stopButton, false);
        _stopRequested = true;
        UiHelpers.SetButtonText(_stopButton, Loc.T("BtnStopping"));
        Log("Stop requested by user (closing window)…", "dim");
        WaitForWorkerThenClose();
    }

    // True for the whole lifetime of a ConfirmDialogs modal (SetDimmed(true)
    // right before ShowDialog(), SetDimmed(false) right after - see
    // ModalDialogWindow.RunModal). Used below to close N9's race: if the job
    // finishes right as the user confirms "stop and quit", OnProcessingFinished
    // sets _processing = false BEFORE the completion dialog (ShowDone/
    // ShowError) is shown, and ShowDialog() pumps a NESTED message loop while
    // it's up - which is exactly when this 100ms poll timer can still fire.
    // Without this check it saw _processing already false and closed the
    // window out from under that still-open modal.
    private bool IsCompletionDialogOpen => _dimOverlay.Visibility == Visibility.Visible;

    private void WaitForWorkerThenClose()
    {
        if (!_processing) { Close(); return; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            if (_processing || IsCompletionDialogOpen) return;
            timer.Stop();
            Close();
        };
        timer.Start();
    }

    private Action MakeTicker(double startPct, double endPct)
    {
        int n = 0;
        return () =>
        {
            _cts!.Token.ThrowIfCancellationRequested();
            n++;
            double frac = 1 - 1.0 / (1 + n * 0.6);
            frac = Math.Min(frac, 0.99);
            double pct = startPct + (endPct - startPct) * frac;
            SetProgress(pct);
            SetStepProgress(frac * 100);
            // Snapshot() takes no progress argument any more: ETA is built
            // from real step-completion timing (ReportStepCompleted), not
            // from this method's own cosmetic easing curve - see
            // JobStatsTracker.ReportStepCompleted for why that distinction
            // matters (it's what was making the old ETA freeze/count up).
            if (_jobStats is { } stats) SetStats(stats.Snapshot());
        };
    }

    /// <summary>Runs every job in <paramref name="jobs"/> one after another
    /// on the background task, stopping the whole run on the first
    /// cancellation/error.</summary>
    /// <summary>Step count for one job's share of the "% of total"/ETA bars:
    /// one analyze step per unique file + one cut step per kept range + one
    /// join - the same formula ProcessOne uses locally, computed here up
    /// front so the whole queue's steps can be counted before any of them
    /// run (see the grandTotalSteps use in ProcessAllJobs below).</summary>
    private static int CountJobSteps(List<ParsedFile> parsed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int uniqueCount = 0;
        foreach (var pf in parsed)
            if (seen.Add(pf.FilePath)) uniqueCount++;
        int totalSegments = parsed.Sum(pf => pf.Ranges.Count);
        return uniqueCount + totalSegments + 1;
    }

    private List<string> ProcessAllJobs(List<QueuedJob> jobs)
    {
        var outputs = new List<string>();
        int totalJobs = jobs.Count;
        // Computed for the whole queue up front so the green "% of total" bar
        // advances once, 0->100, across every queued job instead of cycling
        // 0->100 once per job while its label still says "of total".
        var stepCounts = jobs.Select(j => CountJobSteps(j.Parsed)).ToList();
        int grandTotalSteps = stepCounts.Sum();
        int stepOffset = 0;
        _completedJobCount = 0;
        for (int i = 0; i < totalJobs; i++)
        {
            string prefix = totalJobs > 1 ? $"[Job {i + 1}/{totalJobs}] " : "";
            ProcessOne(jobs[i].Parsed, jobs[i].Output, prefix, stepOffset, grandTotalSteps);
            stepOffset += stepCounts[i];
            outputs.Add(jobs[i].Output);
            _completedJobCount = i + 1;
        }
        return outputs;
    }

    private void ProcessOne(List<ParsedFile> parsed, string output, string jobPrefix, int globalStepOffset, int grandTotalSteps)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(), $"clipglue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            // OrdinalIgnoreCase: Windows paths are case-insensitive, and the
            // per-path lookups below (keyframesByPath/probeByPath/etc.) are
            // keyed off whichever casing happened to appear first - without
            // this, the same file referenced with two different casings
            // would be probed and cut twice instead of once.
            var uniquePaths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pf in parsed)
                if (seen.Add(pf.FilePath)) uniquePaths.Add(pf.FilePath);

            int totalSegments = parsed.Sum(pf => pf.Ranges.Count);
            if (totalSegments == 0) throw new InvalidOperationException("Nothing to process.");

            // One step per analyzed file + one per kept range (cut) + one
            // join. Each step is a full, independent 0-100% cycle of the
            // current-step (blue) bar.
            int totalSteps = uniquePaths.Count + totalSegments + 1;
            // The green "% of total"/ETA bars are scaled against the WHOLE
            // queue's steps (grandTotalSteps), not just this job's - see N6
            // in AUDIT_TODO.md. stepIdx starts at this job's offset into
            // that grand total, so the bar keeps climbing across jobs
            // instead of resetting to 0 at the start of each one.
            double stepPct = 100.0 / grandTotalSteps;
            int stepIdx = globalStepOffset;
            (double S, double E) NextRange()
            {
                double s = stepIdx * stepPct;
                double e = (stepIdx + 1) * stepPct;
                stepIdx++;
                return (s, e);
            }

            // -- analyze --------------------------------------------------
            Log(jobPrefix + "Analyzing source video...");
            var t0 = DateTime.UtcNow;
            // Fresh per job (not per queued run): throughput/CPU/memory
            // reset to zero between files, so there's no single meaningful
            // "speed" spanning unrelated ffmpeg invocations on different
            // inputs. Created before probing starts so MakeTicker's ticks
            // during the analyze step already have a tracker to read from
            // (it just won't have any samples yet - ffprobe calls don't
            // feed it, only RunFfmpeg does). Its ETA plan (SetTotalWork)
            // isn't known yet at this point - see the call further below,
            // right after every file's keyframes/compatibility are known.
            _jobStats = new JobStatsTracker();

            var fileProbes = new List<ProbeResult>();
            var keyframesByPath = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            for (int idx = 0; idx < uniquePaths.Count; idx++)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                var filePath = uniquePaths[idx];
                if (uniquePaths.Count > 1)
                    Log($"  {idx + 1} of {uniquePaths.Count}", "dim");
                SetStepProgress(0);
                var (s, e) = NextRange();
                var fileTick = MakeTicker(s, e);
                var fp = FfmpegEngine.ProbeFull(filePath, tick: fileTick);
                fileProbes.Add(fp);
                keyframesByPath[filePath] = FfmpegEngine.ProbeKeyframeTimes(filePath, tick: fileTick);
                SetProgress(e);
                SetStepProgress(100);
                _jobStats?.ReportStepCompleted(fp.Duration * FfmpegEngine.ProbeSecondsPerContentSecond);
            }

            var target = FfmpegEngine.DecideTarget(fileProbes);
            // Subtitle passthrough only ever happens into an .mkv output.
            bool subtitlesKept = target.Subtitle is not null
                && string.Equals(Path.GetExtension(output), ".mkv", StringComparison.OrdinalIgnoreCase);
            if (!subtitlesKept) target = target with { Subtitle = null };

            var tv = target.Video;
            var ta = target.Audio;
            Log($"  target: {tv.Codec} video, {tv.Width}x{tv.Height} @ {tv.Fps.ToString("F2", CultureInfo.InvariantCulture)}fps, " +
                $"{(ta is not null ? ta.Codec + " audio" : "no audio")}", "dim");
            Log(subtitlesKept
                ? $"  subtitles: {target.Subtitle!.Codec} - will be kept"
                : "  subtitles: not carried over (missing, mismatched, an unsupported format across the batch, or output isn't .mkv)",
                "dim");

            var probeByPath = fileProbes.ToDictionary(fp => fp.Path, StringComparer.OrdinalIgnoreCase);
            var videoOkByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var audioOkByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in uniquePaths)
            {
                var fp = probeByPath[filePath];
                bool vOk = FfmpegEngine.IsVideoCompatible(fp, target);
                bool aOk = FfmpegEngine.IsAudioCompatible(fp, target);
                videoOkByPath[filePath] = vOk;
                audioOkByPath[filePath] = aOk;
                Log($"  {Path.GetFileName(filePath)}: video {(vOk ? "matches target - will be copied" : "needs re-encoding")}, " +
                    $"audio {(aOk ? "matches target - will be copied" : "needs re-encoding")}", "dim");
            }
            Log($"  done in {(DateTime.UtcNow - t0).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s", "dim");

            // Now that every file's keyframes and video/audio compatibility
            // are known, the ETA plan for the rest of the job (every cut
            // plus the final join) can be built for real, instead of
            // guessing that every remaining step costs the same - see
            // JobStatsTracker.SetTotalWork.
            double plannedWork = parsed
                .SelectMany(pf => pf.Ranges.Select(r => (pf.FilePath, r.Start, r.End)))
                .Sum(x => FfmpegEngine.EstimateCutSeconds(
                    x.Start, x.End, keyframesByPath[x.FilePath], videoOkByPath[x.FilePath], audioOkByPath[x.FilePath]));
            double totalKeptDuration = parsed.Sum(pf => pf.Ranges.Sum(r => r.End - r.Start));
            plannedWork += totalKeptDuration * FfmpegEngine.CopyBothSecondsPerContentSecond;
            _jobStats?.SetTotalWork(plannedWork);

            // Every kept range must actually fit inside its file.
            foreach (var pf in parsed)
            {
                double duration = probeByPath[pf.FilePath].Duration;
                foreach (var (start, end) in pf.Ranges)
                {
                    if (end > duration + 0.1)
                        throw new InvalidOperationException(
                            $"Range {TimeUtils.FormatTime(start)}-{TimeUtils.FormatTime(end)} in {Path.GetFileName(pf.FilePath)} " +
                            $"exceeds the file's length ({TimeUtils.FormatTime(duration)})");
                }
            }

            // -- cut --------------------------------------------------------
            var segmentPaths = new List<string>();
            int segNum = 0;
            foreach (var pf in parsed)
            {
                foreach (var (start, end) in pf.Ranges)
                {
                    _cts!.Token.ThrowIfCancellationRequested();
                    segNum++;
                    string desc = $"Cutting segment {segNum}/{totalSegments}: {Path.GetFileName(pf.FilePath)}";
                    SetCurrentFile(Path.GetFileName(pf.FilePath));
                    Log(jobPrefix + desc + $"  [{TimeUtils.FormatTime(start)}-{TimeUtils.FormatTime(end)}]", "accent");
                    SetStepProgress(0);
                    var (s, e) = NextRange();
                    var t1 = DateTime.UtcNow;
                    double segWeight = FfmpegEngine.EstimateCutSeconds(
                        start, end, keyframesByPath[pf.FilePath], videoOkByPath[pf.FilePath], audioOkByPath[pf.FilePath]);
                    var pieces = FfmpegEngine.CutSegmentSmart(
                        pf.FilePath, start, end, target, keyframesByPath[pf.FilePath],
                        videoOkByPath[pf.FilePath], audioOkByPath[pf.FilePath],
                        tmpDir, $"seg_{segNum - 1:D3}", Log, MakeTicker(s, e), _pauseController, _jobStats);
                    SetProgress(e);
                    SetStepProgress(100);
                    _jobStats?.ReportStepCompleted(segWeight);
                    Log($"  done in {(DateTime.UtcNow - t1).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s", "dim");
                    segmentPaths.AddRange(pieces);
                }
            }

            // -- join -------------------------------------------------------------
            _cts!.Token.ThrowIfCancellationRequested();
            string joinDesc = $"Joining {segmentPaths.Count} piece(s) into the final file";
            Log(jobPrefix + joinDesc + "...", "accent");
            SetStepProgress(0);
            var (js, je) = NextRange();
            var t2 = DateTime.UtcNow;
            FfmpegEngine.ConcatSegments(segmentPaths, output, Log, MakeTicker(js, je), hasSubtitle: subtitlesKept, pause: _pauseController, stats: _jobStats);
            // je, not a hardcoded 100: this job's last step ends at je within
            // the whole queue's scale (see N6 fix above) - only the actual
            // last job's last step lands on 100.
            SetProgress(je);
            SetStepProgress(100);
            _jobStats?.ReportStepCompleted(totalKeptDuration * FfmpegEngine.CopyBothSecondsPerContentSecond);
            Log($"  done in {(DateTime.UtcNow - t2).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s", "dim");

            // Sanity check: the known concat-demuxer seam artifact is at
            // most ~1 frame per seam, so a bigger drift than that is worth
            // flagging - not fatal, since the file is still there.
            var finalProbe = FfmpegEngine.ProbeFull(output, pad: false);
            double expected = parsed.SelectMany(pf => pf.Ranges).Sum(r => r.End - r.Start);
            double tolerance = 0.5 + 0.05 * segmentPaths.Count;
            double diff = Math.Abs(finalProbe.Duration - expected);
            if (diff > tolerance)
            {
                Log(jobPrefix + $"  Warning: output length ({TimeUtils.FormatTime(finalProbe.Duration)}) differs from the " +
                    $"requested total ({TimeUtils.FormatTime(expected)}) by {diff.ToString("F2", CultureInfo.InvariantCulture)}s", "warn");
            }

            Log(jobPrefix + "Finished: " + output, "accent");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
