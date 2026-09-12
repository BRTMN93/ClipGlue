using ClipGlue.Engine;

namespace ClipGlue.Models;

/// <summary>
/// One shared, process-wide store of "how long is this file", filled by
/// ffprobe in the background.
///
/// Two places need it now and neither should pay for it twice: the file row
/// shows the duration next to the name, and the project preview's kept/cut
/// strip needs each file's real extent so a trailing cut is visible. Before
/// this existed the preview probed on its own and the row had no duration at
/// all; a per-path cache means adding twelve files costs twelve probes once,
/// not once per consumer per reload.
///
/// <para><b>Thread-safety is explicit here, and deliberately so.</b> An
/// earlier version awaited with the default context capture and let callers
/// update their controls straight from the continuation. That works only
/// while a SynchronizationContext is installed on the calling thread - which
/// it is not before <c>Application.Run</c> starts pumping, so the
/// continuation resumed on a thread-pool worker and touching a control from
/// it threw "the calling thread cannot access this object". Every await here
/// now runs <c>ConfigureAwait(false)</c> and callers marshal their own UI
/// work through their control's Dispatcher, which cannot depend on ambient
/// state. The dictionaries are locked for the same reason: several rows can
/// be probing at once.</para>
///
/// A path that fails to probe is NOT cached, so a file that becomes readable
/// later (a slow network share) is retried rather than remembered as broken.
/// </summary>
public static class DurationCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, double> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cached duration in seconds, or 0 when it is not known
    /// yet. Never blocks on ffprobe and never starts one.</summary>
    public static double Get(string path)
    {
        lock (Gate) return Known.TryGetValue(path, out var d) ? d : 0;
    }

    /// <summary>
    /// Probes whatever is still unknown among <paramref name="paths"/> and
    /// returns true if anything new was learned, so the caller can decide
    /// whether a repaint is warranted. Paths already known, or already being
    /// probed by another caller, are skipped.
    ///
    /// Resumes on a thread-pool thread, NOT on the caller's context - see the
    /// class note. Marshal any UI work yourself.
    /// </summary>
    public static async Task<bool> RequestAsync(IEnumerable<string> paths)
    {
        var wanted = new List<string>();
        lock (Gate)
        {
            foreach (var path in paths)
            {
                if (Known.ContainsKey(path) || InFlight.Contains(path)) continue;
                InFlight.Add(path);
                wanted.Add(path);
            }
        }
        if (wanted.Count == 0) return false;

        try
        {
            var found = await Task.Run(() =>
            {
                var results = new List<(string Path, double Duration)>();
                foreach (var path in wanted)
                {
                    try
                    {
                        // pad:false - this is a display value, not a cut
                        // boundary, so it must not carry the safety padding
                        // the engine adds when it is about to seek.
                        var probe = FfmpegEngine.ProbeFull(path, pad: false);
                        if (probe.Duration > 0) results.Add((path, probe.Duration));
                    }
                    catch
                    {
                        // Left out of the cache on purpose - see the class note.
                    }
                }
                return results;
            }).ConfigureAwait(false);

            lock (Gate)
            {
                foreach (var (path, duration) in found) Known[path] = duration;
            }
            return found.Count > 0;
        }
        finally
        {
            lock (Gate)
            {
                foreach (var path in wanted) InFlight.Remove(path);
            }
        }
    }
}
