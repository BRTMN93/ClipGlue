using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ClipGlue.Controls;
using ClipGlue.Models;

namespace ClipGlue;

/// <summary>
/// The three button roles the redesign recognises. Passing one of these
/// instead of a hand-picked (bg, fg, hoverBg) triple is what keeps "primary"
/// meaning the same thing in the toolbar, the action bar and the trim-range
/// window - the pre-redesign code spelled the same three brushes out at
/// eleven call sites, so a palette change had to be applied eleven times.
/// </summary>
public enum ButtonVariant
{
    /// <summary>The one action that starts something: gradient fill, warm
    /// dark label, and the accent glow under it. Deliberately scarce - the
    /// mockup allows it on START, "Add video files" and Apply, and nowhere
    /// else.</summary>
    Primary,
    /// <summary>Everything else: card-colored fill with a hairline outline.</summary>
    Ghost,
    /// <summary>Destructive: outlined in Danger rather than filled with it,
    /// so STOP and Remove do not compete with the primary CTA for
    /// attention.</summary>
    Danger,
}

/// <summary>Bundles what a flat button needs to restore its visuals after
/// being enabled/disabled, since the button's own Background/Foreground (and,
/// for the primary variant, its border and glow) get overwritten while
/// disabled.</summary>
public sealed class ButtonColors
{
    public required Brush NormalBg { get; set; }
    public required Brush NormalFg { get; set; }
    public required Brush HoverBg { get; set; }
    /// <summary>Outline for the ghost and danger variants; null for a filled
    /// button, which has no border at all.</summary>
    public Brush? NormalBorder { get; set; }
    /// <summary>The primary variant's accent glow, kept here so disabling can
    /// take it off and re-enabling can put it back. Null for every other
    /// variant - see Theme.CardTopHighlightBrush for why this must never be
    /// handed out to a list row.</summary>
    public Effect? Glow { get; set; }
}

/// <summary>
/// Shared flat-button construction, used by MainWindow, PreviewPlayerWindow
/// and the dialogs - one place instead of the three near-duplicate
/// _flat_button() helpers the Python original had in app.py,
/// preview_player.py and confirm_dialog.py.
/// </summary>
public static class UiHelpers
{
    private static readonly ControlTemplate FlatTemplate = BuildFlatTemplate();
    private static readonly ControlTemplate RoundedTextBoxTemplate = BuildRoundedTextBoxTemplate();

    /// <summary>Next ancestor up the visual tree, falling back to the logical
    /// one - a press can originate on a non-Visual (e.g. a text Run), which
    /// VisualTreeHelper refuses outright (see gotchas #17/#19 in CLAUDE.md).
    /// Shared by every hit-test walk in the app instead of each control
    /// re-deriving the same fallback.</summary>
    public static DependencyObject? NextAncestor(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    /// <summary>True if <paramref name="source"/> is <paramref name="container"/>
    /// or a descendant of it, walking up via <see cref="NextAncestor"/>.</summary>
    public static bool IsInside(DependencyObject? source, DependencyObject container)
    {
        for (var cur = source; cur != null; cur = NextAncestor(cur))
            if (ReferenceEquals(cur, container)) return true;
        return false;
    }

    public readonly record struct VolumeControl(System.Windows.Shapes.Path Icon, MiniSlider Slider, Border Slot, FrameworkElement Row);

    /// <summary>Speaker icon + mute toggle + slider, wired to
    /// <paramref name="toggleMute"/>/<paramref name="setVolume"/> and to
    /// refresh its own tooltip from <paramref name="isMuted"/> after either
    /// fires. Shared by PreviewPlayerWindow and ProjectPreviewPanel, which
    /// need the identical control at different sizes/tints and each own
    /// their own muted/volume state, hence the callbacks instead of a
    /// two-way binding.</summary>
    public static VolumeControl BuildVolumeControl(
        double slotSize, double sliderWidth, double sliderMarginLeft, Brush iconBrush, double initialVolume,
        Func<bool> isMuted, Action toggleMute, Action<double> setVolume)
    {
        var icon = Icons.Stroked(Icons.Speaker, iconBrush, 1.5);
        var slot = new Border
        {
            Background = Theme.TransparentBrush,
            CornerRadius = new CornerRadius(9),
            Width = slotSize,
            Height = slotSize,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("MuteTooltip"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon,
        };
        System.Windows.Automation.AutomationProperties.SetName(slot, Loc.T("MuteTooltip"));

        void RefreshTooltip()
        {
            slot.ToolTip = isMuted() ? Loc.T("UnmuteTooltip") : Loc.T("MuteTooltip");
            System.Windows.Automation.AutomationProperties.SetName(slot, (string)slot.ToolTip);
        }

        slot.MouseLeftButtonDown += (_, e) => { e.Handled = true; toggleMute(); RefreshTooltip(); };

        var slider = new MiniSlider(sliderWidth, initialVolume) { Margin = new Thickness(sliderMarginLeft, 0, 0, 0) };
        slider.ValueChanged += v => { setVolume(v); RefreshTooltip(); };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slot);
        row.Children.Add(slider);
        return new VolumeControl(icon, slider, slot, row);
    }

    private static ControlTemplate BuildFlatTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        // Bound, unlike before the redesign: the ghost and danger variants
        // are outlined rather than filled, and the stock template ignored
        // BorderBrush/BorderThickness entirely, so an outline set on the
        // button simply never appeared.
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.ButtonRadius));
        border.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(ContentPresenter.MarginProperty, new Binding("Padding")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        border.AppendChild(content);

        template.VisualTree = border;
        return template;
    }

    /// <summary>
    /// A TextBox skin with rounded corners. WPF's stock TextBox template
    /// draws a hard-cornered border of its own, so the only way to round an
    /// entry field is to replace that template outright: the frame becomes a
    /// Border with a CornerRadius, wrapped around the content host that
    /// TextBoxBase looks up by the exact name "PART_ContentHost" in order to
    /// place its text - omit that name and the box renders permanently empty.
    /// </summary>
    private static ControlTemplate BuildRoundedTextBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.InputRadius));
        border.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);

        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        host.SetValue(Control.BackgroundProperty, Theme.TransparentBrush);
        host.SetValue(FrameworkElement.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetBinding(FrameworkElement.MarginProperty, new Binding("Padding")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        border.AppendChild(host);

        template.VisualTree = border;
        return template;
    }

    /// <summary>Applies the rounded skin to an entry field (the file row's
    /// time-range box).</summary>
    public static void MakeRounded(TextBox box) => box.Template = RoundedTextBoxTemplate;

    /// <summary>
    /// Builds a button in one of the three redesign roles. Prefer this over
    /// the explicit-brush overload below: it is the single place that decides
    /// what "primary" looks like, so the look moves with
    /// <see cref="ButtonVariant"/> rather than with whoever last copied a
    /// brush triple into a view.
    /// </summary>
    public static Button CreateFlatButton(string text, ButtonVariant variant,
        double fontSize = Theme.UiFontSize, bool bold = true, double padH = 18, double padV = 10,
        Geometry? icon = null)
    {
        var (bg, fg, hoverBg, border, glow) = ResolveVariant(variant);
        return BuildFlatButton(text, bg, fg, hoverBg, border, glow, fontSize, bold, padH, padV, icon);
    }

    /// <summary>The variant table. Everything here comes from the token
    /// file's "Komponenty" section; nothing picks its own colors.</summary>
    private static (Brush Bg, Brush Fg, Brush HoverBg, Brush? Border, Effect? Glow) ResolveVariant(ButtonVariant v) => v switch
    {
        // Hover lifts the whole fill to the top of the gradient rather than
        // shifting the gradient, which at button size reads as a flicker.
        ButtonVariant.Primary => (Theme.AccentGradientBrush, Theme.OnAccentBrush,
            Theme.AccentGold300Brush, null, Theme.AccentGlow),
        ButtonVariant.Danger => (Theme.BgCardBrush, Theme.DangerBrush,
            Theme.DangerDimBrush, Theme.DangerBrush, null),
        _ => (Theme.BgCardBrush, Theme.TextHiBrush,
            Theme.BgCardHlBrush, Theme.BorderSoftBrush, null),
    };

    /// <summary>Explicit-brush construction, kept for the call sites that
    /// genuinely need a one-off color pair (the modal dialogs, which tint
    /// their buttons to match the panel behind them). New code should pass a
    /// <see cref="ButtonVariant"/> instead.</summary>
    public static Button CreateFlatButton(string text, Brush bg, Brush fg, Brush hoverBg,
        double fontSize = Theme.UiFontSize, bool bold = true, double padH = 18, double padV = 10,
        Geometry? icon = null)
        => BuildFlatButton(text, bg, fg, hoverBg, border: null, glow: null, fontSize, bold, padH, padV, icon);

    private static Button BuildFlatButton(string text, Brush bg, Brush fg, Brush hoverBg,
        Brush? border, Effect? glow, double fontSize, bool bold, double padH, double padV, Geometry? icon)
    {
        var btn = new Button
        {
            Background = bg,
            Foreground = fg,
            BorderBrush = border,
            BorderThickness = new Thickness(border is null ? 0 : 1),
            Padding = new Thickness(padH, padV, padH, padV),
            Cursor = Cursors.Hand,
            FontFamily = Theme.UiFontFamily,
            FontSize = fontSize,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Template = FlatTemplate,
            FocusVisualStyle = null,
            Effect = glow,
        };
        btn.Content = icon is null ? text : BuildIconContent(icon, text);
        // A Button's accessible name is derived from its Content, so a
        // string content labels itself - but an icon+label StackPanel does
        // NOT, and the button goes out to screen readers and UI Automation
        // with an empty Name (verified: all four icon buttons came back as
        // name='' from a UIA sweep). Setting it explicitly keeps every
        // button addressable by its visible label whichever form it takes,
        // which also keeps the scripted UI tests in this repo working. This
        // stays unconditional now that a variant can also change Content's
        // shape - see gotcha #14.
        System.Windows.Automation.AutomationProperties.SetName(btn, text);
        var colors = new ButtonColors
        {
            NormalBg = bg,
            NormalFg = fg,
            HoverBg = hoverBg,
            NormalBorder = border,
            Glow = glow,
        };
        btn.Tag = colors;
        btn.MouseEnter += (_, _) => { if (btn.IsEnabled) btn.Background = colors.HoverBg; };
        btn.MouseLeave += (_, _) => { if (btn.IsEnabled) btn.Background = colors.NormalBg; };
        if (glow is not null) ApplyClearTypeCacheFix(btn);
        return btn;
    }

    /// <summary>A WPF Effect (DropShadowEffect included) forces the element it
    /// sits on - text and all, since Button.Effect renders the button's whole
    /// visual through one intermediate bitmap - through a cache that defaults
    /// to 96 DPI with ClearType off. On this machine's 150% display scale
    /// that softened the CTA buttons' own label, which read as "blurry text",
    /// even though the label itself was never touched. The documented fix is
    /// a BitmapCache on the same element with RenderAtScale pinned to the
    /// button's real DPI and EnableClearType on, read once the button is
    /// actually in the visual tree (DPI is unknown before Loaded).</summary>
    private static void ApplyClearTypeCacheFix(Button btn)
    {
        btn.Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(btn);
            btn.CacheMode = new BitmapCache { RenderAtScale = dpi.DpiScaleX, EnableClearType = true };
        };
    }

    /// <summary>Icon + label content for a flat button. The icon's stroke is
    /// BOUND to the button's Foreground rather than assigned once, so it
    /// follows the label through every state change - in particular it greys
    /// out together with the text when SetButtonEnabled re-points Foreground,
    /// instead of staying at full contrast on a disabled button.</summary>
    private static UIElement BuildIconContent(Geometry icon, string text)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var path = Icons.Stroked(icon, Theme.FgBrush, 1.8);
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        stack.Children.Add(path);
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return stack;
    }

    /// <summary>A square, icon-only flat button (no label) - used for the
    /// language switcher in the header. Shares the same FlatTemplate/hover
    /// mechanics as <see cref="CreateFlatButton"/>, just without the
    /// icon+text StackPanel content that a labeled button builds.</summary>
    public static Button CreateIconButton(Geometry icon, Brush bg, Brush fg, Brush hoverBg, string accessibleName, double size = 36)
    {
        var btn = new Button
        {
            Width = size,
            Height = size,
            Background = bg,
            Foreground = fg,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Template = FlatTemplate,
            FocusVisualStyle = null,
        };
        var path = Icons.Stroked(icon, fg, 1.6);
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        btn.Content = path;
        System.Windows.Automation.AutomationProperties.SetName(btn, accessibleName);
        var colors = new ButtonColors { NormalBg = bg, NormalFg = fg, HoverBg = hoverBg };
        btn.Tag = colors;
        btn.MouseEnter += (_, _) => { if (btn.IsEnabled) btn.Background = colors.HoverBg; };
        btn.MouseLeave += (_, _) => { if (btn.IsEnabled) btn.Background = colors.NormalBg; };
        return btn;
    }

    /// <summary>Enables/disables a flat button, visibly greying it out and
    /// blocking clicks (used for START/STOP/Add files while a job runs).</summary>
    public static void SetButtonEnabled(Button btn, bool enabled)
    {
        var colors = (ButtonColors)btn.Tag;
        if (enabled)
        {
            btn.IsEnabled = true;
            btn.Background = colors.NormalBg;
            btn.Foreground = colors.NormalFg;
            btn.BorderBrush = colors.NormalBorder;
            btn.Effect = colors.Glow;
            btn.Cursor = Cursors.Hand;
        }
        else
        {
            btn.IsEnabled = false;
            btn.Background = Theme.DisabledBgBrush;
            btn.Foreground = Theme.DisabledFgBrush;
            // A disabled button keeps its outline (so the shape does not
            // collapse) but loses the accent glow entirely - a greyed-out
            // control still throwing a gold halo reads as enabled from
            // across the window, which is the one thing this state has to
            // communicate.
            btn.BorderBrush = colors.NormalBorder is null ? null : Theme.BorderBrush;
            btn.Effect = null;
            btn.Cursor = Cursors.Arrow;
        }
    }

    /// <summary>Re-points a button's "normal" colors (used by the preview
    /// player's Add-range button, which switches between an enabled accent
    /// look and a disabled grey look as the in/out selection changes).</summary>
    public static void UpdateButtonColors(Button btn, Brush bg, Brush fg, Brush hoverBg)
    {
        var colors = (ButtonColors)btn.Tag;
        colors.NormalBg = bg;
        colors.NormalFg = fg;
        colors.HoverBg = hoverBg;
        if (btn.IsEnabled) btn.Background = bg;
        btn.Foreground = fg;
    }

    /// <summary>Swaps the glyph on an icon+label button, for the ones whose
    /// icon is part of their state rather than decoration (the instruction
    /// panel's chevron, which points down when open and right when closed).
    /// Does nothing on a button built without an icon.</summary>
    public static void SetButtonIcon(Button btn, Geometry icon)
    {
        if (btn.Content is StackPanel stack
            && stack.Children.OfType<System.Windows.Shapes.Path>().FirstOrDefault() is { } path)
            path.Data = icon;
    }

    /// <summary>Re-labels a flat button, keeping its accessible name in step.
    /// An icon button's content is a StackPanel rather than a string, so the
    /// label lives in the TextBlock inside it - and a Button derives its UIA
    /// Name from its Content, which for that shape is nothing at all unless
    /// it is set explicitly (see gotcha #14).</summary>
    public static void SetButtonText(Button btn, string text)
    {
        if (btn.Content is StackPanel stack && stack.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
            label.Text = text;
        else
            btn.Content = text;
        System.Windows.Automation.AutomationProperties.SetName(btn, text);
    }
}
