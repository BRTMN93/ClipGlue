using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ClipGlue.Models;
using ShapePath = System.Windows.Shapes.Path;

namespace ClipGlue.Controls;

/// <summary>
/// A single file row, drawn as a rounded card over TWO lines, per the
/// approved mockup. The first line carries the row identity - drag handle,
/// numbered badge, file name, the file's own duration, then edit, preview
/// and delete. The second carries the range chips, indented to start under
/// the name.
///
/// That order is the second approved layout for this row: the earlier one
/// (a port of widgets.py's FileRow) led with delete and put preview between
/// the name and the entry. Delete moved to the far end - away from the
/// handle you grab to reorder - and preview moved next to it, so the two
/// buttons sit together on the right and the eye runs handle → number →
/// name → ranges without a control interrupting it. The word "File N:"
/// collapsed into the badge to buy that width back.
///
/// Still built imperatively in code (no XAML), the same way the Tkinter
/// original was.
/// </summary>
public sealed class FileRowControl : UserControl
{
    /// <summary>Square hit area around each 16x16 row icon. The icons
    /// themselves are small, so they get a generous padded target instead of
    /// only being clickable on the glyph strokes (a TextBlock with no
    /// background, as in the pre-restyle row, was hit-testable on the drawn
    /// pixels alone).</summary>
    private const double IconHit = 28;

    public string FilePath { get; }
    public bool Locked { get; private set; }
    private bool _wrapMode;

    /// <summary>Current animated Canvas.Top position, owned by the parent
    /// row-list manager (MainWindow), not this control.</summary>
    public double CurY;
    /// <summary>Target Canvas.Top position the animation eases toward.</summary>
    public double TargetY;

    private readonly Border _root;
    private readonly Border _handle;
    private readonly ShapePath _handleIcon;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly TextBlock _nameLabel;
    private readonly TextBlock _durationLabel;
    private readonly RangeChipList _rangeBox;
    private readonly Border _editBtn;
    private readonly ShapePath _editIcon;
    private readonly Border _previewBtn;
    private readonly ShapePath _previewIcon;
    private readonly Border _deleteBtn;
    private readonly ShapePath _deleteIcon;
    private readonly RowDefinition _line2Row;
    private readonly ScaleTransform _dragScale;
    private readonly DropShadowEffect _dragShadow;
    private bool _isDraggingCandidate;

    public event EventHandler? DeleteRequested;
    /// <summary>Raised when the user edits the ranges here (chip added,
    /// removed, or raw text typed) - the project preview listens so it can
    /// tell a structural change from a keystroke.</summary>
    public event EventHandler? RangesEdited;
    /// <summary>Raised whenever something that can change this row's own
    /// height happens while it is in <see cref="SetWrapMode"/> - a chip
    /// added/removed/edited, or the raw-text/inline-editor view swapped in or
    /// out (each has its own fixed height, different from the chip strip's).
    /// The list owner (MainWindow) uses this to know when it needs to
    /// re-walk the row canvas rather than trusting the row's old measured
    /// height.</summary>
    public event EventHandler? RowHeightMayHaveChanged;
    public event EventHandler? PreviewRequested;
    public event EventHandler<MouseEventArgs>? DragStartRequested;
    public event EventHandler<MouseEventArgs>? DragMoveRequested;
    public event EventHandler? DragEndRequested;

    public FileRowControl(string filePath)
    {
        FilePath = filePath;
        // The control occupies the WHOLE row slot, gap included, not just
        // the visible card: the list canvas positions rows every RowH, so
        // anything the control does not cover is a strip that belongs to no
        // row at all and silently swallows a mouse press. Giving the control
        // the full slot and pushing the gap inside it (as the card's bottom
        // margin) means consecutive rows tile with no seam, and pressing
        // anywhere - card or gap - grabs the row it looks like it belongs to.
        Height = Theme.RowH;
        // Transparent, not null: a null Background is not hit-testable, so
        // the gap would still be dead even though the control now covers it.
        Background = Theme.TransparentBrush;
        // Plain arrow at rest, everywhere on the card - see OnDragMouseDown
        // for why the move cursor is no longer a standing hover hint.
        Cursor = Cursors.Arrow;
        Focusable = false;

        _root = new Border
        {
            Background = Theme.BgRowBrush,
            BorderBrush = Theme.BorderCardBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.CardRadius),
            Padding = new Thickness(Theme.CardPadH - 6, Theme.CardPadV, Theme.CardPadH - 6, Theme.CardPadV),
            Margin = new Thickness(0, 0, 0, Theme.RowPad),
            SnapsToDevicePixels = true,
        };

        // Lift cue for the row being dragged: a subtle scale-DOWN plus a soft
        // drop shadow, both switched on only while dragging (SetDragging) so
        // idle rows never pay for the effect. Shrinking rather than growing
        // is deliberate - a card that grows while dragged near the edge of
        // the list pushes past the window bounds and looks broken; shrinking
        // stays inside them at any drag position while still reading as
        // "lifted" thanks to the shadow. RenderTransformOrigin centers the
        // scale so the card shrinks in place instead of drifting toward its
        // corner.
        _dragScale = new ScaleTransform(1, 1);
        _root.RenderTransform = _dragScale;
        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        _dragShadow = new DropShadowEffect
        {
            Color = Colors.Black,
            Opacity = 0.55,
            BlurRadius = 16,
            ShadowDepth = 5,
            Direction = 270,
        };

        // TWO lines, per the mockup: identity on the first, range chips on
        // the second. The card height is still a constant (the list canvas
        // multiplies by Theme.RowH to place rows), so this is a bigger fixed
        // number rather than a measured one - none of the canvas gotchas
        // (#12/#18/#20) are reopened by it.
        //
        // Row 2 is RowInputH + RowLineGap tall, not just RowInputH: below,
        // _rangeBox (RangeChipList) is Margin.Top = RowLineGap and its own
        // Height is RowInputH, so it needs RowLineGap + RowInputH of cell
        // height to fit without overflowing past this Grid's own bottom edge
        // into _root's Padding - which used to be exactly the case (a
        // RowLineGap-sized overflow, cushioned only by CardPadV) until
        // Bartek flagged the range chips as reading like they were tucked
        // under/overlapped by the card's own edge. Giving row 2 its full
        // margin-inclusive height removes that overflow entirely rather than
        // just enlarging the cushion around it - see Theme.RowCardH for the
        // matching card-height budget.
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.RowLine1H) });
        _line2Row = new RowDefinition { Height = new GridLength(Theme.RowInputH + Theme.RowLineGap) };
        grid.RowDefinitions.Add(_line2Row);

        // The card's 1px elevation highlight, drawn rather than cast: a
        // DropShadowEffect per row measured ~40x the cost of this (see
        // Theme.CardTopHighlightBrush). Inset at both ends because a Border
        // does not clip its children to its own CornerRadius, so a
        // full-width line would poke out of the rounded corners.
        var topHighlight = new Border
        {
            Height = Theme.CardTopHighlightH,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Theme.CardTopHighlightInset, -Theme.CardPadV, Theme.CardTopHighlightInset, 0),
            Background = Theme.CardTopHighlightBrush,
            IsHitTestVisible = false,
        };
        Grid.SetRow(topHighlight, 0);
        grid.Children.Add(topHighlight);

        var line1 = new Grid();
        Grid.SetRow(line1, 0);
        grid.Children.Add(line1);

        // handle | badge | name (fills) | duration | edit | preview | delete
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _handleIcon = Icons.Filled(Icons.Grip, Theme.FgDimBrush);
        _handle = MakeIconSlot(_handleIcon, Cursors.Arrow);
        _handle.Width = 22;
        _handle.Height = Theme.RowLine1H;
        _handle.ToolTip = Loc.T("DragToReorderTooltip");
        Grid.SetColumn(_handle, 0);
        line1.Children.Add(_handle);

        // The serial number, as an outlined chip rather than a "File N:"
        // label. MinWidth (not Width) so a two-digit index still fits
        // without the digits touching the frame.
        // Mono and dark-on-gradient, per the token file's index badge: a
        // gold-filled chip carrying the row number, rather than the outlined
        // one with gold digits the restyle used.
        _badgeText = new TextBlock
        {
            Foreground = Theme.OnAccentBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.UiFontSize - 1,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new Border
        {
            Background = Theme.BadgeGradientBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Theme.BadgeRadius),
            MinWidth = Theme.BadgeSize,
            Height = Theme.BadgeSize,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(4, 0, Theme.CardGap + 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Arrow,
            SnapsToDevicePixels = true,
            Child = _badgeText,
        };
        Grid.SetColumn(_badge, 1);
        line1.Children.Add(_badge);

        // No longer a fixed 170: with the chips on their own line the name
        // has nothing to stay aligned with, so it takes the leftover width
        // and the duration sits at the far end of the line.
        _nameLabel = new TextBlock
        {
            Text = System.IO.Path.GetFileName(filePath),
            Foreground = Theme.FgBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.UiFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, Theme.CardPadH, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Arrow,
            ToolTip = filePath,
        };
        Grid.SetColumn(_nameLabel, 2);
        line1.Children.Add(_nameLabel);

        // The file's own length, filled in from the shared probe cache once
        // it lands. Blank rather than a placeholder while unknown: a dash
        // where a timecode belongs reads as "this file is broken".
        _durationLabel = new TextBlock
        {
            Foreground = Theme.FgFaintBrush,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, Theme.CardGap, 0),
            Cursor = Cursors.Arrow,
        };
        Grid.SetColumn(_durationLabel, 3);
        line1.Children.Add(_durationLabel);

        // Chips, not a raw entry field. The typed path is still here - the
        // pencil on the first line swaps the chips for the same
        // comma-separated field the row used to show - but it is now the
        // opt-in rather than the default. See RangeChipList for why the two
        // share one slot instead of stacking.
        _rangeBox = new RangeChipList
        {
            Margin = new Thickness(Theme.RowChipIndent, Theme.RowLineGap, Theme.CardGap, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _rangeBox.Committed += (_, _) =>
        {
            RangesEdited?.Invoke(this, EventArgs.Empty);
            RowHeightMayHaveChanged?.Invoke(this, EventArgs.Empty);
        };
        _rangeBox.RawModeChanged += (_, _) => RowHeightMayHaveChanged?.Invoke(this, EventArgs.Empty);
        _rangeBox.PickVisuallyRequested += (_, _) => { if (!Locked) PreviewRequested?.Invoke(this, EventArgs.Empty); };
        Grid.SetRow(_rangeBox, 1);
        grid.Children.Add(_rangeBox);

        // The typed path, demoted but not removed: this swaps the chip strip
        // for the same comma-separated field the row used to show. It sits
        // with the other row actions rather than inside the chip strip both
        // because that is where the mockup puts it and because keeping it
        // out of the strip gives the chips their column's full width - which
        // measured as the difference between fitting one chip and two at the
        // default window size.
        _editIcon = Icons.Stroked(Icons.Pencil, Theme.FgFaintBrush, 1.4);
        _editBtn = MakeIconSlot(_editIcon, Cursors.Hand);
        _editBtn.Width = 26;
        _editBtn.ToolTip = Loc.T("ManualEntryTooltip");
        AutomationProperties.SetName(_editBtn, Loc.T("ManualEntryLabel"));
        _editBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!Locked) _rangeBox.ToggleRaw(); };
        _editBtn.MouseEnter += (_, _) =>
        {
            if (Locked) return;
            _editBtn.Background = Theme.IconHoverBgBrush;
            if (!_rangeBox.RawVisible) _editIcon.Stroke = Theme.FgBrush;
        };
        _editBtn.MouseLeave += (_, _) =>
        {
            if (Locked) return;
            _editBtn.Background = Theme.TransparentBrush;
            RefreshEditIcon();
        };
        _rangeBox.RawModeChanged += (_, _) => RefreshEditIcon();
        Grid.SetColumn(_editBtn, 4);
        line1.Children.Add(_editBtn);

        // Preview keeps exactly the colors it always had - link blue at
        // rest, accent on hover - and only gains the frame and the new
        // position at the right-hand end of the card.
        _previewIcon = Icons.Stroked(Icons.Play, Theme.FgLinkBrush);
        _previewBtn = MakeIconSlot(_previewIcon, Cursors.Hand);
        _previewBtn.BorderBrush = Theme.BorderBrush;
        _previewBtn.BorderThickness = new Thickness(1);
        _previewBtn.Margin = new Thickness(0, 0, 6, 0);
        _previewBtn.ToolTip = Loc.T("PickRangesTooltip");
        AutomationProperties.SetName(_previewBtn, Loc.T("PickRangesTooltip"));
        _previewBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!Locked) PreviewRequested?.Invoke(this, EventArgs.Empty); };
        _previewBtn.MouseEnter += (_, _) =>
        {
            if (Locked) return;
            _previewIcon.Stroke = Theme.FgAccentBrush;
            _previewBtn.Background = Theme.IconHoverBgBrush;
        };
        _previewBtn.MouseLeave += (_, _) =>
        {
            if (Locked) return;
            _previewIcon.Stroke = Theme.FgLinkBrush;
            _previewBtn.Background = Theme.TransparentBrush;
        };
        Grid.SetColumn(_previewBtn, 5);
        line1.Children.Add(_previewBtn);

        _deleteIcon = Icons.Stroked(Icons.Close, Theme.RedBrush);
        _deleteBtn = MakeIconSlot(_deleteIcon, Cursors.Hand);
        _deleteBtn.ToolTip = Loc.T("RemoveFileTooltip");
        AutomationProperties.SetName(_deleteBtn, Loc.T("RemoveFileTooltip"));
        _deleteBtn.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!Locked) DeleteRequested?.Invoke(this, EventArgs.Empty); };
        _deleteBtn.MouseEnter += (_, _) =>
        {
            if (Locked) return;
            _deleteIcon.Stroke = Theme.HoverRedBrush;
            _deleteBtn.Background = Theme.IconHoverBgBrush;
        };
        _deleteBtn.MouseLeave += (_, _) =>
        {
            if (Locked) return;
            _deleteIcon.Stroke = Theme.RedBrush;
            _deleteBtn.Background = Theme.TransparentBrush;
        };
        Grid.SetColumn(_deleteBtn, 6);
        line1.Children.Add(_deleteBtn);

        _root.Child = grid;
        Content = _root;

        // ONE set of handlers, on the row control itself, rather than one
        // set per grabbable child. MouseLeftButtonDown bubbles, so a press
        // on the handle, the badge or the name reaches here anyway, while
        // the range entry and the two icon buttons stay inert because they
        // mark the event handled before it gets this far. Handlers on the
        // children as well used to fire the drag twice for a single press
        // (child first, then the card it bubbled to) and had each of them
        // fight over the mouse capture.
        MouseLeftButtonDown += OnDragMouseDown;
        MouseMove += OnDragMouseMove;
        MouseLeftButtonUp += OnDragMouseUp;

        // Tooltips are read once at construction time but the row control
        // outlives a language switch (rows aren't rebuilt when the picker
        // changes), so they need to be re-pulled on Loc.LanguageChanged like
        // MainWindow's own chrome. Unsubscribed on Unloaded so a deleted row
        // doesn't keep itself alive via the static event.
        LoadDurationAsync();

        Loc.LanguageChanged += RefreshTooltips;
        Unloaded += (_, _) => Loc.LanguageChanged -= RefreshTooltips;
    }

    /// <summary>
    /// Fills in the duration shown at the end of the identity line, probing
    /// in the background if the shared cache does not have it yet. Fire and
    /// forget: the row is usable without it, and a file that cannot be
    /// probed simply keeps an empty slot rather than showing a dash where a
    /// timecode belongs.
    /// </summary>
    private async void LoadDurationAsync()
    {
        ShowDuration();
        if (DurationCache.Get(FilePath) > 0) return;
        await DurationCache.RequestAsync(new[] { FilePath }).ConfigureAwait(false);
        // Marshalled explicitly rather than relying on the await capturing a
        // context: there is no SynchronizationContext on the thread until the
        // dispatcher is pumping, so the continuation can land on a pool
        // thread and touching _durationLabel from there throws.
        await Dispatcher.BeginInvoke(ShowDuration);
    }

    private void ShowDuration()
    {
        double seconds = DurationCache.Get(FilePath);
        _durationLabel.Text = seconds > 0 ? TimeUtils.FormatTime(seconds) : "";
    }

    /// <summary>Gold while the raw text field is open, faint otherwise - the
    /// pencil is a toggle, so it has to show which of its two states the row
    /// is currently in.</summary>
    private void RefreshEditIcon()
    {
        _editIcon.Stroke = Locked ? Theme.DisabledFgBrush
            : _rangeBox.RawVisible ? Theme.FgAccentBrush
            : Theme.FgFaintBrush;
    }

    private void RefreshTooltips()
    {
        _editBtn.ToolTip = Loc.T("ManualEntryTooltip");
        AutomationProperties.SetName(_editBtn, Loc.T("ManualEntryLabel"));
        _handle.ToolTip = Loc.T("DragToReorderTooltip");
        _previewBtn.ToolTip = Loc.T("PickRangesTooltip");
        // AutomationProperties.SetName also needs a language-change refresh
        // here, not just the tooltip - see N12 in AUDIT_TODO.md. It was
        // previously set once, in English, at construction time only, so a
        // screen reader or this repo's own UI Automation-driven tests kept
        // reading these two buttons in English after switching languages.
        AutomationProperties.SetName(_previewBtn, Loc.T("PickRangesTooltip"));
        _deleteBtn.ToolTip = Loc.T("RemoveFileTooltip");
        AutomationProperties.SetName(_deleteBtn, Loc.T("RemoveFileTooltip"));
    }

    /// <summary>A padded, rounded, transparent square around one icon: the
    /// click target and the surface the hover wash is painted on.</summary>
    private static Border MakeIconSlot(ShapePath icon, Cursor cursor)
    {
        return new Border
        {
            Background = Theme.TransparentBrush,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Width = IconHit,
            // Bounded by the identity line rather than by IconHit: the line
            // is RowLine1H tall, and a taller slot would simply be clipped.
            Height = Theme.RowLine1H,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = cursor,
            Child = icon,
        };
    }

    private void OnDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Locked) return;
        if (StartsInsideControl(e.OriginalSource as DependencyObject)) return;
        ((UIElement)sender).CaptureMouse();
        _isDraggingCandidate = true;
        // The move cursor is not a standing hover hint over the card - it
        // only appears once a press has actually claimed the row as a drag,
        // and for as long as that drag lasts. ForceCursor makes it win over
        // whatever cursor the pointer happens to be sitting on underneath
        // (the range column's chips carry their own Hand/IBeam cursors) for
        // the whole capture, rather than flickering between them as the
        // pointer crosses those children while dragging.
        Cursor = Cursors.SizeAll;
        ForceCursor = true;
        DragStartRequested?.Invoke(this, e);
    }

    /// <summary>
    /// True when a press originated on the edit/preview/delete buttons, or on
    /// one of the range list's own interactive pieces (a chip, the add/
    /// overflow pill, an inline-editor field or button, the raw text box) -
    /// the parts of the card that own the click instead of starting a drag.
    /// Everything else, INCLUDING the empty background of the range column
    /// itself (the space reserved for chips that are not there, or between
    /// the ones that are), is fair game for a drag - see
    /// RangeChipList.IsInteractiveHit.
    ///
    /// This is checked explicitly rather than left to those children marking
    /// the event handled. The entry does normally swallow the press (that is
    /// how the caret gets placed), but relying on it makes "can I still type
    /// in this box" depend on a WPF internal: the editor's mouse handling
    /// bails out early when it cannot resolve a hit against a laid-out text
    /// view, and then the press sails straight up to the row and starts
    /// dragging it instead of putting a caret in the field. An explicit
    /// origin test cannot fail that way and says what it means.
    /// </summary>
    private bool StartsInsideControl(DependencyObject? source)
    {
        if (_rangeBox.IsInteractiveHit(source)) return true;
        for (var cur = source; cur != null; cur = UiHelpers.NextAncestor(cur))
        {
            if (ReferenceEquals(cur, _editBtn) || ReferenceEquals(cur, _previewBtn) || ReferenceEquals(cur, _deleteBtn))
                return true;
            if (ReferenceEquals(cur, this))
                return false;
        }
        return false;
    }

    private void OnDragMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCandidate) return;
        DragMoveRequested?.Invoke(this, e);
    }

    private void OnDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingCandidate) return;
        _isDraggingCandidate = false;
        ((UIElement)sender).ReleaseMouseCapture();
        ForceCursor = false;
        Cursor = Cursors.Arrow;
        DragEndRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the row's serial number. The badge shows the bare
    /// digit, so the word it replaced is kept for assistive tech, which
    /// would otherwise announce the row as just a number.</summary>
    public void SetIndex(int i)
    {
        _badgeText.Text = i.ToString();
        AutomationProperties.SetName(_badge, $"File {i}");
    }

    /// <summary>
    /// The row's ranges, as the same comma-separated string this property
    /// has always exposed. Unchanged on purpose: Save/Load project, the trim
    /// window's Apply and both project collectors read and write it, so
    /// swapping the entry field for chips stayed a presentation change.
    ///
    /// Assigning it is still the programmatic path (Load Project,
    /// PreviewPlayerWindow Apply) and still bypasses the live-typing
    /// auto-formatter, which strips to digits and re-splits on a hardcoded
    /// 6-digit MM:SS:ff layout and would silently truncate already-correct
    /// values for files over ~100 minutes (AUDIT_TODO.md #1). Callers are
    /// expected to hand over well-formed range text. RangeChipList.Text has
    /// the same contract, so the guard now lives there rather than here.
    /// </summary>
    public string RangesText
    {
        get => _rangeBox.Text;
        set => _rangeBox.Text = value;
    }

    /// <summary>
    /// Switches this row between the normal fixed-slot card (single-line
    /// chip strip, "+N" overflow pill) and a variable-height one whose second
    /// line wraps every range chip onto as many lines as it needs, growing
    /// the card downward instead of hiding chips behind "+N".
    ///
    /// MainWindow is the only caller, and only turns this on while the window
    /// is maximized - see MainWindow.OnWindowStateChanged. At any other
    /// window size the row list positions cards at a fixed Theme.RowH step,
    /// so a row that grew downward would overlap the next one (the same
    /// constraint RangeChipList's own doc comment describes); only once the
    /// list is walked by each row's real height (MainWindow.RowSlotHeight)
    /// is it safe to let a card be taller than that step.
    /// </summary>
    public void SetWrapMode(bool wrap)
    {
        if (_wrapMode == wrap) return;
        _wrapMode = wrap;
        _line2Row.Height = wrap ? GridLength.Auto : new GridLength(Theme.RowInputH + Theme.RowLineGap);
        _rangeBox.SetWrapMode(wrap);
        // Height itself has to give up being a fixed constant too - RowH is
        // sized for exactly one chip line (see Theme.RowCardH's formula), so
        // pinning it here would clip every wrapped line past the first.
        Height = wrap ? double.NaN : Theme.RowH;
    }

    public void SetDragging(bool dragging)
    {
        _root.Background = dragging ? Theme.BgRowHlBrush : Theme.BgRowBrush;
        // Accent-colored border (instead of the plain lighter gray used
        // before) so the picked-up card reads as "active", not just
        // "hovered" - the same orange the badge/handle already use to mean
        // "this row". Paired with the scale-down and shadow set below, the
        // dragged card visibly detaches from the list instead of merely
        // swapping color.
        _root.BorderBrush = dragging ? Theme.FgAccentBrush : Theme.BorderCardBrush;
        _root.Effect = dragging ? _dragShadow : null;
        _dragScale.ScaleX = dragging ? 0.96 : 1.0;
        _dragScale.ScaleY = dragging ? 0.96 : 1.0;
        _handleIcon.Fill = dragging ? Theme.FgAccentBrush : Theme.FgDimBrush;
    }

    /// <summary>Greys the row out and blocks removal/dragging/editing while
    /// a job is running, without hiding it from view.</summary>
    public void SetLocked(bool locked)
    {
        Locked = locked;
        // The move cursor itself no longer stands on hover (see
        // OnDragMouseDown) - Arrow is the whole card's resting cursor
        // regardless of lock state, so nothing here needs to toggle it.
        _rangeBox.SetLocked(locked);
        _editBtn.Background = Theme.TransparentBrush;
        _editBtn.Cursor = locked ? Cursors.Arrow : Cursors.Hand;
        RefreshEditIcon();
        _handleIcon.Fill = locked ? Theme.DisabledHandleBrush : Theme.FgDimBrush;
        // The badge number stays dark-on-gold regardless of lock state - it
        // is not part of the greyed-out treatment. Toggling it (dim while
        // locked, accent orange while not) previously left it either
        // washed-out white or blended into the badge's own orange
        // background, both unreadable.
        _badgeText.Foreground = Theme.OnAccentBrush;
        _nameLabel.Foreground = locked ? Theme.FgDimBrush : Theme.FgBrush;
        _previewIcon.Stroke = locked ? Theme.FgDimBrush : Theme.FgLinkBrush;
        _previewBtn.Background = Theme.TransparentBrush;
        _previewBtn.Cursor = locked ? Cursors.Arrow : Cursors.Hand;
        _deleteIcon.Stroke = locked ? Theme.FgDimBrush : Theme.RedBrush;
        _deleteBtn.Background = Theme.TransparentBrush;
        _deleteBtn.Cursor = locked ? Cursors.Arrow : Cursors.Hand;
    }
}
