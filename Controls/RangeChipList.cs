using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipGlue.Models;
using ShapePath = System.Windows.Shapes.Path;

namespace ClipGlue.Controls;

/// <summary>
/// The time ranges of one file, shown as pills instead of one raw text
/// field. Replaces the single <see cref="TextBox"/> the row used to carry.
///
/// <para><b>The raw text is still the model.</b> <see cref="Text"/> is the
/// same comma-separated "MM:SS:ff-MM:SS:ff, ..." string the row has always
/// exposed, and the chips are a view rendered from it. Save/Load project,
/// the trim window's Apply, and both project collectors go on reading and
/// writing that one string, so this is a presentation change and nothing
/// downstream of it had to move.</para>
///
/// <para><b>Three modes, one slot.</b> Chips (default), the inline add/edit
/// editor, and the raw text field. They swap in place rather than stacking,
/// because the row card's height is pinned to
/// <see cref="Theme.RowCardH"/> - the list canvas positions rows at fixed
/// <see cref="Theme.RowH"/> steps, so a row that grew downward would overlap
/// the next one. Keeping the typed path one click away rather than one line
/// lower is the only version of "keep it, demoted" that fits a fixed-height
/// row.</para>
/// </summary>
public sealed class RangeChipList : Grid
{
    /// <summary>Raised whenever <see cref="Text"/> changes because the USER
    /// changed it here. Programmatic assignment does not raise it, so a
    /// caller writing the value back cannot loop.</summary>
    public event EventHandler? TextChanged;

    /// <summary>Raised only on a DISCRETE edit - a chip added, edited or
    /// removed - never per keystroke in the raw field. Callers that do
    /// something expensive in response (the project preview swaps its
    /// decoder) must use this rather than <see cref="TextChanged"/>.</summary>
    public event EventHandler? Committed;

    /// <summary>Raised by the add chip when the user asks for the visual
    /// picker instead of typing - the row forwards it to the trim window.</summary>
    public event EventHandler? PickVisuallyRequested;

    /// <summary>Raised when the raw-text mode is entered or left, so the row
    /// can light up the pencil button that toggles it. The button lives in
    /// the ROW's icon cluster rather than in here: it belongs with the other
    /// two row actions visually, and keeping it out of this control hands
    /// the chip strip the full width of its column - measured, that is the
    /// difference between fitting one chip and two at the default window
    /// width.</summary>
    public event EventHandler? RawModeChanged;

    /// <summary>True while the raw comma-separated field is showing instead
    /// of the chips.</summary>
    public bool RawVisible => _rawBox.Visibility == Visibility.Visible;

    /// <summary>Switches between the chips and the raw text field. The row's
    /// pencil button calls this.</summary>
    public void ToggleRaw()
    {
        if (_locked) return;
        ShowRaw(!RawVisible);
    }

    private bool _wrapMode;

    /// <summary>
    /// Switches the chip strip between the normal fixed-height "one line plus
    /// a +N pill" layout and a multi-line layout that shows every chip and
    /// grows downward instead. The row control (FileRowControl) is the only
    /// caller - it only turns this on while the window is maximized, where
    /// the list has spare height to give a taller row, and the list canvas is
    /// walked with each row's real height rather than a fixed step (see
    /// MainWindow.RowSlotHeight). At any other window size the row height is
    /// still a fixed constant the canvas positions by multiplying, so wrapping
    /// there would push chips outside the card - hence this being opt-in
    /// rather than automatic.
    /// </summary>
    public void SetWrapMode(bool wrap)
    {
        if (_wrapMode == wrap) return;
        _wrapMode = wrap;
        Height = wrap ? double.NaN : Theme.RowInputH;
        _strip.WrapMode = wrap;
        _strip.InvalidateMeasure();
    }

    private readonly ChipStrip _strip;
    private readonly Border _addChip;
    private readonly Border _moreChip;
    private readonly TextBlock _moreLabel;

    private readonly Grid _inlineEditor;
    private readonly TextBox _inlineStart;
    private readonly TextBox _inlineEnd;
    // Held so a language switch can re-pull them: the inline editor is built
    // once in the constructor, and the row control outlives a switch.
    private TextBlock _inlineStartLabel = null!, _inlineEndLabel = null!;
    private Border _inlineOkButton = null!, _inlineCancelButton = null!;

    private readonly TextBox _rawBox;

    private readonly List<string> _ranges = new();
    private bool _locked;
    private bool _suppressRawSync;
    /// <summary>Index being edited by the inline editor, or -1 when it is
    /// adding a new range rather than editing an existing one.</summary>
    private int _editIndex = -1;

    public RangeChipList()
    {
        Height = Theme.RowInputH;
        Background = Theme.TransparentBrush;

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });


        _strip = new ChipStrip { VerticalAlignment = VerticalAlignment.Center };

        _moreLabel = MakeChipText("", Theme.TextMidBrush);
        _moreChip = MakeChipShell(_moreLabel, Theme.BgCardHl2Brush);
        // Fixed minimum so the pill's desired width does not change with the
        // digits in it. ChipStrip decides how many chips fit by reserving
        // this width DURING measure and then writes the label - if the label
        // could change that width, the next pass could reach a different
        // answer and the layout would oscillate.
        _moreChip.MinWidth = 37;
        _moreChip.Cursor = Cursors.Hand;
        _moreChip.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_locked) ShowRaw(true); };

        _addChip = BuildAddChip();

        // Order matters: ChipStrip lays children out left to right and treats
        // the last two as the add and overflow chips.
        _strip.Children.Add(_addChip);
        _strip.Children.Add(_moreChip);
        _strip.SetTrailingChips(_addChip, _moreChip);

        Children.Add(_strip);

        (_inlineEditor, _inlineStart, _inlineEnd) = BuildInlineEditor();
        _inlineEditor.Visibility = Visibility.Collapsed;
        Children.Add(_inlineEditor);

        _rawBox = BuildRawBox();
        _rawBox.Visibility = Visibility.Collapsed;
        Children.Add(_rawBox);

        Loc.LanguageChanged += RefreshLanguage;
        Unloaded += (_, _) => Loc.LanguageChanged -= RefreshLanguage;

        Rebuild();
    }

    private void RefreshLanguage()
    {
        _addChip.ToolTip = Loc.T("AddRangeTooltip");
        AutomationProperties.SetName(_addChip, Loc.T("AddRangeLabel"));


        _rawBox.ToolTip = Loc.T("ManualEntryTooltip");
        _inlineStartLabel.Text = Loc.T("FieldStartLabel");
        _inlineEndLabel.Text = Loc.T("FieldEndLabel");
        RelabelInline(_inlineOkButton, Loc.T("OK"));
        RelabelInline(_inlineCancelButton, Loc.T("Cancel"));
        // Chips carry translated tooltips of their own, so they have to be
        // rebuilt rather than just re-labelled.
        Rebuild();
    }

    private static void RelabelInline(Border button, string name)
    {
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
    }

    // -- model ----------------------------------------------------------------

    /// <summary>
    /// The comma-separated range text. Assigning it re-renders the chips and
    /// does NOT raise <see cref="TextChanged"/>, matching the old TextBox's
    /// programmatic path (Load Project, the trim window's Apply), which
    /// likewise bypassed the live formatter - see FileRowControl.RangesText.
    /// </summary>
    public string Text
    {
        get => string.Join(", ", _ranges);
        set
        {
            SetRangesFromText(value ?? "");
            Rebuild();
        }
    }

    /// <summary>Splits comma-separated range text into <see cref="_ranges"/>,
    /// dropping empty parts. Shared by the <see cref="Text"/> setter and the
    /// raw-box editor, which parse the same shape from two different
    /// sources.</summary>
    private void SetRangesFromText(string text)
    {
        _ranges.Clear();
        foreach (var part in text.Split(','))
        {
            var t = part.Trim();
            if (t.Length > 0) _ranges.Add(t);
        }
    }

    private void CommitUserEdit()
    {
        Rebuild();
        TextChanged?.Invoke(this, EventArgs.Empty);
        Committed?.Invoke(this, EventArgs.Empty);
    }

    // -- chips ----------------------------------------------------------------

    private void Rebuild()
    {
        // Drop the chips built last time, keeping the two trailing ones the
        // strip needs to always have.
        for (int i = _strip.Children.Count - 1; i >= 0; i--)
        {
            var child = _strip.Children[i];
            if (!ReferenceEquals(child, _addChip) && !ReferenceEquals(child, _moreChip))
                _strip.Children.RemoveAt(i);
        }

        for (int i = 0; i < _ranges.Count; i++)
            _strip.Children.Insert(i, BuildRangeChip(_ranges[i], i));

        _moreChip.ToolTip = _ranges.Count > 0 ? string.Join(", ", _ranges) : null;
        // The overflow pill needs its own name: it is a clickable Border, so
        // nothing derives one for it, and what it stands for is the ranges it
        // is standing in for.
        AutomationProperties.SetName(_moreChip,
            _ranges.Count > 0 ? string.Join(", ", _ranges) : Loc.T("AddRangeLabel"));
        _addChip.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        RawModeChanged?.Invoke(this, EventArgs.Empty);
        // The raw field is deliberately NOT synced here. It is filled by
        // ShowRaw when raw mode opens; writing it from Rebuild would run its
        // own TextChanged handler, which parses back into _ranges and raises
        // TextChanged again - a spurious round trip on every chip edit.
        _strip.InvalidateMeasure();
    }

    /// <summary>One committed range, built from the SHARED chip component so
    /// this list and the trim window's range list cannot drift apart. Only
    /// the behaviour around it is local: clicking opens the inline editor for
    /// that range, and the ✕ removes it.</summary>
    private UIElement BuildRangeChip(string range, int index)
    {
        var chip = new RangeChip(range, _locked ? RangeChipTone.Muted : RangeChipTone.Info)
        {
            Margin = new Thickness(0, 0, Theme.ChipGap, 0),
        };
        if (!_locked)
        {
            chip.AddRemoveButton(Loc.T("RemoveRangeTooltip"), () =>
            {
                if (index >= 0 && index < _ranges.Count) _ranges.RemoveAt(index);
                CommitUserEdit();
            });
            chip.MakeClickable(Loc.T("EditRangeTooltip"), () => OpenInlineEditor(index));
        }
        return chip;
    }


    private Border BuildAddChip()
    {
        // Icon only, no "Dodaj zakres"/"Add range" label - Bartek asked for
        // the plus alone once the chip strip had both a labeled add chip and
        // (once ranges exist) a raw-mode pencil doing similar jobs nearby;
        // the tooltip and the AutomationProperties name below still carry the
        // label for anyone who needs it, sighted or not. The glyph stays at
        // its native 16x16 size - an earlier version scaled it 1.25x to
        // match the chip enlargement, but combined with the wide padding
        // below that read as "definitely too big" once built; a plain
        // native-size icon in a square button (matching the app's existing
        // 28 DIP icon-hit convention, e.g. FileRowControl's edit/preview/
        // delete buttons) is the more familiar affordance.
        var plus = Icons.Stroked(Icons.Plus, Theme.TextMidBrush, 1.4);

        var chip = new Border
        {
            // Ghost, not filled: the add affordance must not read as one more
            // range that is already part of the output.
            Background = Theme.TransparentBrush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.ChipRadius),
            // Square, not padding-driven: a fixed Width equal to the chip's
            // own Height keeps this an icon-button square rather than a
            // pill that happens to be icon-only.
            Width = Theme.RowInputH - 4,
            Margin = new Thickness(0, 0, Theme.ChipGap, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Height = Theme.RowInputH - 4,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("AddRangeTooltip"),
            Child = plus,
        };
        AutomationProperties.SetName(chip, Loc.T("AddRangeLabel"));
        chip.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (!_locked) OpenInlineEditor(-1); };
        // Right-click hands the job to the timeline instead, for the times
        // when the number is not already known.
        chip.MouseRightButtonUp += (_, e) => { e.Handled = true; if (!_locked) PickVisuallyRequested?.Invoke(this, EventArgs.Empty); };
        chip.MouseEnter += (_, _) =>
        {
            if (_locked) return;
            chip.Background = Theme.BgCardHlBrush;
            chip.BorderBrush = Theme.AccentGold500Brush;
        };
        chip.MouseLeave += (_, _) =>
        {
            chip.Background = Theme.TransparentBrush;
            chip.BorderBrush = Theme.BorderBrush;
        };
        return chip;
    }

    private static TextBlock MakeChipText(string text, Brush fg) => new()
    {
        Text = text,
        Foreground = fg,
        FontFamily = Theme.UiFontFamily,
        FontSize = Theme.ChipFontSize,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border MakeChipShell(UIElement child, Brush bg) => new()
    {
        Background = bg,
        BorderBrush = Theme.BorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(Theme.ChipRadius),
        Padding = new Thickness(11, 0, 11, 0),
        Margin = new Thickness(0, 0, Theme.ChipGap, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Height = Theme.RowInputH - 4,
        Child = child,
    };

    /// <summary>Called by <see cref="ChipStrip"/> once it knows how many
    /// chips actually fit.</summary>
    internal void SetOverflowCount(int hidden) => _moreLabel.Text = hidden > 0 ? $"+{hidden}" : "";

    // -- inline editor ---------------------------------------------------------

    private (Grid Root, TextBox Start, TextBox End) BuildInlineEditor()
    {
        var root = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var start = MakeTimeBox();
        var end = MakeTimeBox();

        _inlineStartLabel = MakeChipText(Loc.T("FieldStartLabel"), Theme.TextLowBrush);
        _inlineEndLabel = MakeChipText(Loc.T("FieldEndLabel"), Theme.TextLowBrush);
        _inlineOkButton = MakeInlineButton(Icons.Check, Theme.AccentCyan400Brush, Loc.T("OK"), CommitInline);
        _inlineCancelButton = MakeInlineButton(Icons.Close, Theme.TextMidBrush, Loc.T("Cancel"), CancelInline);

        row.Children.Add(_inlineStartLabel);
        row.Children.Add(start);
        row.Children.Add(_inlineEndLabel);
        row.Children.Add(end);
        row.Children.Add(_inlineOkButton);
        row.Children.Add(_inlineCancelButton);

        root.Children.Add(row);
        return (root, start, end);
    }

    /// <summary>Widest a bare "MM:SS:ff" value ever needs to be - the
    /// baseline every time box must fit without clipping, measured in the
    /// same font it renders in. Longer values (past 99 minutes) still grow
    /// the box beyond this via MinWidth rather than being cut off.</summary>
    private static double MeasureTimeTextWidth(string text)
    {
        var typeface = new Typeface(Theme.MonoFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
            Theme.ChipFontSize, Brushes.Black, 1.0);
        return ft.Width;
    }

    private TextBox MakeTimeBox()
    {
        var box = new TextBox
        {
            // No fixed Width: the box grows with what is typed (e.g. a
            // 3+ digit minute count) instead of clipping it, but never
            // shrinks below fitting "00:00:00" - see MeasureTimeTextWidth.
            MinWidth = MeasureTimeTextWidth("00:00:00") + 16 /* left+right Padding */ + 2 /* caret room */,
            Height = Theme.RowInputH - 4,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
            Background = Theme.BgCardHl2Brush,
            Foreground = Theme.AccentCyan300Brush,
            CaretBrush = Theme.AccentCyan300Brush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(6, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam,
        };
        UiHelpers.MakeRounded(box);
        // Digits-only auto-formatting, so typing "1000" still becomes "10:00" -
        // but through the SINGLE-VALUE formatter. This box holds one end of one
        // range, so the range formatter's comma and dash handling has nothing
        // to do here: it used to turn a "125:30:00" start into "12:53:00-0".
        box.TextChanged += (_, _) =>
        {
            if (_suppressRawSync) return;
            var formatted = TimeUtils.FormatTimeText(box.Text);
            if (formatted == box.Text) return;
            _suppressRawSync = true;
            try
            {
                box.Text = formatted;
                box.CaretIndex = formatted.Length;
            }
            finally { _suppressRawSync = false; }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitInline(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CancelInline(); }
        };
        return box;
    }

    private Border MakeInlineButton(Geometry icon, Brush stroke, string name, Action onClick)
    {
        var b = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(Theme.InputRadius),
            Background = Theme.TransparentBrush,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = name,
            Child = Icons.Stroked(icon, stroke, 1.5),
        };
        AutomationProperties.SetName(b, name);
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        b.MouseEnter += (_, _) => b.Background = Theme.IconHoverBgBrush;
        b.MouseLeave += (_, _) => b.Background = Theme.TransparentBrush;
        return b;
    }

    private void OpenInlineEditor(int index)
    {
        _editIndex = index;
        string start = "", end = "";
        if (index >= 0 && index < _ranges.Count)
        {
            var parts = _ranges[index].Split('-');
            start = parts.Length > 0 ? parts[0].Trim() : "";
            end = parts.Length > 1 ? parts[1].Trim() : "";
        }
        _suppressRawSync = true;
        try
        {
            _inlineStart.Text = start;
            _inlineEnd.Text = end;
        }
        finally { _suppressRawSync = false; }

        _strip.Visibility = Visibility.Collapsed;
        _rawBox.Visibility = Visibility.Collapsed;
        _inlineEditor.Visibility = Visibility.Visible;
        RawModeChanged?.Invoke(this, EventArgs.Empty);
        _inlineStart.Focus();
        _inlineStart.SelectAll();
    }

    private void CommitInline()
    {
        var start = _inlineStart.Text.Trim();
        var end = _inlineEnd.Text.Trim();
        // An empty pair is a cancel, not a blank range: writing "-" into the
        // model would fail validation later with nothing to point at.
        if (start.Length > 0 || end.Length > 0)
        {
            var text = start + "-" + end;
            if (_editIndex >= 0 && _editIndex < _ranges.Count) _ranges[_editIndex] = text;
            else _ranges.Add(text);
        }
        CloseInline();
        CommitUserEdit();
    }

    private void CancelInline()
    {
        CloseInline();
        Rebuild();
    }

    private void CloseInline()
    {
        _editIndex = -1;
        _inlineEditor.Visibility = Visibility.Collapsed;
        _strip.Visibility = Visibility.Visible;
        RawModeChanged?.Invoke(this, EventArgs.Empty);
    }

    // -- raw text mode ---------------------------------------------------------

    private TextBox BuildRawBox()
    {
        var box = new TextBox
        {
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.MonoFontSize,
            Background = Theme.BgCardHl2Brush,
            Foreground = Theme.AccentCyan300Brush,
            CaretBrush = Theme.AccentCyan300Brush,
            BorderBrush = Theme.BorderBrush,
            BorderThickness = new Thickness(1),
            Height = Theme.RowInputH,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.IBeam,
            ToolTip = Loc.T("ManualEntryTooltip"),
        };
        UiHelpers.MakeRounded(box);
        box.TextChanged += (_, _) =>
        {
            if (_suppressRawSync) return;
            var formatted = TimeUtils.FormatRangesText(box.Text);
            if (formatted != box.Text)
            {
                _suppressRawSync = true;
                try
                {
                    box.Text = formatted;
                    box.CaretIndex = formatted.Length;
                }
                finally { _suppressRawSync = false; }
            }
            // Parse straight back into the chip model, so leaving raw mode
            // never needs a separate "apply" step and the two views cannot
            // drift apart.
            _suppressRawSync = true;
            try { SetRangesFromText(box.Text); }
            finally { _suppressRawSync = false; }
            TextChanged?.Invoke(this, EventArgs.Empty);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { e.Handled = true; ShowRaw(false); }
        };
        return box;
    }

    private void ShowRaw(bool raw)
    {
        CloseInline();
        if (raw)
        {
            _suppressRawSync = true;
            try { _rawBox.Text = Text; }
            finally { _suppressRawSync = false; }
        }
        else
        {
            Rebuild();
        }
        _rawBox.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        _strip.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        RawModeChanged?.Invoke(this, EventArgs.Empty);
        if (raw)
        {
            _rawBox.Focus();
            _rawBox.CaretIndex = _rawBox.Text.Length;
        }
    }

    // -- state -----------------------------------------------------------------

    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (locked) { CloseInline(); ShowRaw(false); }
        _rawBox.IsReadOnly = locked;
        Rebuild();
    }

    // -- hit testing for the row's drag gesture --------------------------------

    /// <summary>
    /// True when <paramref name="source"/> - an OriginalSource from a mouse
    /// event that landed somewhere inside this control - is one of the
    /// actual interactive pieces here (a range chip, its remove button, the
    /// add/overflow pill, an inline-editor field or button, or the raw text
    /// box) rather than the empty background between and around them. The
    /// row control uses this so a drag can start from anywhere in the range
    /// column that is not already spoken for, instead of treating the whole
    /// column as off-limits the way blocking on this control's own identity
    /// used to.
    /// </summary>
    public bool IsInteractiveHit(DependencyObject? source)
    {
        for (var cur = source; cur != null; cur = UiHelpers.NextAncestor(cur))
        {
            if (cur is RangeChip or TextBox
                || ReferenceEquals(cur, _addChip) || ReferenceEquals(cur, _moreChip)
                || ReferenceEquals(cur, _inlineOkButton) || ReferenceEquals(cur, _inlineCancelButton))
                return true;
            if (ReferenceEquals(cur, this))
                return false;
        }
        return false;
    }
}

/// <summary>
/// Lays chips out on ONE line, left to right, and reports how many did not
/// fit so the list can show a "+N" pill instead of wrapping - or, in
/// <see cref="WrapMode"/>, flows every chip across as many lines as it takes
/// and never hides any of them.
///
/// One line is the default because it is a hard requirement at any normal
/// window size, not a style choice: the file row's height is fixed there
/// (the list canvas positions rows at fixed steps), so wrapping would push
/// chips outside the card rather than making the card taller. Measured
/// budget on this machine - at the default 860-wide window the range area
/// gets 478 DIP and one chip is about 162 DIP, so two chips plus the add
/// chip is the ceiling; the third onwards folds into "+N". WrapMode is the
/// maximized-window exception: MainWindow turns it on there, where the list
/// canvas positions rows by each one's real measured height instead of that
/// fixed step, so a taller card is free to happen.
/// </summary>
public sealed class ChipStrip : Panel
{
    /// <summary>Vertical gap between wrapped lines. Only meaningful in
    /// <see cref="WrapMode"/> - the single-line layout has nothing to
    /// stack.</summary>
    private const double LineGap = 6;

    private UIElement? _addChip;
    private UIElement? _moreChip;
    private int _visible;
    private bool _wrapMode;

    /// <summary>Set by RangeChipList.SetWrapMode. Changing it invalidates
    /// nothing by itself - the caller is expected to invalidate measure right
    /// after, since this only takes effect on the next layout pass.</summary>
    public bool WrapMode
    {
        get => _wrapMode;
        set => _wrapMode = value;
    }

    /// <summary>One wrapped chip's assigned position, computed during Measure
    /// and consumed by Arrange - the two must agree on where every chip
    /// landed, so the line-breaking decision is made exactly once per pass.</summary>
    private readonly List<(UIElement Chip, double X, double Y)> _wrapPositions = new();

    public void SetTrailingChips(UIElement addChip, UIElement moreChip)
    {
        _addChip = addChip;
        _moreChip = moreChip;
    }

    private IEnumerable<UIElement> RangeChips()
    {
        foreach (UIElement child in Children)
            if (!ReferenceEquals(child, _addChip) && !ReferenceEquals(child, _moreChip))
                yield return child;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var loose = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (UIElement child in Children) child.Measure(loose);

        return _wrapMode ? MeasureWrapped(availableSize) : MeasureSingleLine(availableSize);
    }

    private Size MeasureSingleLine(Size availableSize)
    {
        double budget = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double addW = _addChip is { Visibility: Visibility.Visible } ? _addChip.DesiredSize.Width : 0;

        var chips = RangeChips().ToList();
        double used = 0;
        _visible = 0;
        foreach (var chip in chips)
        {
            double w = chip.DesiredSize.Width;
            // Every chip after the first must also leave room for the "+N"
            // pill it might turn into, or the strip would fit N chips and
            // then have nowhere to say that N+1 was dropped.
            double reserve = addW + (_visible + 1 < chips.Count && _moreChip is not null
                ? _moreChip.DesiredSize.Width
                : 0);
            if (used + w + reserve > budget && _visible > 0) break;
            used += w;
            _visible++;
        }

        int hidden = chips.Count - _visible;
        if (_moreChip is not null)
            _moreChip.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Parent is RangeChipList owner) owner.SetOverflowCount(hidden);

        double total = used + addW + (hidden > 0 && _moreChip is not null ? _moreChip.DesiredSize.Width : 0);
        double height = 0;
        foreach (UIElement child in Children) height = Math.Max(height, child.DesiredSize.Height);
        return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), height);
    }

    /// <summary>Flows every range chip plus the add chip across as many lines
    /// as the available width demands - nothing is ever hidden behind "+N"
    /// here, so the overflow pill stays collapsed and reports zero.</summary>
    private Size MeasureWrapped(Size availableSize)
    {
        if (_moreChip is not null) _moreChip.Visibility = Visibility.Collapsed;
        if (Parent is RangeChipList owner) owner.SetOverflowCount(0);

        double budget = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;

        var order = RangeChips().ToList();
        if (_addChip is { Visibility: Visibility.Visible }) order.Add(_addChip);

        _wrapPositions.Clear();
        double x = 0, y = 0, lineH = 0, maxLineW = 0;
        bool lineHasContent = false;
        foreach (var chip in order)
        {
            double w = chip.DesiredSize.Width;
            double h = chip.DesiredSize.Height;
            if (lineHasContent && x + w > budget)
            {
                maxLineW = Math.Max(maxLineW, x);
                y += lineH + LineGap;
                x = 0;
                lineH = 0;
                lineHasContent = false;
            }
            _wrapPositions.Add((chip, x, y));
            x += w;
            lineH = Math.Max(lineH, h);
            lineHasContent = true;
        }
        maxLineW = Math.Max(maxLineW, x);
        double totalHeight = y + lineH;

        return new Size(double.IsInfinity(availableSize.Width) ? maxLineW : Math.Min(maxLineW, budget), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_wrapMode)
        {
            foreach (var (chip, x, y) in _wrapPositions)
                chip.Arrange(new Rect(x, y, chip.DesiredSize.Width, chip.DesiredSize.Height));
            return finalSize;
        }

        double px = 0;
        int i = 0;
        foreach (var chip in RangeChips())
        {
            if (i++ < _visible)
            {
                chip.Arrange(new Rect(px, 0, chip.DesiredSize.Width, finalSize.Height));
                px += chip.DesiredSize.Width;
            }
            else
            {
                // Arranged to nothing rather than collapsed: changing
                // Visibility from inside a layout pass re-triggers measure.
                // A zero-sized child draws nothing and is not hit-testable.
                chip.Arrange(new Rect(px, 0, 0, 0));
            }
        }
        if (_moreChip is { Visibility: Visibility.Visible })
        {
            _moreChip.Arrange(new Rect(px, 0, _moreChip.DesiredSize.Width, finalSize.Height));
            px += _moreChip.DesiredSize.Width;
        }
        if (_addChip is { Visibility: Visibility.Visible })
            _addChip.Arrange(new Rect(px, 0, _addChip.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }
}
