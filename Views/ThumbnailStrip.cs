using System.Windows.Media.Imaging;
using ClipGlue.Engine;

namespace ClipGlue.Views;

/// <summary>
/// Decodes and caches the single frames that make up the scrubber's
/// filmstrip. One ffmpeg process per thumbnail (the same
/// <see cref="FfmpegEngine.ExtractFramePpm"/> the frame-at-a-time preview
/// fallback uses) at roughly 0.1-0.4s each, so this has to run off the UI
/// thread and land progressively.
///
/// Sibling of, not a replacement for, <see cref="FrameFetcher"/>: that one
/// keeps exactly ONE request in flight and throws away everything older,
/// because a scrub only ever wants the newest position. A filmstrip wants a
/// whole set of positions to arrive eventually, so this keeps a work list
/// instead - but still drops the whole list the moment the viewport changes,
/// since thumbnails for a viewport nobody is looking at any more are wasted
/// ffmpeg launches.
///
/// Timestamps are quantised onto a 0.1s grid before they become cache keys,
/// so panning back over ground already covered redraws from the cache
/// instead of re-decoding.
/// </summary>
public sealed class ThumbnailStrip
{
    private const double Quant = 0.1;
    private const int CacheCap = 320;

    private readonly string _path;
    private readonly Action _onReady;
    private readonly object _gate = new();
    private readonly Dictionary<long, BitmapSource?> _cache = new();
    private readonly Queue<long> _evictOrder = new();
    private readonly List<double> _pending = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;

    /// <summary>Pixel width to decode at - set from the on-screen cell width
    /// so the strip is not decoding 640px frames for 90px slots.</summary>
    public int DecodeWidth { get; set; } = 96;

    public ThumbnailStrip(string path, Action onReady)
    {
        _path = path;
        _onReady = onReady;
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private static long Key(double t) => (long)Math.Round(Math.Max(0.0, t) / Quant);

    /// <summary>The frame for this slot, or null while it is still being
    /// decoded (or if it could not be decoded at all).</summary>
    public BitmapSource? Get(double t)
    {
        lock (_gate) return _cache.TryGetValue(Key(t), out var bmp) ? bmp : null;
    }

    /// <summary>Replaces the work list with the frames the scrubber needs
    /// now. Anything already cached is skipped.</summary>
    public void Request(IReadOnlyList<double> times)
    {
        lock (_gate)
        {
            _pending.Clear();
            foreach (double t in times)
                if (!_cache.ContainsKey(Key(t)))
                    _pending.Add(t);
            if (_pending.Count == 0) return;
        }
        try { _signal.Release(); } catch (SemaphoreFullException) { /* worker is already awake */ }
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _signal.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        // See FrameFetcher.Stop's comment for why this waits for the worker
        // task rather than disposing _cts/_signal synchronously - N16 in
        // AUDIT_TODO.md.
        _runTask.ContinueWith(_ =>
        {
            _cts.Dispose();
            _signal.Dispose();
        }, TaskScheduler.Default);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                double t;
                lock (_gate)
                {
                    if (_pending.Count == 0) t = double.NaN;
                    else { t = _pending[0]; _pending.RemoveAt(0); }
                }
                if (double.IsNaN(t))
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);
                    continue;
                }

                byte[]? data;
                try { data = FfmpegEngine.ExtractFramePpm(_path, t, DecodeWidth); }
                catch { data = null; }
                if (token.IsCancellationRequested) return;

                // Decoded off the UI thread; PpmDecoder freezes the result,
                // so handing it straight to the render pass is safe.
                BitmapSource? bmp = data is null ? null : PpmDecoder.Decode(data);
                Store(Key(t), bmp);
                _onReady();
            }
        }
        catch (OperationCanceledException) { /* Stop() was called */ }
    }

    /// <summary>Caches the result - including a null, so a frame ffmpeg
    /// cannot produce is not retried on every redraw.</summary>
    private void Store(long key, BitmapSource? bmp)
    {
        lock (_gate)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = bmp;
            _evictOrder.Enqueue(key);
            while (_evictOrder.Count > CacheCap)
                _cache.Remove(_evictOrder.Dequeue());
        }
    }
}
