namespace ClipGlue.Engine;

public sealed record VideoInfo(string Codec, string? Profile, string PixFmt, int Width, int Height, double Fps, string Sar);

public sealed record AudioInfo(string Codec, int SampleRate, int Channels);

public sealed record SubtitleInfo(string Codec);

public sealed record ProbeResult(string Path, VideoInfo Video, AudioInfo? Audio, SubtitleInfo? Subtitle, double Duration);

public sealed record TargetVideo(string Codec, string Encoder, string Preset, int Crf, string PixFmt, int Width, int Height, double Fps, string Sar);

public sealed record TargetAudio(string Codec, string Encoder, int SampleRate, int Channels, string Bitrate);

public sealed record TargetSubtitle(string Codec);

public sealed record TargetFormat(TargetVideo Video, TargetAudio? Audio, TargetSubtitle? Subtitle);

/// <summary>Thread-safe flag ffmpeg invocations poll to suspend/resume the
/// child process itself while a blocking confirmation dialog is up.</summary>
public sealed class PauseController
{
    private volatile bool _paused;
    public bool IsPaused => _paused;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
}
