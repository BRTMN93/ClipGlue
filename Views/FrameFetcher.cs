using System.Threading.Channels;
using ClipGlue.Engine;

namespace ClipGlue.Views;

/// <summary>
/// Runs frame extraction on its own background task, one request "in
/// flight" at a time. Request() just overwrites the pending timestamp - if
/// the user drags faster than ffmpeg can grab frames, only the latest
/// position ever actually gets fetched, so the preview never falls behind
/// queued stale requests.
///
/// Port of preview_player.py's _FrameFetcher. The Python original hand-
/// rolled this with a lock + threading.Event; a bounded Channel with
/// DropOldest gives the identical "overwrite the pending request" semantics
/// for free.
/// </summary>
public sealed class FrameFetcher
{
    private readonly string _path;
    private readonly Channel<double> _channel = Channel.CreateBounded<double>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<byte[]> _onFrame;
    private readonly Task _runTask;
    private int _stopped;

    public int Width { get; set; }

    public FrameFetcher(string path, int width, Action<byte[]> onFrame)
    {
        _path = path;
        Width = width;
        _onFrame = onFrame;
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Request(double t) => _channel.Writer.TryWrite(t);

    /// <summary>Called both when MediaOpened hands playback to the real
    /// decoder and, independently, from the window's Closed handler - so
    /// this must tolerate being called twice without touching the
    /// already-cancelled/disposed _cts a second time.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        // Disposed only once RunAsync has actually observed the
        // cancellation and returned - see N16 in AUDIT_TODO.md. Disposing
        // _cts synchronously here, while RunAsync's ReadAllAsync(token) may
        // still be registered against it, risks an ObjectDisposedException
        // racing with that registration; waiting for _runTask first avoids
        // that entirely.
        _runTask.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var t in _channel.Reader.ReadAllAsync(token))
            {
                byte[]? data;
                try { data = FfmpegEngine.ExtractFramePpm(_path, t, Width); }
                catch { data = null; }
                if (data is not null) _onFrame(data);
            }
        }
        catch (OperationCanceledException) { /* Stop() was called */ }
    }
}
