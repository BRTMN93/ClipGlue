using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using ClipGlue.Models;

namespace ClipGlue;

/// <summary>
/// Entry point. The Python original needed a manual DPI-awareness shim
/// here (SetProcessDpiAwareness via ctypes) because Tkinter apps aren't
/// DPI-aware by default on Windows; .NET WPF apps are Per-Monitor-V2 DPI
/// aware out of the box, so that step simply doesn't exist here.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        Loc.Init();
        FileAssociation.EnsureRegistered();

        var window = new MainWindow();
        window.Show();

        // Double-clicking a .clipglue file (or "Open"/"Open with" from its
        // Explorer context menu) launches ClipGlue.exe with the file's path
        // as the one command-line argument - see FileAssociation for the
        // registration that makes that happen.
        var projectPath = e.Args.FirstOrDefault(a => string.Equals(Path.GetExtension(a), ".clipglue", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (projectPath != null) window.LoadProjectFromPath(projectPath);
    }

    /// <summary>
    /// A packaged, windowed .exe has no console to print a crash traceback
    /// to - the Python original only ever logged worker-thread exceptions
    /// (into its own console box) for exactly that reason, leaving any bug
    /// in UI-thread code to fail as a bare, undiagnosable silent exit.
    /// Surfacing the full exception here instead is a deliberate
    /// improvement: it turns a report of "it just closed" into an actual
    /// stack trace to act on.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "An unexpected error occurred:\n\n" + e.Exception,
            "ClipGlue - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
