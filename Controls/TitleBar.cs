using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace ClipGlue.Controls;

/// <summary>
/// The custom title bar that replaces the system one once a window switches
/// to WindowStyle.None + WindowChrome (see NativeChrome and
/// Theme.WindowRadius): the icon badge, "ClipGlue" plus its tagline, and the
/// three caption buttons, matching Bartek's approved mockup.
///
/// Everything left of the buttons is deliberately left alone as far as
/// WindowChrome is concerned - its default WindowChrome.
/// IsHitTestVisibleInChrome is false, and that's exactly what turns that
/// whole strip into a caption region for free: drag-to-move,
/// double-click-to-maximize and Aero Snap all come from WindowChrome itself,
/// none of them wired up by hand here. Only the three buttons opt back into
/// normal hit-testing, or every click on them would be swallowed as a drag
/// gesture instead of reaching the Button.
/// </summary>
public static class TitleBar
{
    public const double Height = Theme.TitleBarHeight;

    public sealed class Handles
    {
        public required Button Minimize { get; init; }
        public required Button MaximizeRestore { get; init; }
        public required Button Close { get; init; }
        public required Path MaximizeRestoreIcon { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Subtitle { get; init; }
    }

    public static (UIElement Element, Handles Handles) Build(Window window)
    {
        var bar = new Grid { Height = Height, Background = Theme.BgWindowBrush };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(Theme.PagePad, 0, 0, 0),
        };

        var iconBadge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (window.Icon is ImageSource iconSource)
        {
            var iconImage = new Image { Source = iconSource, Stretch = Stretch.Uniform };
            // Paths.LoadAppIcon already picks the .ico's largest frame, so
            // this is always a downscale - HighQuality (a linear/anti-alias
            // filter) instead of WPF's default NearestNeighbor-ish choice
            // is what keeps that downscale crisp rather than aliased.
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
            iconBadge.Child = iconImage;
        }
        left.Children.Add(iconBadge);

        var textStack = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Foreground = Theme.TextHiBrush,
            FontFamily = Theme.HeadFontFamily,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
        };
        var subtitle = new TextBlock
        {
            Foreground = Theme.TextMidBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = 10.5,
            Margin = new Thickness(0, 1, 0, 0),
        };
        textStack.Children.Add(title);
        textStack.Children.Add(subtitle);
        left.Children.Add(textStack);

        Grid.SetColumn(left, 0);
        bar.Children.Add(left);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var (minimize, _) = CaptionButton(Icons.Minus, "Minimize", danger: false);
        var (maximizeRestore, maximizeIcon) = CaptionButton(Icons.Maximize, "Maximize", danger: false);
        var (close, _) = CaptionButton(Icons.CloseSmall, "Close", danger: true);

        minimize.Click += (_, _) => window.WindowState = WindowState.Minimized;
        maximizeRestore.Click += (_, _) =>
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
        close.Click += (_, _) => window.Close();

        buttons.Children.Add(minimize);
        buttons.Children.Add(maximizeRestore);
        buttons.Children.Add(close);
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);

        // The one hairline the mockup draws under the whole bar - the same
        // separator treatment used elsewhere in the app (see MainWindow's
        // header/toolbar rule), not a WindowChrome affordance.
        var sep = new Border { Height = 1, Background = Theme.BorderSoftBrush, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumnSpan(sep, 2);
        bar.Children.Add(sep);

        var handles = new Handles
        {
            Minimize = minimize,
            MaximizeRestore = maximizeRestore,
            Close = close,
            MaximizeRestoreIcon = maximizeIcon,
            Title = title,
            Subtitle = subtitle,
        };
        RefreshMaximizeGlyph(handles, window.WindowState);
        return (bar, handles);
    }

    /// <summary>Swaps the maximize button's glyph between the square
    /// (maximize) and the overlapping-squares (restore) shape. Called once
    /// at build time and again on every WindowState change - MainWindow's
    /// existing StateChanged handler is the caller for that second case.</summary>
    public static void RefreshMaximizeGlyph(Handles handles, WindowState state)
    {
        bool maximized = state == WindowState.Maximized;
        handles.MaximizeRestoreIcon.Data = maximized ? Icons.Restore : Icons.Maximize;
        System.Windows.Automation.AutomationProperties.SetName(
            handles.MaximizeRestore, maximized ? "Restore" : "Maximize");
    }

    private static readonly ControlTemplate CaptionTemplate = BuildCaptionTemplate();

    /// <summary>No corner radius, unlike the app's usual FlatTemplate - the
    /// window's own top corners are rounded by the OS (see NativeChrome),
    /// which clips a plain rectangular button for free exactly the way a
    /// native title bar's own caption buttons are clipped.</summary>
    private static ControlTemplate BuildCaptionTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background")
        {
            RelativeSource = RelativeSource.TemplatedParent,
        });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;
        return template;
    }

    /// <summary>One caption button, flat until hovered (close turns solid
    /// red like every other Windows title bar's close button; minimize/
    /// maximize just lighten), and marked hit-test-visible so WindowChrome
    /// routes clicks to it instead of treating the press as a drag/maximize
    /// gesture on the caption region behind it.</summary>
    private static (Button Button, Path Icon) CaptionButton(Geometry icon, string accessibleName, bool danger)
    {
        var btn = new Button
        {
            Width = 46,
            Height = Height,
            Background = Theme.TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = Cursors.Arrow,
            Template = CaptionTemplate,
            FocusVisualStyle = null,
        };
        var path = Icons.Stroked(icon, Theme.TextMidBrush, 1.4);
        btn.Content = path;
        System.Windows.Automation.AutomationProperties.SetName(btn, accessibleName);

        var hoverBg = danger ? Theme.RedBrush : Theme.BgCardHl2Brush;
        btn.MouseEnter += (_, _) => { btn.Background = hoverBg; path.Stroke = Theme.TextHiBrush; };
        btn.MouseLeave += (_, _) => { btn.Background = Theme.TransparentBrush; path.Stroke = Theme.TextMidBrush; };

        WindowChrome.SetIsHitTestVisibleInChrome(btn, true);
        return (btn, path);
    }
}
