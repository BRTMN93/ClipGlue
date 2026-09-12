namespace ClipGlue.Models;

/// <summary>
/// Small persisted app-wide toggles that aren't per-project state, stored in
/// the same shared <c>%APPDATA%\ClipGlue\config.json</c> as everything else
/// in <see cref="ConfigStore"/>. Currently just the verbose-ffmpeg-logging
/// switch added at Bartek's request (2026-09-06): off by default, since the
/// full command line + stderr of every ffmpeg call is only useful while
/// tracking down a specific problem, not during normal use.
/// </summary>
public static class AppSettings
{
    private const string VerboseLoggingConfigKey = "verbose_logging";
    private const string SegmentStripVisibleConfigKey = "segment_strip_visible";
    private const string DetailedProgressViewConfigKey = "detailed_progress_view";

    public static bool VerboseLogging { get; private set; } = ConfigStore.LoadFlag(VerboseLoggingConfigKey, false);

    /// <summary>Whether the project preview's kept/cut strip (the bar under
    /// the seek bar, showing each source file's surviving ranges) is shown.
    /// On by default; turning it off frees the vertical space it and its
    /// legend occupied for the video itself.</summary>
    public static bool SegmentStripVisible { get; private set; } = ConfigStore.LoadFlag(SegmentStripVisibleConfigKey, true);

    /// <summary>Which of the two progress views MainWindow shows while a job
    /// runs: off (default) is the minimal single-bar status strip at the
    /// bottom of the window; on is the full card (both bars, the current
    /// file name, CPU/memory) in the action bar. Off by default because the
    /// minimal strip is what most runs need - the detailed card is for when
    /// something needs a closer look.</summary>
    public static bool DetailedProgressView { get; private set; } = ConfigStore.LoadFlag(DetailedProgressViewConfigKey, false);

    private static void SetFlag(string key, bool value, bool current, Action<bool> apply)
    {
        if (current == value) return;
        apply(value);
        ConfigStore.Save((key, value ? "1" : "0"));
    }

    public static void SetVerboseLogging(bool enabled) =>
        SetFlag(VerboseLoggingConfigKey, enabled, VerboseLogging, v => VerboseLogging = v);

    public static void SetSegmentStripVisible(bool visible) =>
        SetFlag(SegmentStripVisibleConfigKey, visible, SegmentStripVisible, v => SegmentStripVisible = v);

    public static void SetDetailedProgressView(bool enabled) =>
        SetFlag(DetailedProgressViewConfigKey, enabled, DetailedProgressView, v => DetailedProgressView = v);
}
