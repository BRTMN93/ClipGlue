using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipGlue.Engine;

/// <summary>One point-in-time reading of the currently running job's stats,
/// as shown in MainWindow's stats panel. <see cref="HasData"/> gates
/// Speed/Cpu/Memory - false until at least one ffmpeg process has actually
/// been sampled (e.g. during the analyze step, before any encode/copy has
/// started). <see cref="Eta"/> is gated independently: it stays null until
/// <see cref="JobStatsTracker.SetTotalWork"/> has been called (i.e. until
/// the whole job's cost plan is known, right after the analyze/probe loop
/// finishes) and at least one weighted step has completed since.</summary>
public readonly record struct JobStatsSnapshot(double SpeedMbps, TimeSpan? Eta, double AvgCpuPercent, double AvgMemoryMb, bool HasData);

/// <summary>
/// Samples the currently running ffmpeg child process's write throughput,
/// CPU and memory use while a job is cutting/joining, and estimates an ETA
/// from real per-step timing, weighted by each step's estimated cost (see
/// <see cref="ReportStepCompleted"/>) rather than counted flat.
///
/// <see cref="Sample"/> is called from inside FfmpegEngine.RunFfmpeg's
/// existing ~150ms tick loop - no separate polling thread. MainWindow reads
/// <see cref="Snapshot"/> from the same cadence (via MakeTicker, on the
/// background worker thread) and marshals the result to the UI thread the
/// same way it already does for the progress bars.
///
/// One instance covers exactly one job (MainWindow.ProcessOne creates a
/// fresh tracker per job): throughput/CPU/memory reset to zero between
/// files, since there is no single meaningful "speed" across unrelated
/// ffmpeg invocations working on different inputs, and an ETA average built
/// from one job's steps has no business surviving into the next job's
/// completely different file sizes.
/// </summary>
public sealed class JobStatsTracker
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private DateTime? _lastSampleUtc;
    private long _lastWriteBytes;
    private TimeSpan _lastCpuTime;

    private double _speedMbpsEma;
    private bool _hasSpeedSample;

    private double _cpuPercentSum;
    private int _cpuSampleCount;
    private double _memoryMbSum;
    private int _memSampleCount;

    // ETA bookkeeping - see ReportStepCompleted for why this replaced an
    // earlier version driven by MakeTicker's per-tick cosmetic percentage,
    // and SetTotalWork for why steps are weighted rather than counted.
    private double _weightCompleted;
    private double? _totalWeight;
    private double _estimatedRemainingSecondsAtBoundary;
    private double _boundaryElapsedSeconds;
    private bool _hasEtaEstimate;

    public JobStatsTracker()
    {
    }

    /// <summary>Called from RunFfmpeg's tick loop, on whatever background
    /// thread is running the job. Never throws - a process that exited
    /// between the loop's exit check and this call, or one running under a
    /// security context that denies GetProcessIoCounters, just contributes
    /// no sample instead of taking the job down with it.</summary>
    public void Sample(Process proc)
    {
        try
        {
            // Process caches TotalProcessorTime/WorkingSet64 after first
            // read on Windows - without this, every sample after the first
            // would report the exact same stale numbers.
            proc.Refresh();
            var now = DateTime.UtcNow;
            var cpuTime = proc.TotalProcessorTime;
            var memoryMb = proc.WorkingSet64 / 1_000_000.0;
            bool haveIo = GetProcessIoCounters(proc.Handle, out var counters);
            long writeBytes = haveIo ? (long)counters.WriteTransferCount : 0;

            lock (_lock)
            {
                _memoryMbSum += memoryMb;
                _memSampleCount++;

                if (_lastSampleUtc is { } last)
                {
                    double dtSeconds = (now - last).TotalSeconds;
                    if (dtSeconds > 0.01)
                    {
                        double cpuPercent = Math.Max(0,
                            (cpuTime - _lastCpuTime).TotalSeconds / dtSeconds / Environment.ProcessorCount * 100.0);
                        _cpuPercentSum += cpuPercent;
                        _cpuSampleCount++;

                        if (haveIo)
                        {
                            double deltaBytes = Math.Max(0, writeBytes - _lastWriteBytes);
                            double instantMbps = deltaBytes * 8.0 / 1_000_000.0 / dtSeconds;
                            // EMA rather than the raw instantaneous reading:
                            // file writes are bursty (buffered, flushed in
                            // chunks), so back-to-back 150ms samples swing
                            // between "0" and a brief spike - smoothing
                            // keeps the displayed number from flickering.
                            _speedMbpsEma = _hasSpeedSample ? _speedMbpsEma * 0.7 + instantMbps * 0.3 : instantMbps;
                            _hasSpeedSample = true;
                        }
                    }
                }

                _lastSampleUtc = now;
                _lastCpuTime = cpuTime;
                _lastWriteBytes = writeBytes;
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Tells the tracker the job's full cost plan: <paramref name="plannedWork"/>
    /// is the estimated combined weight (see FfmpegEngine.EstimateCutSeconds)
    /// of every step from here to the end of the job that hasn't completed
    /// yet - i.e. every remaining cut plus the final join. Call this once,
    /// from ProcessOne, right after the analyze/probe loop finishes: that is
    /// the earliest point where every file's keyframes and video/audio
    /// compatibility are known, so it's the earliest point a real plan can
    /// be built.
    ///
    /// Before this is called, ReportStepCompleted still accumulates weight
    /// (from the probe steps, whose own duration-based weight IS known as
    /// each probe finishes) but withholds an ETA rather than guess against
    /// an unknown total - see the class doc's note on why Eta is gated
    /// independently of HasData.
    /// </summary>
    public void SetTotalWork(double plannedWork)
    {
        lock (_lock)
        {
            _totalWeight = _weightCompleted + Math.Max(0, plannedWork);
        }
    }

    /// <summary>
    /// Marks one whole step (one file probed, one segment cut, or the final
    /// join) as genuinely finished - call this once per real step boundary,
    /// the same boundaries MainWindow.ProcessOne's own NextRange() already
    /// hands out for the progress bar. <paramref name="weight"/> is that
    /// step's estimated cost (FfmpegEngine.EstimateCutSeconds for a cut,
    /// duration * ProbeSecondsPerContentSecond for a probe, duration *
    /// CopyBothSecondsPerContentSecond for the join) - NOT a flat 1 per
    /// step, because real steps in a job vary by an order of magnitude or
    /// more (a lossless-copy segment vs. one needing a full re-encode), and
    /// averaging elapsed time over a flat step count badly misjudges the
    /// remaining time whenever the steps left differ in kind from the ones
    /// already done - e.g. it drastically underestimates the ETA right
    /// before a heavy re-encode segment if everything counted so far was a
    /// cheap copy.
    ///
    /// This - not MakeTicker's per-tick cosmetic percentage - is what ETA is
    /// built from now. The original version fed MakeTicker's
    /// startPct + (endPct-startPct)*frac straight into the ETA formula, where
    /// frac = 1 - 1/(1 + n*0.6) is a hyperbolic curve that exists purely to
    /// ease the progress BAR toward 99% and has no relationship to how long
    /// the step actually takes: it reaches ~99% of ITS OWN span after only
    /// about ten ticks (~1.5s) regardless of whether the real step takes one
    /// second or one minute. For a slow step, that pinned the ETA formula's
    /// only moving input elapsed / (pct/100) is bound to grow while pct sits
    /// frozen, so the estimate climbed roughly linearly with elapsed time
    /// for as long as the step ran - reported by Bartek as the ETA
    /// "freezing" then "counting up" - and then jumped hard the instant the
    /// step's true end percentage landed. Real per-step boundaries have
    /// none of that: they only move forward when actual work finishes.
    /// </summary>
    public void ReportStepCompleted(double weight)
    {
        lock (_lock)
        {
            _weightCompleted += Math.Max(0, weight);
            double elapsed = _clock.Elapsed.TotalSeconds;

            if (_totalWeight is { } totalWeight)
            {
                double remainingWeight = totalWeight - _weightCompleted;
                if (remainingWeight > 0 && _weightCompleted > 0)
                {
                    double avgSecondsPerWeight = elapsed / _weightCompleted;
                    _estimatedRemainingSecondsAtBoundary = avgSecondsPerWeight * remainingWeight;
                    _hasEtaEstimate = true;
                }
                else
                {
                    _hasEtaEstimate = false;
                }
            }
            else
            {
                _hasEtaEstimate = false;
            }
            _boundaryElapsedSeconds = elapsed;
        }
    }

    /// <summary>Reads the current running averages plus an ETA that counts
    /// down in real time from the estimate <see cref="ReportStepCompleted"/>
    /// last computed - between boundaries this only ever subtracts real
    /// elapsed seconds, so it cannot free-run upward or stall the way the
    /// old percentage-driven version did.</summary>
    public JobStatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            bool hasData = _memSampleCount > 0;
            double avgCpu = _cpuSampleCount > 0 ? _cpuPercentSum / _cpuSampleCount : 0;
            double avgMem = _memSampleCount > 0 ? _memoryMbSum / _memSampleCount : 0;

            TimeSpan? eta = null;
            if (_hasEtaEstimate)
            {
                double sinceBoundary = _clock.Elapsed.TotalSeconds - _boundaryElapsedSeconds;
                double remaining = Math.Max(0, _estimatedRemainingSecondsAtBoundary - sinceBoundary);
                eta = TimeSpan.FromSeconds(remaining);
            }

            return new JobStatsSnapshot(_speedMbpsEma, eta, avgCpu, avgMem, hasData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);
}
