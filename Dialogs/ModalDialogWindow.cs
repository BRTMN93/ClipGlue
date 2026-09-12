using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipGlue.Dialogs;

/// <summary>
/// What kind of notice a <see cref="ModalDialogWindow"/> is raising - purely
/// a badge above the title, so every dialog keeps the exact same panel,
/// layout and button styling and only the badge (and the buttons/message the
/// caller supplies) changes with the action. Replaces the old approach of
/// painting the whole panel red for errors, which made the error dialog read
/// as a visually different window from Done/Stop-confirm instead of the same
/// one reporting something different - Bartek's ask when he flagged that all
/// of the app's center-screen popups should look like one window.</summary>
public enum DialogKind { Success, Warning, Error, Question }

/// <summary>
/// A borderless, centered notice/confirmation, styled like the rest of the
/// app instead of the native Windows messagebox - port of confirm_dialog.py's
/// _ModalDialog.
///
/// The Python original needed a second Toplevel as a dimming overlay plus
/// manual sign-aware geometry math and an owner &lt;Configure&gt; rebind, because
/// Tkinter's Toplevel has no built-in owner-centering, no real modality, and
/// a background window cannot legally raise itself above the foreground app
/// on Windows. A real WPF owned Window sidesteps all of that: ShowDialog()
/// gives genuine modality and keeps the dialog above its owner for free, and
/// WindowStartupLocation.CenterOwner centers it correctly even across
/// monitors with negative coordinates - so none of that plumbing exists
/// here. The dimmed (now blurred - see SetDimmed at each call site) backdrop
/// is instead a translucent overlay already living inside the owner's own
/// visual tree, toggled by <paramref name="ownerDimmer"/>.
/// </summary>
public sealed class ModalDialogWindow : Window
{
    /// <summary>Transparent margin around the panel, wide enough for
    /// <see cref="Theme.DialogShadow"/> to fade out inside the window's own
    /// bounds instead of clipping flat against its edge.</summary>
    private const double ShadowMargin = 40;

    public object? Result { get; private set; }
    private readonly object? _defaultValue;
    private readonly object? _escapeValue;
    private readonly Action<bool>? _ownerDimmer;

    public ModalDialogWindow(
        Window owner, string title, string? message,
        IReadOnlyList<(string Text, object? Value, Brush Bg, Brush Fg, Brush HoverBg)> buttons,
        DialogKind? kind = null,
        Func<ModalDialogWindow, Panel, Brush, UIElement?>? bodyBuilder = null,
        Action<bool>? ownerDimmer = null)
    {
        _ownerDimmer = ownerDimmer;

        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        // AllowsTransparency is what makes the rounded corners below actually
        // show as rounded against the desktop, rather than being papered over
        // by a square window rectangle filled in the same color - a plain
        // WindowStyle.None window is still a square surface underneath
        // whatever its Content draws. This is also why the panel needs a
        // margin around it (ShadowMargin below): the window's own bounds
        // still have to be big enough to give DropShadowEffect somewhere to
        // paint outside the panel's edge, or the shadow would clip flat
        // against the window edge exactly where it is meant to fade out.
        AllowsTransparency = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.Transparent;
        MinWidth = 320 + 2 * ShadowMargin;

        var outerBorder = new Border
        {
            BorderBrush = Theme.BorderCardBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.PanelRadius),
            // The brightest layer in the app's depth scale (see Theme.BgCardHl2's
            // own doc comment, which names dialogs explicitly) - not
            // Theme.Bg/BgWindow, the deepest one, which is what made this
            // panel read as flat: it barely stood out from the window behind
            // it instead of reading as the one thing floating above it.
            Background = Theme.BgCardHl2Brush,
            Effect = Theme.DialogShadow,
            Margin = new Thickness(ShadowMargin),
        };
        var pad = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        var badge = BuildKindBadge(kind);
        if (badge is not null) pad.Children.Add(badge);

        pad.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Theme.FgBrush,
            FontFamily = Theme.UiFontFamily,
            FontSize = Theme.HeadFontSize,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });

        if (bodyBuilder is not null)
        {
            var body = bodyBuilder(this, pad, Theme.FgDimBrush);
            if (body is not null) pad.Children.Add(body);
        }
        else
        {
            pad.Children.Add(new TextBlock
            {
                Text = message,
                Foreground = Theme.FgDimBrush,
                FontFamily = Theme.UiFontFamily,
                FontSize = Theme.UiFontSize,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                Margin = new Thickness(0, 10, 0, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Button? firstBtn = null;
        for (int i = 0; i < buttons.Count; i++)
        {
            var (text, value, bg, fg, hoverBg) = buttons[i];
            var btn = UiHelpers.CreateFlatButton(text, bg, fg, hoverBg);
            btn.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
            btn.Click += (_, _) => Finish(value);
            btnRow.Children.Add(btn);
            firstBtn ??= btn;
        }
        pad.Children.Add(btnRow);

        _defaultValue = buttons[0].Value;
        _escapeValue = buttons[^1].Value;

        // Same top-edge elevation highlight every card in the app draws
        // (Theme.CardTopHighlightBrush) - one more piece of the "look like
        // the rest of the app, not a different window" ask, on the one
        // surface that hadn't picked it up yet.
        var topHighlight = new Border
        {
            Background = Theme.CardTopHighlightBrush,
            Height = Theme.CardTopHighlightH,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Theme.CardTopHighlightInset, 0, Theme.CardTopHighlightInset, 0),
        };
        var panelBody = new Grid();
        panelBody.Children.Add(pad);
        panelBody.Children.Add(topHighlight);

        outerBorder.Child = panelBody;
        Content = outerBorder;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Finish(_escapeValue); e.Handled = true; }
            else if (e.Key == Key.Enter) { Finish(_defaultValue); e.Handled = true; }
        };

        Loaded += (_, _) =>
        {
            firstBtn?.Focus();
            // Every dialog raised through here centers on its owner, full
            // stop - no exceptions. The Stop-confirmation dialog used to
            // follow the same pin-to-top rule as the app's real windows
            // instead, and Bartek flagged that as visually inconsistent
            // with the Done/error notices sitting centered: one popup
            // stuck to the top edge while the others centered read as two
            // different kinds of window, not one. See WindowPlacement.
            // CenterOnOwner for why this still has to be computed by hand
            // rather than left to WindowStartupLocation.CenterOwner.
            WindowPlacement.CenterOnOwner(this);
        };
    }

    /// <summary>Closes the dialog as if the given button's value had been
    /// clicked - used by the "Done" dialog's clickable output-path links,
    /// which close the dialog as a side effect of opening the folder.</summary>
    public void CloseWith(object? value) => Finish(value);

    private void Finish(object? value)
    {
        Result = value;
        Close();
    }

    /// <summary>Shows the dialog modally and returns the value of whichever
    /// button (or Escape/Enter shortcut) closed it.</summary>
    public object? RunModal()
    {
        _ownerDimmer?.Invoke(true);
        // Flush the dispatcher at Render priority before handing off to
        // ShowDialog()'s own nested pump below - a bare Visibility flip only
        // QUEUES a repaint, and the call site that matters most here
        // (OnStartClicked, right after a real job finishes) fires a whole
        // batch of OTHER pending changes in the same tick first (re-enabling
        // every toolbar button, resetting both progress bars, clearing the
        // stats panel). Observed: an isolated repro with nothing else queued
        // dimmed correctly without this, but the owner showed up completely
        // undimmed behind the Done dialog after a real job - i.e. the
        // pending repaint was still sitting in the queue when the nested
        // modal loop took over. This forces it to land first, every time.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        try
        {
            ShowDialog();
        }
        finally
        {
            _ownerDimmer?.Invoke(false);
        }
        return Result;
    }

    /// <summary>The one visual element that tells dialogs apart: a filled
    /// circle with a glyph, colored by <paramref name="kind"/>, sitting above
    /// the title. Everything else about the dialog (panel, border, title
    /// style, button row) is identical regardless of kind.</summary>
    private static UIElement? BuildKindBadge(DialogKind? kind)
    {
        if (kind is null) return null;
        var (glyph, bg) = kind.Value switch
        {
            DialogKind.Success => ("✓", Theme.SuccessBrush),
            DialogKind.Warning => ("!", Theme.AccentGold500Brush),
            DialogKind.Error => ("✕", Theme.RedBrush),
            DialogKind.Question => ("?", Theme.AccentGold500Brush),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = bg,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = glyph,
                Foreground = Theme.DarkTextBrush,
                FontFamily = Theme.UiFontFamily,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
