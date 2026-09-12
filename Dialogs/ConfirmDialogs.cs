using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipGlue.Dialogs;

/// <summary>
/// Every one of the app's center-screen popups - the STOP confirmation, the
/// job-finished success/error notices, and the general-purpose
/// warning/error/question messages that used to be raised as plain native
/// MessageBox.Show calls all over MainWindow and the Trim range window. Port
/// of confirm_dialog.py's module-level helpers, since extended past that
/// original scope: Bartek asked for every popup in the app to look like the
/// same window (see <see cref="ModalDialogWindow"/>'s own doc comment), which
/// meant routing all of them through here instead of leaving most of them as
/// OS message boxes.
/// </summary>
public static class ConfirmDialogs
{
    public static bool AskStopConfirm(Window owner, Action<bool> ownerDimmer, string title, string message,
        string confirmText = "STOP", string cancelText = "Cancel")
    {
        var dlg = new ModalDialogWindow(owner, title, message,
            new (string, object?, Brush, Brush, Brush)[]
            {
                (confirmText, true, Theme.RedBrush, Theme.DarkTextBrush, Theme.HoverRedBrush),
                (cancelText, false, Theme.BgRowBrush, Theme.FgBrush, Theme.BgRowHlBrush),
            },
            kind: DialogKind.Warning, ownerDimmer: ownerDimmer);
        return dlg.RunModal() is true;
    }

    /// <summary>General-purpose single-OK notice - the styled replacement for
    /// every plain <c>MessageBox.Show(..., MessageBoxButton.OK, ...)</c> call
    /// the app used to make (missing files, invalid ranges, save/load
    /// failures, and so on). <paramref name="kind"/> picks the badge; pass
    /// <see cref="DialogKind.Warning"/> for input-validation problems the user
    /// can fix and <see cref="DialogKind.Error"/> for failures that happened
    /// on their own (I/O, ffmpeg, a bad file).</summary>
    public static void ShowMessage(Window owner, Action<bool> ownerDimmer, DialogKind kind,
        string title, string message, string okText)
    {
        var dlg = new ModalDialogWindow(owner, title, message,
            new (string, object?, Brush, Brush, Brush)[]
            {
                (okText, null, Theme.FgAccentBrush, Theme.DarkTextBrush, Theme.HoverAccentBrush),
            },
            kind: kind, ownerDimmer: ownerDimmer);
        dlg.RunModal();
    }

    /// <summary>General-purpose Yes/No confirmation - the styled replacement
    /// for every plain <c>MessageBox.Show(..., MessageBoxButton.YesNo, ...)</c>
    /// call (clear queue, stop-and-quit, apply an empty range selection).
    /// Returns true only if the user picked <paramref name="yesText"/>.</summary>
    public static bool AskYesNo(Window owner, Action<bool> ownerDimmer, DialogKind kind,
        string title, string message, string yesText, string noText)
    {
        var dlg = new ModalDialogWindow(owner, title, message,
            new (string, object?, Brush, Brush, Brush)[]
            {
                (yesText, true, Theme.FgAccentBrush, Theme.DarkTextBrush, Theme.HoverAccentBrush),
                (noText, false, Theme.BgRowBrush, Theme.FgBrush, Theme.BgRowHlBrush),
            },
            kind: kind, ownerDimmer: ownerDimmer);
        return dlg.RunModal() is true;
    }

    /// <summary>Modal, single-OK "job finished" notice. Each output path is
    /// shown as a clickable link that opens its containing folder (with the
    /// file itself pre-selected) in Explorer.</summary>
    public static void ShowDone(Window owner, Action<bool> ownerDimmer, IReadOnlyList<string> outputPaths,
        string title = "Done", string singleLabel = "Output file saved:", string pluralLabel = "Output files saved:",
        string okText = "OK")
    {
        string labelText = outputPaths.Count == 1 ? singleLabel : pluralLabel;

        UIElement Body(ModalDialogWindow dlg, Panel pad, Brush messageFg)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = labelText,
                Foreground = messageFg,
                FontFamily = Theme.UiFontFamily,
                FontSize = Theme.UiFontSize,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 8),
            });
            foreach (var p in outputPaths)
            {
                var link = new TextBlock
                {
                    Text = p,
                    Foreground = Theme.FgLinkBrush,
                    FontFamily = Theme.UiFontFamily,
                    FontSize = Theme.UiFontSize,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320,
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                // The dialog is always on top, so leaving it open would just
                // bury the Explorer window it opens behind it - closing it
                // here is what actually lets the user see the folder.
                link.MouseLeftButtonDown += (_, _) => { OpenContainingFolder(p); dlg.CloseWith(null); };
                link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                link.MouseLeave += (_, _) => link.TextDecorations = null;
                stack.Children.Add(link);
            }
            stack.Children.Add(new Border { Height = 12 });
            return stack;
        }

        var dialog = new ModalDialogWindow(owner, title, null,
            new (string, object?, Brush, Brush, Brush)[]
            {
                (okText, null, Theme.FgAccentBrush, Theme.DarkTextBrush, Theme.HoverAccentBrush),
            },
            kind: DialogKind.Success, ownerDimmer: ownerDimmer, bodyBuilder: Body);
        dialog.RunModal();
    }

    /// <summary>Modal, single-OK notice for the job-failed case. Same panel
    /// as every other dialog now - only the red badge above the title marks
    /// it as a failure, instead of the whole panel turning red the way it
    /// used to (which made this dialog look like a different window from
    /// Done/Stop-confirm rather than the same one reporting something else).</summary>
    public static void ShowError(Window owner, Action<bool> ownerDimmer, string title, string message, string okText = "OK")
    {
        var dlg = new ModalDialogWindow(owner, title, message,
            new (string, object?, Brush, Brush, Brush)[]
            {
                (okText, null, Theme.FgAccentBrush, Theme.DarkTextBrush, Theme.HoverAccentBrush),
            },
            kind: DialogKind.Error, ownerDimmer: ownerDimmer);
        dlg.RunModal();
    }

    /// <summary>Opens the file's folder in Explorer with the file itself
    /// selected. Fire-and-forget: explorer.exe often exits nonzero even on
    /// success, and if the file's since been moved/deleted this just opens
    /// the folder with nothing selected instead of raising.</summary>
    private static void OpenContainingFolder(string path)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = false };
            psi.ArgumentList.Add($"/select,{Path.GetFullPath(path)}");
            // Process.Start's return can be null (reused an existing process
            // instance isn't a thing for explorer.exe specifically, but the
            // API still allows it in general) - `using` handles both that and
            // disposing the handle once explorer.exe has been launched. This
            // was previously left undisposed, per N15 in AUDIT_TODO.md.
            using var _ = Process.Start(psi);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            // Best-effort convenience shortcut only. ArgumentException
            // covers Path.GetFullPath (e.g. a path with characters invalid
            // for a full path), not just Process.Start.
        }
    }
}
