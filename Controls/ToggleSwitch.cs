using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipGlue.Controls;

/// <summary>
/// The pill switch behind "Snap to keyframes". Custom-drawn like everything
/// else in this app rather than a restyled ToggleButton: the stock control's
/// template would have to be replaced wholesale anyway to lose its chrome,
/// and a switch is two rounded rectangles.
///
/// Blue rather than accent when on, because the palette reserves the accent
/// for the range currently being edited and this is a persistent setting,
/// not an action.
/// </summary>
public sealed class ToggleSwitch : FrameworkElement
{
    private const double PillW = 40;
    private const double PillH = 22;
    private const double KnobR = 8;

    private bool _isOn;
    private bool _hover;

    public event Action<bool>? Toggled;

    public ToggleSwitch(bool isOn)
    {
        _isOn = isOn;
        Width = PillW;
        Height = PillH;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        IsEnabledChanged += OnEnabledChanged;
    }

    public bool IsOn
    {
        get => _isOn;
        set { if (_isOn == value) return; _isOn = value; InvalidateVisual(); }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsEnabled) return;
        IsOn = !_isOn;
        Toggled?.Invoke(_isOn);
    }

    private void OnEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Cursor = IsEnabled ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        bool live = IsEnabled;
        Brush pill = !live ? Theme.DisabledBgBrush
            : _isOn ? Theme.FgLinkBrush
            : _hover ? Theme.BgRowHlBrush
            : Theme.BgRowBrush;
        Brush knob = !live ? Theme.DisabledFgBrush : _isOn ? Theme.FgBrush : Theme.FgDimBrush;

        dc.DrawRoundedRectangle(pill, new Pen(Theme.BorderCardBrush, 1),
            new Rect(0.5, 0.5, PillW - 1, PillH - 1), (PillH - 1) / 2.0, (PillH - 1) / 2.0);

        double cx = _isOn ? PillW - KnobR - 3 : KnobR + 3;
        dc.DrawEllipse(knob, null, new Point(cx, PillH / 2.0), KnobR, KnobR);
    }
}
