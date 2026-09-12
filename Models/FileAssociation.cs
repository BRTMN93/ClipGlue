using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClipGlue.Models;

/// <summary>
/// Registers .clipglue as a file type opened by this app, so double-clicking
/// a project file in Explorer (or picking "Open"/"Open with" from its
/// context menu) launches ClipGlue with it instead of Windows asking how to
/// open it. Per-user only (HKEY_CURRENT_USER\Software\Classes): that needs
/// no admin elevation, and on Windows 10+ it shadows the machine-wide
/// HKEY_CLASSES_ROOT registration for the current user - all a single-user,
/// installer-less app needs.
///
/// The main window's settings panel exposes <see cref="Unregister"/> /
/// <see cref="Reregister"/> as one toggling button, so a user who removes
/// the association gets to keep it removed: the "enabled" flag persisted
/// here is what <see cref="EnsureRegistered"/> checks on every subsequent
/// startup, otherwise the app would silently put the registry keys straight
/// back the next time it opens.
/// </summary>
public static class FileAssociation
{
    private const string ProgId = "ClipGlue.Project";
    private const string Extension = ".clipglue";
    private const string EnabledConfigKey = "file_association_enabled";

    /// <summary>Registers (or refreshes) the association, unless the user
    /// has explicitly removed it via <see cref="Unregister"/> - see
    /// <see cref="IsEnabled"/>. Call once at startup, before the first
    /// window is shown.</summary>
    public static void EnsureRegistered()
    {
        if (IsEnabled()) Register();
    }

    /// <summary>Whether the association is meant to be registered, per the
    /// user's last choice in the settings panel - defaults to true (the
    /// association is on by default) when nothing has been recorded yet.</summary>
    public static bool IsEnabled() => ConfigStore.LoadFlag(EnabledConfigKey, true);

    /// <summary>Whether the registry actually maps .clipglue to this app
    /// right now - read fresh from the registry (not from the persisted
    /// "enabled" flag) so the settings panel reflects reality even if
    /// something outside this app touched the key.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Extension}");
            return key?.GetValue(null) as string == ProgId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the .clipglue association from the registry and
    /// records that choice so it sticks across restarts. Never throws - the
    /// keys might already be gone, or the environment might not allow
    /// writing to HKCU\Software\Classes at all.</summary>
    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Extension}", throwOnMissingSubKey: false);
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
        SetEnabled(false);
    }

    /// <summary>Puts the association back after <see cref="Unregister"/> -
    /// clears the persisted "disabled" choice and writes the registry keys
    /// immediately, rather than waiting for the next startup.</summary>
    public static void Reregister()
    {
        SetEnabled(true);
        Register();
    }

    private static void SetEnabled(bool enabled) => ConfigStore.Save((EnabledConfigKey, enabled ? "1" : "0"));

    /// <summary>
    /// Idempotent - each value is compared against what's already on record
    /// before writing, so a normal startup that finds everything current
    /// touches the registry not at all. Re-registers when the exe has moved
    /// (a rebuild landed in a different bin\ folder, or the published .exe
    /// was moved/renamed), which is also why EnsureRegistered runs this on
    /// every startup rather than once ever. Never throws - a locked-down
    /// environment where HKCU\Software\Classes can't be written should not
    /// stop the app from opening; it just won't get the double-click
    /// association.
    /// </summary>
    private static void Register()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

            bool changed = false;
            changed |= SetValueIfDifferent($@"Software\Classes\{Extension}", null, ProgId);
            changed |= SetValueIfDifferent($@"Software\Classes\{ProgId}", null, "ClipGlue Project");
            changed |= SetValueIfDifferent($@"Software\Classes\{ProgId}\DefaultIcon", null, $"\"{exePath}\",0");
            changed |= SetValueIfDifferent($@"Software\Classes\{ProgId}\shell\open\command", null, $"\"{exePath}\" \"%1\"");

            // Explorer caches icon/handler associations; without this a
            // freshly-changed registration only takes effect after sign-out.
            if (changed) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    private static bool SetValueIfDifferent(string keyPath, string? name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (key.GetValue(name) as string == value) return false;
        key.SetValue(name, value);
        return true;
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const int SHCNF_IDLIST = 0;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
