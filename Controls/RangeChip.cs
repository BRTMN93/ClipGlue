using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;

namespace ClipGlue.Controls;

/// <summary>What a chip is saying about the range it carries.</summary>
public enum RangeChipTone
{
    /// <summary>A committed range. Cyan - the information accent.</summary>
    Info,
    /// <summary>The range currently being edited. Gold - the action accent,
    /// the same one the scrubber's live band and the row badge use.</summary>
    Active,
    /// <summary>Read-only, because a job is running.</summary>
    Muted,
}

/// <summary>
/// One time range drawn as a pill: a status dot, the timecode in mono, and
/// optionally a round remove button.
///
/// <para>Extracted so there is exactly ONE implementation of this shape.
/// The file list in the main window and the range list in the trim window
/// both show the same thing - a range you can act on - and previously each
/// drew its own version, which is two places to fix when the token file
/// moves. The chip's own metrics (dot size, close size, radius, font) come
/// from <see cref="Theme"/>, so neither caller spells them out.</para>
///
/// <para>What each caller adds AROUND the chip is still its own business:
/// the trim window puts a length readout and play/edit/delete icons beside
/// it, the file row puts the add-range chip after it. Only the pill itself
/// is shared.</para>
/// </summary>
public sealed class RangeChip : Border
{
    private readonly TextBlock _label;
    private readonly Border _dot;
    private readonly StackPanel _content;
    private RangeChipTone _tone;

    public RangeChip(string text, RangeChipTone tone = RangeChipTone.Info)
    {
        _tone = tone;

        _dot = new Border
        {
            Width = Theme.ChipDotSize,
            Height = Theme.ChipDotSize,
            CornerRadius = new CornerRadius(Theme.ChipRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _label = new TextBlock
        {
            Text = text,
            FontFamily = Theme.MonoFontFamily,
            FontSize = Theme.ChipFontSize,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _content = new StackPanel { Orientation = Orientation.Horizontal };
        _content.Children.Add(_dot);
        _content.Children.Add(_label);

        Background = Theme.BgCardHlBrush;
        BorderBrush = Theme.BorderBrush;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(Theme.ChipRadius);
        Padding = new Thickness(10, 0, 10, 0);
        Height = Theme.RowInputH - 4;
        VerticalAlignment = VerticalAlignment.Center;
        Child = _content;

        AutomationProperties.SetName(this, text);
        ApplyTone();
    }

    public string Text
    {
        get => _label.Text;
        set
        {
            _label.Text = value;
            AutomationProperties.SetName(this, value);
        }
    }

    public RangeChipTone Tone
    {
        get => _tone;
        set { _tone = value; ApplyTone(); }
    }

    private void ApplyTone()
    {
        (_dot.Background, _label.Foreground) = _tone switch
        {
            RangeChipTone.Active => (Theme.AccentGold500Brush, (Brush)Theme.AccentGold300Brush),
            RangeChipTone.Muted => (Theme.TextLowBrush, Theme.TextLowBrush),
            _ => (Theme.AccentCyan400Brush, Theme.AccentCyan300Brush),
        };
    }

    /// <summary>
    /// Adds the round ✕ at the trailing end. Not part of the constructor,
    /// because the trim window's list already carries its own delete icon
    /// next to the chip and a second one inside it would be two ways to do
    /// the same thing on one row.
    /// </summary>
    public void AddRemoveButton(string tooltip, Action onRemove)
    {
        // The right padding shrinks to make room: the button brings its own
        // visual weight, so the pill would otherwise read lopsided.
        Padding = new Thickness(10, 0, 3, 0);

        var glyph = Icons.Stroked(Icons.CloseSmall, Theme.TextMidBrush, 1.4);
        var close = new Border
        {
            Width = Theme.ChipCloseSize,
            Height = Theme.ChipCloseSize,
            CornerRadius = new CornerRadius(Theme.ChipRadius),
            Background = Theme.TransparentBrush,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = glyph,
        };
        AutomationProperties.SetName(close, tooltip);
        close.MouseEnter += (_, _) =>
        {
            close.Background = Theme.DangerDimBrush;
            glyph.Stroke = Theme.TextHiBrush;
        };
        close.MouseLeave += (_, _) =>
        {
            close.Background = Theme.TransparentBrush;
            glyph.Stroke = Theme.TextMidBrush;
        };
        close.MouseLeftButtonDown += (_, e) => { e.Handled = true; onRemove(); };
        _content.Children.Add(close);
    }

    /// <summary>Makes the whole pill clickable, with a lift on hover. Used
    /// by the file row, where clicking a chip opens the inline editor for
    /// that range.</summary>
    public void MakeClickable(string tooltip, Action onClick)
    {
        Cursor = Cursors.Hand;
        ToolTip = tooltip;
        MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        MouseEnter += (_, _) => Background = Theme.BgCardHl2Brush;
        MouseLeave += (_, _) => Background = Theme.BgCardHlBrush;
    }
}
