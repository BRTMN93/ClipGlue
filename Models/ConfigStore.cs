using System.IO;
using System.Text.Json;

namespace ClipGlue.Models;

/// <summary>
/// Small persisted user config (last-used folders) - a flat JSON file
/// under %APPDATA%, not bundled with the app. Direct port of
/// clipglue/config.py.
/// </summary>
public static class ConfigStore
{
    private const string ConfigDirName = "ClipGlue";
    private const string ConfigFileName = "config.json";

    private static string ConfigPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(baseDir, ConfigDirName, ConfigFileName);
    }

    private static Dictionary<string, string> LoadOrThrow(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    /// <summary>Returns the persisted config, or an empty dict if none exists
    /// yet or it can't be read (corrupt file, permissions) - never throws.</summary>
    public static Dictionary<string, string> Load()
    {
        try { return LoadOrThrow(ConfigPath()); }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>Reads a single "0"/"1" flag under <paramref name="key"/>,
    /// or <paramref name="defaultValue"/> if it has never been set. Shared by
    /// every persisted on/off toggle in the app (<see cref="AppSettings"/>,
    /// <see cref="FileAssociation"/>) instead of each re-deriving the same
    /// TryGetValue-and-compare.</summary>
    public static bool LoadFlag(string key, bool defaultValue)
    {
        var config = Load();
        return config.TryGetValue(key, out var v) ? v == "1" : defaultValue;
    }

    /// <summary>Merges the given key/value pairs into the persisted config and
    /// writes it back. Silently does nothing on failure - remembering the
    /// last folder is a convenience, not something worth crashing over.</summary>
    public static void Save(params (string Key, string Value)[] kv)
    {
        var path = ConfigPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch
        {
            return;
        }

        // Loaded separately from the public Load() above (which is meant to
        // swallow a missing/corrupt file and hand back an empty dict for
        // ordinary reads). Here that same "empty dict" fallback would be
        // disastrous: merging a couple of new keys into an empty dict and
        // writing it back is exactly what silently erased every remembered
        // setting - language, every recent folder, the .clipglue file
        // association - from a single unreadable read (a real risk given
        // this file is shared with the Python build, where any non-string
        // JSON value makes Deserialize<Dictionary<string,string>> throw; see
        // pitfall #2 in CLAUDE.md and N8 in AUDIT_TODO.md). So: a genuinely
        // missing file (first run) starts from empty as before, but a file
        // that exists and fails to read/parse is preserved as a .bak instead
        // of being clobbered, and the save still proceeds from empty so the
        // keys being set right now aren't lost either.
        Dictionary<string, string> data;
        if (!File.Exists(path))
        {
            data = new Dictionary<string, string>();
        }
        else
        {
            try
            {
                data = LoadOrThrow(path);
            }
            catch
            {
                try { File.Copy(path, path + ".bak", overwrite: true); } catch { /* best-effort */ }
                data = new Dictionary<string, string>();
            }
        }

        foreach (var (key, value) in kv)
            data[key] = value;

        try
        {
            var json = JsonSerializer.Serialize(data);
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, json);
            if (File.Exists(path))
                File.Replace(tmpPath, path, null);
            else
                File.Move(tmpPath, path);
        }
        catch
        {
            // Best-effort only.
        }
    }
}
