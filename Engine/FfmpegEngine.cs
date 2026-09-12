using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipGlue.Models;

namespace ClipGlue.Engine;

/// <summary>
/// Video cut / join logic (ffmpeg). Direct port of clipglue/ffmpeg_engine.py.
///
/// DESIGN, in one paragraph
/// -------------------------
/// Never re-encode a stream unless the cut boundary or a genuine format
/// mismatch actually requires it, and when re-encoding is unavoidable, do it
/// with quality-appropriate settings (CRF, slow preset, matched to the
/// target codec) rather than a blanket "veryfast". All numeric formatting
/// here uses CultureInfo.InvariantCulture on purpose: ffmpeg only accepts
/// "."-decimal numbers in its arguments, and this app is developed and run
/// under a Polish (comma-decimal) locale where the default ToString() would
/// silently corrupt every -ss/-t/-crf argument.
/// </summary>
public static class FfmpegEngine
{
    /// <summary>Every visible processing step (analyze / cut / join) is padded
    /// to take at least this long, so the console never flashes past too
    /// fast to read. Real ffmpeg work that already takes longer than this is
    /// left untouched.</summary>
    public const double MinStepSeconds = 5.0;

    /// <summary>A boundary sliver shorter than this (seconds) isn't worth its
    /// own ffmpeg call - float/keyframe-timestamp noise, not a real
    /// re-encode need.</summary>
    public const double MinSliverSeconds = 0.04;

    // ETA cost-weighting constants - see EstimateCutSeconds below. These are
    // rough ratios of (wall-clock seconds spent) per (second of content),
    // ballparked from real ffmpeg runs (crf18/slow libx264 1080p): a genuine
    // video re-encode runs close to realtime, a stream-copy-both segment is
    // ~1000x faster than realtime, and a video-copy-but-audio-reencode
    // segment sits in between (audio encoding alone is cheap but not free
    // over a long segment). They don't need to be precise - JobStatsTracker
    // recalibrates its actual seconds-per-weight-unit ratio from real
    // elapsed time after every completed step, so only the RELATIVE sizes
    // here matter (a re-encode segment must weigh far more than a copy
    // segment of the same duration), not their absolute values.
    private const double ReencodeSecondsPerContentSecond = 1.0;
    private const double CopyAudioReencodeSecondsPerContentSecond = 0.05;
    public const double CopyBothSecondsPerContentSecond = 0.001;
    public const double ProbeSecondsPerContentSecond = 0.01;

    /// <summary>
    /// Estimates how many wall-clock seconds CutSegmentSmart will spend
    /// cutting [start, end) from a file with the given keyframes/compat
    /// flags, WITHOUT running ffmpeg - used only to weight this step in the
    /// job's ETA (see JobStatsTracker.ReportStepCompleted). Mirrors
    /// CutSegmentSmart's own window-finding branches exactly, because the
    /// two must agree on which parts of the range get re-encoded vs copied
    /// for the weight to mean anything; if that logic ever changes, change
    /// it here too.
    /// </summary>
    public static double EstimateCutSeconds(
        double start, double end, IReadOnlyList<double> keyframes, bool videoOk, bool audioOk)
    {
        double? k1 = null, k2 = null;
        if (videoOk && keyframes.Count > 0)
        {
            foreach (var kf in keyframes) { if (kf >= start - 1e-3) { k1 = kf; break; } }
            foreach (var kf in keyframes) { if (kf <= end + 1e-3) k2 = kf; else break; }
        }

        bool hasWindow = videoOk && k1 is not null && k2 is not null && (k2.Value - k1.Value) > MinSliverSeconds;
        if (!hasWindow)
            return (end - start) * ReencodeSecondsPerContentSecond;

        double k1v = k1!.Value, k2v = k2!.Value;
        double cost = 0;
        if (k1v - start > MinSliverSeconds) cost += (k1v - start) * ReencodeSecondsPerContentSecond;
        cost += (k2v - k1v) * (audioOk ? CopyBothSecondsPerContentSecond : CopyAudioReencodeSecondsPerContentSecond);
        if (end - k2v > MinSliverSeconds) cost += (end - k2v) * ReencodeSecondsPerContentSecond;
        return cost;
    }

    public static readonly string Ffmpeg = Paths.FindTool("ffmpeg");
    public static readonly string Ffprobe = Paths.FindTool("ffprobe");

    private static readonly HashSet<string> Mp4SafeVideoCodecs = new() { "h264", "hevc" };

    private static readonly Dictionary<string, (string Encoder, string Preset, int Crf)> VideoEncodeSettings = new()
    {
        ["h264"] = ("libx264", "slow", 18),
        ["hevc"] = ("libx265", "slow", 20),
    };

    private const string AudioEncoder = "aac";

    public static readonly HashSet<string> SupportedSubtitleCodecs = new() { "subrip", "ass", "ssa" };

    private static readonly Dictionary<string, string> SubtitleMuxerByCodec = new()
    {
        ["subrip"] = "srt",
        ["ass"] = "ass",
        ["ssa"] = "ass",
    };

    private static void PadToMinimum(DateTime start, Action? tick, double minimum = MinStepSeconds)
    {
        double remaining = minimum - (DateTime.UtcNow - start).TotalSeconds;
        while (remaining > 0)
        {
            tick?.Invoke();
            double step = Math.Min(0.15, remaining);
            Thread.Sleep((int)(step * 1000));
            remaining -= step;
        }
    }

    private static class NativeProcessControl
    {
        [DllImport("ntdll.dll")]
        private static extern int NtSuspendProcess(IntPtr processHandle);

        [DllImport("ntdll.dll")]
        private static extern int NtResumeProcess(IntPtr processHandle);

        /// <summary>Best-effort OS-level pause of the running ffmpeg process
        /// itself - not just our own polling of it - via the same
        /// undocumented NtSuspendProcess/NtResumeProcess pair Task Manager's
        /// own "Suspend process" uses. Any failure here is swallowed - a job
        /// that keeps running instead of pausing is far preferable to one
        /// that crashes because pausing it didn't work.</summary>
        public static void Suspend(Process p)
        {
            try { NtSuspendProcess(p.Handle); } catch { /* best-effort */ }
        }

        public static void Resume(Process p)
        {
            try { NtResumeProcess(p.Handle); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Runs one ffmpeg invocation. <paramref name="pad"/> controls whether
    /// THIS call pads itself to MinStepSeconds - callers that make several
    /// ffmpeg calls to produce one logical, user-visible "step" (see
    /// CutSegmentSmart) pass pad=false and do the padding once themselves,
    /// across the whole step. <paramref name="pause"/>, while set, suspends
    /// the ffmpeg process and skips calling tick() (so a pending STOP,
    /// which tick() itself is responsible for raising, is only noticed once
    /// resumed).
    /// </summary>
    public static void RunFfmpeg(IEnumerable<string> args, Action<string, string?> log,
        Action? tick = null, bool pad = true, PauseController? pause = null, JobStatsTracker? stats = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        bool verbose = AppSettings.VerboseLogging;
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add(verbose ? "info" : "error");
        foreach (var a in args) psi.ArgumentList.Add(a);

        if (verbose) log("$ " + FormatCommandForLog(psi), "dim");

        var start = DateTime.UtcNow;
        using var proc = new Process { StartInfo = psi };
        // EnableRaisingEvents + Exited lets the wait loop below block on a
        // real OS wait handle instead of spin-polling HasExited on a fixed
        // timer - the process is noticed the instant it exits rather than
        // up to one poll interval later, and the thread costs nothing while
        // ffmpeg is quietly running (which is most of a job's wall time).
        using var exited = new ManualResetEventSlim(false);
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => { try { exited.Set(); } catch (ObjectDisposedException) { /* race with using-dispose below */ } };
        proc.Start();
        proc.StandardInput.Close(); // mirrors Python's stdin=subprocess.DEVNULL
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutDrainTask = proc.StandardOutput.ReadToEndAsync();

        bool suspended = false;
        try
        {
            // The wait interval below still doubles as the tick()/pause-check
            // cadence (progress-bar smoothing, STOP responsiveness) - but
            // exited.Wait(ms) returns as soon as the Exited event fires,
            // instead of always sleeping the full interval like
            // Thread.Sleep did.
            while (!exited.IsSet)
            {
                if (pause is { IsPaused: true })
                {
                    if (!suspended) { NativeProcessControl.Suspend(proc); suspended = true; }
                    exited.Wait(100);
                    continue;
                }
                if (suspended) { NativeProcessControl.Resume(proc); suspended = false; }
                stats?.Sample(proc);
                tick?.Invoke();
                exited.Wait(150);
            }
        }
        catch
        {
            // Includes cancellation (STOP was pressed): kill ffmpeg right
            // away instead of letting it run to completion in the background.
            if (suspended) NativeProcessControl.Resume(proc);
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { proc.WaitForExit(5000); } catch { /* best-effort */ }
            throw;
        }

        proc.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult();
        _ = stdoutDrainTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
        {
            log("ffmpeg error:\n" + stderr, null);
            throw new InvalidOperationException("ffmpeg failed (see log above)");
        }
        if (verbose && !string.IsNullOrWhiteSpace(stderr)) log("ffmpeg stderr:\n" + stderr, "dim");
        if (pad) PadToMinimum(start, tick);
    }

    /// <summary>Renders a ProcessStartInfo's file name + ArgumentList back into
    /// a single copy-pasteable line for the verbose log - quoting only the
    /// arguments that actually need it (contain a space or are empty),
    /// mirroring how a shell would echo the command.</summary>
    private static string FormatCommandForLog(ProcessStartInfo psi)
    {
        var parts = psi.ArgumentList.Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a);
        return psi.FileName + " " + string.Join(" ", parts);
    }

    // -----------------------------------------------------------------
    // Probing: full stream parameters + keyframe map
    // -----------------------------------------------------------------

    private sealed class ProbeStreamDto
    {
        [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
        [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
        [JsonPropertyName("profile")] public string? Profile { get; set; }
        [JsonPropertyName("pix_fmt")] public string? PixFmt { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
        [JsonPropertyName("r_frame_rate")] public string? RFrameRate { get; set; }
        [JsonPropertyName("sample_aspect_ratio")] public string? SampleAspectRatio { get; set; }
        [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }
        [JsonPropertyName("channels")] public int? Channels { get; set; }
    }

    private sealed class ProbeFormatDto
    {
        [JsonPropertyName("duration")] public string? Duration { get; set; }
    }

    private sealed class ProbeRootDto
    {
        [JsonPropertyName("streams")] public List<ProbeStreamDto>? Streams { get; set; }
        [JsonPropertyName("format")] public ProbeFormatDto? Format { get; set; }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCapture(string fileName, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Returns the stream parameters this module cares about for
    /// compatibility decisions. <paramref name="pad"/> controls whether this
    /// call stretches itself to MinStepSeconds - wanted for the "Analyzing
    /// source video..." step so the progress bar doesn't flash past
    /// instantly, but wrong for the preview player, which just wants the
    /// duration/dimensions back as fast as ffprobe can produce them.
    /// </summary>
    public static ProbeResult ProbeFull(string path, Action? tick = null, bool pad = true)
    {
        var start = DateTime.UtcNow;
        var (exitCode, stdout, stderr) = RunCapture(Ffprobe, new[]
        {
            "-v", "error", "-show_streams", "-show_format", "-of", "json", path,
        });
        if (exitCode != 0)
            throw new InvalidOperationException($"ffprobe failed for {path}: {stderr}");

        var data = JsonSerializer.Deserialize<ProbeRootDto>(stdout) ?? new ProbeRootDto();

        VideoInfo? video = null;
        AudioInfo? audio = null;
        SubtitleInfo? subtitle = null;
        foreach (var s in data.Streams ?? new List<ProbeStreamDto>())
        {
            if (s.CodecType == "video" && video is null)
            {
                var parts = (s.RFrameRate ?? "0/1").Split('/');
                double num = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double den = parts.Length > 1 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : 1.0;
                double fps = den != 0 ? num / den : num;
                video = new VideoInfo(
                    s.CodecName ?? "", s.Profile, s.PixFmt ?? "",
                    s.Width ?? 0, s.Height ?? 0, fps, s.SampleAspectRatio ?? "1:1");
            }
            else if (s.CodecType == "audio" && audio is null)
            {
                int sr = int.TryParse(s.SampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srVal) ? srVal : 48000;
                audio = new AudioInfo(s.CodecName ?? "", sr, s.Channels ?? 2);
            }
            else if (s.CodecType == "subtitle" && subtitle is null)
            {
                subtitle = new SubtitleInfo(s.CodecName ?? "");
            }
        }
        if (video is null)
            throw new InvalidOperationException($"No video stream found in {path}");

        double duration = 0.0;
        if (data.Format?.Duration is { } durStr)
            double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

        if (pad) PadToMinimum(start, tick);
        return new ProbeResult(path, video, audio, subtitle, duration);
    }

    /// <summary>
    /// Returns the sorted list of keyframe (I-frame) timestamps for the
    /// first video stream, read straight from packet headers - no decoding
    /// involved. This is what makes lossless cutting possible at all: a
    /// copy cut that starts and ends exactly on a keyframe is both instant
    /// and perfectly lossless.
    /// </summary>
    public static List<double> ProbeKeyframeTimes(string path, Action? tick = null)
    {
        var (exitCode, stdout, stderr) = RunCapture(Ffprobe, new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,dts_time,flags",
            "-of", "csv=p=0", path,
        });
        if (exitCode != 0)
            throw new InvalidOperationException($"ffprobe (keyframes) failed for {path}: {stderr}");

        var times = new List<double>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            var ptsTime = parts[0];
            var dtsTime = parts[1];
            var flags = parts[2];
            if (!flags.Contains('K')) continue;
            var t = ptsTime != "N/A" ? ptsTime : dtsTime;
            if (t == "N/A") continue;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                times.Add(val);
        }
        times.Sort();
        tick?.Invoke();
        return times;
    }

    // -----------------------------------------------------------------
    // Preview: single-frame extraction for the range-picker player
    // -----------------------------------------------------------------

    /// <summary>
    /// Grabs one decoded video frame at <paramref name="timestamp"/>
    /// (seconds), as raw PPM bytes - written to a temp file and read back
    /// rather than piped, because the trimmed ffmpeg-mini build only
    /// enables the "file" protocol, not "pipe". "-ss" before "-i" is
    /// ffmpeg's fast, keyframe-approximate seek - the right kind for a
    /// scrub preview, wrong for frame-accurate cuts (CutSegmentSmart never
    /// calls this). Returns null instead of throwing when ffmpeg produces
    /// no frame, so a caller mid-drag can just skip that update and keep
    /// showing the last good frame.
    /// </summary>
    public static byte[]? ExtractFramePpm(string path, double timestamp, int maxWidth = 640)
    {
        double t = Math.Max(0.0, timestamp);
        string outPath = Path.Combine(Path.GetTempPath(), $"clipglue_{Guid.NewGuid():N}.ppm");
        try
        {
            var (exitCode, _, _) = RunCapture(Ffmpeg, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-ss", t.ToString("F3", CultureInfo.InvariantCulture), "-i", path,
                "-frames:v", "1",
                "-vf", $"scale={maxWidth.ToString(CultureInfo.InvariantCulture)}:-2",
                outPath,
            });
            if (exitCode != 0 || !File.Exists(outPath)) return null;
            var data = File.ReadAllBytes(outPath);
            return data.Length > 0 ? data : null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(outPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Pulls the first subtitle stream of <paramref name="path"/> out as
    /// plain SRT/ASS text, for the preview player's cue overlay. Always a
    /// stream copy (never decodes/re-encodes). Returns null on any failure -
    /// a preview without a subtitle overlay is a lesser experience, not a
    /// fatal one.
    /// </summary>
    public static string? ExtractSubtitleText(string path, string codec)
    {
        if (!SubtitleMuxerByCodec.TryGetValue(codec, out var muxer)) return null;
        string outPath = Path.Combine(Path.GetTempPath(), $"clipglue_{Guid.NewGuid():N}.{muxer}");
        try
        {
            var (exitCode, _, _) = RunCapture(Ffmpeg, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", path, "-map", "0:s:0", "-c:s", "copy",
                "-f", muxer, outPath,
            });
            if (exitCode != 0 || !File.Exists(outPath)) return null;
            return File.ReadAllText(outPath, System.Text.Encoding.UTF8);
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(outPath); } catch { /* best-effort */ }
        }
    }

    // -----------------------------------------------------------------
    // Target format decision + compatibility checks
    // -----------------------------------------------------------------

    /// <summary>
    /// Picks the one video/audio format every kept range gets normalized to
    /// before the final concat. Resolution/fps/pixel format/SAR always come
    /// from the FIRST input file; the target video codec is whichever of
    /// h264/hevc is used by the most input files (ties favour the first
    /// file's codec), falling back to h264 if none qualify. Audio only
    /// targets AAC (at the first file's sample rate/channel count) when
    /// EVERY file in the batch has an audio track of its own - same
    /// all-or-nothing rule as subtitles below, and for the same reason: a
    /// segment cut from a file with no audio stream at all has nothing to
    /// copy or encode into an audio track, so mixing it with segments from
    /// files that DO have one would hand the concat demuxer pieces with
    /// inconsistent stream layouts (see AUDIT_TODO.md #3). This bundled
    /// ffmpeg build is also trimmed down to only mov/matroska/concat
    /// demuxers with no lavfi/anullsrc/volume filter available, so
    /// synthesizing a silent filler track isn't an option either - dropping
    /// audio for the whole batch is the only fix that's both correct and
    /// actually implementable against this binary.
    /// </summary>
    public static TargetFormat DecideTarget(IReadOnlyList<ProbeResult> fileProbes)
    {
        var baseline = fileProbes[0];

        var codecVotes = new Dictionary<string, int>();
        foreach (var p in fileProbes)
        {
            var c = p.Video.Codec;
            if (Mp4SafeVideoCodecs.Contains(c))
                codecVotes[c] = codecVotes.GetValueOrDefault(c) + 1;
        }
        string targetCodec;
        if (codecVotes.Count > 0)
        {
            targetCodec = codecVotes
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => kv.Key == baseline.Video.Codec)
                .First().Key;
        }
        else
        {
            targetCodec = "h264";
        }

        var venc = VideoEncodeSettings[targetCodec];
        var bv = baseline.Video;
        var targetVideo = new TargetVideo(
            Codec: targetCodec,
            Encoder: venc.Encoder,
            Preset: venc.Preset,
            Crf: venc.Crf,
            PixFmt: bv.Codec == targetCodec ? bv.PixFmt : "yuv420p",
            Width: bv.Width,
            Height: bv.Height,
            Fps: bv.Fps,
            Sar: bv.Sar);

        TargetAudio? targetAudio = null;
        if (fileProbes.All(p => p.Audio is not null))
        {
            var audioBaseline = fileProbes[0].Audio!;
            int channels = audioBaseline.Channels;
            targetAudio = new TargetAudio(
                Codec: "aac",
                Encoder: AudioEncoder,
                SampleRate: audioBaseline.SampleRate,
                Channels: channels,
                // A stereo track is essentially transparent at 192k AAC; a
                // 5.1 (or more) track needs proportionally more to hold up.
                Bitrate: channels <= 2 ? "192k" : "384k");
        }

        List<string>? subtitleCodecs = new();
        foreach (var p in fileProbes)
        {
            var sub = p.Subtitle;
            if (sub is null || !SupportedSubtitleCodecs.Contains(sub.Codec))
            {
                subtitleCodecs = null;
                break;
            }
            subtitleCodecs.Add(sub.Codec);
        }
        TargetSubtitle? targetSubtitle = null;
        if (subtitleCodecs is { Count: > 0 } && subtitleCodecs.Distinct().Count() == 1)
            targetSubtitle = new TargetSubtitle(subtitleCodecs[0]);

        return new TargetFormat(targetVideo, targetAudio, targetSubtitle);
    }

    /// <summary>True if this file's video stream can be losslessly copied
    /// straight into the target format. Profile/level are deliberately not
    /// checked: differing profiles concatenate fine in practice.</summary>
    public static bool IsVideoCompatible(ProbeResult probe, TargetFormat target)
    {
        var v = probe.Video;
        var t = target.Video;
        return v.Codec == t.Codec
            && v.PixFmt == t.PixFmt
            && v.Width == t.Width
            && v.Height == t.Height
            && Math.Abs(v.Fps - t.Fps) < 0.01
            && v.Sar == t.Sar;
    }

    /// <summary>True if this file's audio stream can be losslessly copied
    /// straight into the target format. A file with no audio stream is
    /// trivially compatible.</summary>
    public static bool IsAudioCompatible(ProbeResult probe, TargetFormat target)
    {
        var a = probe.Audio;
        if (a is null) return true;
        var t = target.Audio;
        if (t is null) return false;
        return a.Codec == t.Codec && a.SampleRate == t.SampleRate && a.Channels == t.Channels;
    }

    // -----------------------------------------------------------------
    // Cutting
    // -----------------------------------------------------------------

    private static string F3(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string F6(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

    private static string NormalizeFilter(TargetFormat target)
    {
        var v = target.Video;
        return $"scale={v.Width}:{v.Height}:force_original_aspect_ratio=decrease," +
               $"pad={v.Width}:{v.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={v.Fps.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// -map args needed once ANY stream is mapped explicitly - which is
    /// only when subtitle passthrough is active. Without this, adding
    /// "-map 0:s:0" alone would also turn OFF ffmpeg's automatic
    /// video/audio stream selection, silently dropping them.
    /// </summary>
    private static List<string> ExplicitStreamMaps(TargetFormat target)
    {
        if (target.Subtitle is null) return new List<string>();
        var maps = new List<string> { "-map", "0:v:0" };
        if (target.Audio is not null) maps.AddRange(new[] { "-map", "0:a:0" });
        maps.AddRange(new[] { "-map", "0:s:0" });
        return maps;
    }

    /// <summary>Piece/output file extension: .mkv when subtitles are being
    /// passed through (MP4 can't hold ass/ssa), .mp4 otherwise.</summary>
    private static string PieceExt(TargetFormat target) => target.Subtitle is not null ? ".mkv" : ".mp4";

    private static List<string> EncodeArgs(string inputFile, double start, double duration, TargetFormat target, bool normalize, string outPath)
    {
        var v = target.Video;
        var a = target.Audio;
        var args = new List<string> { "-ss", F3(start), "-i", inputFile, "-t", F3(duration) };
        args.AddRange(ExplicitStreamMaps(target));
        if (normalize) args.AddRange(new[] { "-vf", NormalizeFilter(target) });
        args.AddRange(new[] { "-c:v", v.Encoder, "-preset", v.Preset, "-crf", v.Crf.ToString(CultureInfo.InvariantCulture) });
        if (a is not null)
        {
            args.AddRange(new[]
            {
                "-c:a", a.Encoder, "-ar", a.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", a.Channels.ToString(CultureInfo.InvariantCulture), "-b:a", a.Bitrate,
            });
        }
        else
        {
            // Explicit, not just "no -c:a given": without this, a source
            // file that DOES have its own audio track (mixed batch, this
            // file's stream just isn't part of the shared target) would
            // still get that track auto-selected and re-encoded by
            // ffmpeg's default stream selection, since omitting "-c:a"
            // does not mean "no audio" - it means "let ffmpeg decide".
            args.Add("-an");
        }
        if (target.Subtitle is not null) args.AddRange(new[] { "-c:s", "copy" });
        args.AddRange(new[] { "-avoid_negative_ts", "auto", outPath });
        return args;
    }

    /// <summary>Audio args for a video-copy middle chunk: copy audio too if
    /// the file's own audio already matches the target, otherwise encode
    /// just the audio stream to the target AAC while the video stream next
    /// to it is still being stream-copied in the same ffmpeg call. Explicit
    /// "-an" (not just omitting "-c:a") when there's no target audio at
    /// all, so a file that DOES have its own audio track doesn't get it
    /// auto-selected and re-encoded by ffmpeg's default stream
    /// selection.</summary>
    private static List<string> MidAudioArgs(TargetFormat target, bool audioOk)
    {
        if (target.Audio is null) return new List<string> { "-an" };
        if (audioOk) return new List<string> { "-c:a", "copy" };
        var a = target.Audio;
        return new List<string>
        {
            "-c:a", a.Encoder, "-ar", a.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", a.Channels.ToString(CultureInfo.InvariantCulture), "-b:a", a.Bitrate,
        };
    }

    /// <summary>
    /// Cuts the [start, end) range to KEEP from <paramref name="inputFile"/>
    /// into one or more piece files that, concatenated in order, reproduce
    /// it - copying whatever can be copied losslessly and re-encoding only
    /// what can't.
    ///
    /// Strategy: if this file's video already matches the target
    /// (<paramref name="videoOk"/>), look for the largest keyframe-aligned
    /// window [k1, k2) inside [start, end). That window is copied verbatim -
    /// instant, zero quality loss - with its audio copied too if compatible
    /// (<paramref name="audioOk"/>), or encoded to the target AAC alongside
    /// it otherwise. Only the small slivers before k1 and/or after k2 need
    /// re-encoding, to land exactly on the boundary the user asked for.
    ///
    /// If no such window exists, the whole range is re-encoded in one piece.
    /// Returns the list of piece file paths, in playback order.
    /// </summary>
    public static List<string> CutSegmentSmart(
        string inputFile, double start, double end, TargetFormat target,
        IReadOnlyList<double> keyframes, bool videoOk, bool audioOk,
        string outDir, string prefix, Action<string, string?> log,
        Action? tick = null, PauseController? pause = null, JobStatsTracker? stats = null)
    {
        var stepStart = DateTime.UtcNow;
        var pieces = new List<string>();
        var ext = PieceExt(target);

        string NextOut(string suffix) => Path.Combine(outDir, $"{prefix}_{suffix}{ext}");

        double? k1 = null, k2 = null;
        if (videoOk && keyframes.Count > 0)
        {
            foreach (var kf in keyframes)
            {
                if (kf >= start - 1e-3) { k1 = kf; break; }
            }
            foreach (var kf in keyframes)
            {
                if (kf <= end + 1e-3) k2 = kf;
                else break;
            }
        }

        bool hasWindow = videoOk && k1 is not null && k2 is not null && (k2.Value - k1.Value) > MinSliverSeconds;

        if (hasWindow)
        {
            double k1v = k1!.Value, k2v = k2!.Value;

            if (k1v - start > MinSliverSeconds)
            {
                var outHead = NextOut("head");
                log($"  [{start.ToString("F2", CultureInfo.InvariantCulture)}s] re-encoding {(k1v - start).ToString("F2", CultureInfo.InvariantCulture)}s lead-in up to the nearest keyframe", "dim");
                RunFfmpeg(EncodeArgs(inputFile, start, k1v - start, target, normalize: false, outPath: outHead), log, tick, pad: false, pause: pause, stats: stats);
                pieces.Add(outHead);
            }

            var outMid = NextOut("mid");
            log($"  [{k1v.ToString("F2", CultureInfo.InvariantCulture)}s-{k2v.ToString("F2", CultureInfo.InvariantCulture)}s] lossless copy, {(k2v - k1v).ToString("F2", CultureInfo.InvariantCulture)}s untouched", "dim");
            // Full precision here (not the usual 3 decimals): k1/k2 are exact
            // keyframe timestamps, and rounding -ss even slightly past one
            // could make ffmpeg seek to the wrong keyframe.
            var copyArgs = new List<string> { "-ss", F6(k1v), "-i", inputFile, "-t", F6(k2v - k1v) };
            copyArgs.AddRange(ExplicitStreamMaps(target));
            copyArgs.AddRange(new[] { "-c:v", "copy" });
            copyArgs.AddRange(MidAudioArgs(target, audioOk));
            if (target.Subtitle is not null) copyArgs.AddRange(new[] { "-c:s", "copy" });
            copyArgs.AddRange(new[] { "-avoid_negative_ts", "auto", outMid });
            RunFfmpeg(copyArgs, log, tick, pad: false, pause: pause, stats: stats);
            pieces.Add(outMid);

            if (end - k2v > MinSliverSeconds)
            {
                var outTail = NextOut("tail");
                log($"  [{k2v.ToString("F2", CultureInfo.InvariantCulture)}s-{end.ToString("F2", CultureInfo.InvariantCulture)}s] re-encoding {(end - k2v).ToString("F2", CultureInfo.InvariantCulture)}s tail-out from the last keyframe", "dim");
                RunFfmpeg(EncodeArgs(inputFile, k2v, end - k2v, target, normalize: false, outPath: outTail), log, tick, pad: false, pause: pause, stats: stats);
                pieces.Add(outTail);
            }
        }
        else
        {
            var reason = !videoOk ? "source format differs from the target" : "range shorter than one GOP";
            log($"  re-encoding the whole range ({reason})", "dim");
            var outFull = NextOut("full");
            RunFfmpeg(EncodeArgs(inputFile, start, end - start, target, normalize: !videoOk, outPath: outFull), log, tick, pad: false, pause: pause, stats: stats);
            pieces.Add(outFull);
        }

        PadToMinimum(stepStart, tick);
        return pieces;
    }

    // -----------------------------------------------------------------
    // Joining
    // -----------------------------------------------------------------

    /// <summary>
    /// Escapes a path for the ffmpeg concat demuxer's quoted file format
    /// (paths are wrapped in single quotes). A literal ' inside the path
    /// must be turned into '\'' or it prematurely closes the quoted string
    /// and breaks the whole list file.
    /// </summary>
    private static string EscapeConcatPath(string pathStr) => pathStr.Replace("'", "'\\''");

    /// <summary>
    /// Joins a list of video files into one, using the ffmpeg concat
    /// demuxer. Every piece CutSegmentSmart() produces is already
    /// normalized to the same target format, so this is always a plain
    /// "-c copy" join - no re-encoding happens here, ever.
    /// <paramref name="hasSubtitle"/> adds an explicit "-map 0" so the
    /// subtitle stream every piece already carries rides along into the
    /// final file too.
    /// </summary>
    public static void ConcatSegments(IReadOnlyList<string> segmentPaths, string outputFile,
        Action<string, string?> log, Action? tick = null, bool hasSubtitle = false, PauseController? pause = null,
        JobStatsTracker? stats = null)
    {
        string listPath = Path.Combine(Path.GetTempPath(), $"clipglue_{Guid.NewGuid():N}.txt");
        try
        {
            // A plain Encoding.UTF8 writes a byte-order-mark that ffmpeg's
            // concat demuxer chokes on ("unknown keyword" on the first
            // line) - Python's open(..., encoding="utf-8") never emits
            // one, so this has to be told not to either.
            using (var writer = new StreamWriter(listPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                foreach (var p in segmentPaths)
                {
                    var posixPath = Path.GetFullPath(p).Replace('\\', '/');
                    writer.Write($"file '{EscapeConcatPath(posixPath)}'\n");
                }
            }
            var args = new List<string> { "-f", "concat", "-safe", "0", "-i", listPath };
            if (hasSubtitle) args.AddRange(new[] { "-map", "0" });
            args.AddRange(new[] { "-c", "copy" });
            // Tags the output container with who/what made it. "-metadata"
            // only touches container tags, not stream data, so it's free to
            // combine with "-c copy" here without triggering a re-encode.
            args.AddRange(new[] { "-metadata", "artist=BRTMN" });
            args.AddRange(new[] { "-metadata", "comment=Created with ClipGlue" });
            args.Add(outputFile);
            RunFfmpeg(args, log, tick, pad: true, pause: pause, stats: stats);
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* best-effort */ }
        }
    }
}
