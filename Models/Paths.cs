using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipGlue.Models;

/// <summary>
/// Locating bundled resources (ffmpeg / ffprobe / icon). Port of
/// clipglue/paths.py - simpler here than the Python original because a
/// normal .NET build/publish always puts these files next to the .exe
/// (AppContext.BaseDirectory), with no PyInstaller-style "unpacked to a
/// temp _MEIPASS at runtime" distinction to account for.
///
/// The one exception: the self-contained single-file publish profile
/// (Properties/PublishProfiles/win-x64.pubxml) is meant to ship ClipGlue as
/// exactly one .exe with nothing loose next to it, so ffmpeg.exe/
/// ffprobe.exe/clipglue.ico are also embedded as resources in
/// ClipGlue.csproj and extracted here to a per-user cache folder under
/// %LOCALAPPDATA% the first time they're actually needed. A normal
/// `dotnet build`/dev run always finds them sitting right next to the exe
/// (still copied there via CopyToOutputDirectory) and never touches the
/// embedded copies or the cache folder at all - extraction only kicks in
/// when the loose file isn't there.
/// </summary>
public static class Paths
{
    private const string FfmpegLogicalName = "ClipGlue.EmbeddedTools.ffmpeg.exe";
    private const string FfprobeLogicalName = "ClipGlue.EmbeddedTools.ffprobe.exe";
    private const string IconLogicalName = "ClipGlue.EmbeddedTools.clipglue.ico";

    public static string BaseDir() => AppContext.BaseDirectory;

    /// <summary>Resolves ffmpeg/ffprobe to a real path on disk: next to the
    /// exe if it's there (dev build / non-single-file publish), otherwise
    /// extracted from the embedded copy into a per-user cache folder (single
    /// -file publish), otherwise just the bare name so Process.Start still
    /// has a chance via PATH.</summary>
    public static string FindTool(string name)
    {
        var exeName = name + ".exe";
        var candidate = Path.Combine(BaseDir(), exeName);
        if (File.Exists(candidate)) return candidate;

        var logicalName = name switch
        {
            "ffmpeg" => FfmpegLogicalName,
            "ffprobe" => FfprobeLogicalName,
            _ => null,
        };
        var extracted = logicalName is not null ? ExtractEmbeddedFile(exeName, logicalName) : null;
        return extracted ?? exeName; // rely on PATH
    }

    /// <summary>Window icon: loaded straight from the loose .ico next to the
    /// exe when present (dev build), otherwise decoded directly from the
    /// embedded resource stream - no extraction to disk needed for this one,
    /// since WPF can build a BitmapFrame from any stream.
    /// BitmapCacheOption.OnLoad forces the image to be fully read before the
    /// manifest-resource stream (scoped to this method) goes away.
    ///
    /// Picks the LARGEST frame in the .ico, not frame 0 - clipglue.ico
    /// carries several resolutions and their on-disk order isn't guaranteed
    /// to be smallest-first. Window.Icon (and the custom title bar's badge,
    /// see Controls/TitleBar.cs) both just take a single BitmapFrame and
    /// scale it down as needed; scaling a small frame UP to fill the 150%
    /// -DPI title bar badge was what read as a blurry icon, whereas scaling
    /// a large one down stays crisp.</summary>
    public static ImageSource? LoadAppIcon()
    {
        try
        {
            var candidate = Path.Combine(BaseDir(), "clipglue.ico");
            if (File.Exists(candidate))
                return LargestFrame(BitmapDecoder.Create(new Uri(candidate), BitmapCreateOptions.None, BitmapCacheOption.OnLoad));

            using var res = typeof(Paths).Assembly.GetManifestResourceStream(IconLogicalName);
            return res is null ? null : LargestFrame(BitmapDecoder.Create(res, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
        }
        catch
        {
            return null; // cosmetic only
        }
    }

    private static ImageSource? LargestFrame(BitmapDecoder decoder)
        => decoder.Frames.Count == 0 ? null : decoder.Frames.OrderByDescending(f => f.PixelWidth).First();

    /// <summary>Writes an embedded resource out to
    /// %LOCALAPPDATA%\ClipGlue\bin\&lt;fileName&gt; and returns that path,
    /// skipping the write if a same-size copy is already cached there (so a
    /// second/third launch doesn't re-extract ~20MB every time). Best-effort:
    /// returns null on any I/O failure (e.g. a concurrent second instance
    /// mid-extraction), letting the caller fall back to PATH instead of
    /// crashing the app over a caching optimization.</summary>
    private static string? ExtractEmbeddedFile(string fileName, string logicalName)
    {
        try
        {
            using var res = typeof(Paths).Assembly.GetManifestResourceStream(logicalName);
            if (res is null) return null;

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClipGlue", "bin");
            Directory.CreateDirectory(cacheDir);
            var outPath = Path.Combine(cacheDir, fileName);

            bool needsWrite = true;
            if (File.Exists(outPath))
            {
                try { needsWrite = new FileInfo(outPath).Length != res.Length; }
                catch (IOException) { needsWrite = true; }
            }
            if (needsWrite)
            {
                using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
                res.CopyTo(fs);
            }
            return outPath;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
