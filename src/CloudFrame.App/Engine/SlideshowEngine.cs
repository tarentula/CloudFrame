using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using CloudFrame.Core.Cloud;
using CloudFrame.Core.Config;
using CloudFrame.Core.Index;

namespace CloudFrame.App.Engine
{
    /// <summary>
    /// Central coordinator for the slideshow. Owns the slide timer, manages
    /// the prefetch queue, and triggers periodic index refreshes.
    ///
    /// Threading model:
    ///   - The slide timer fires on a thread-pool thread.
    ///   - The engine raises <see cref="SlideReady"/> on that same thread.
    ///   - <see cref="SlideshowForm"/> must marshal to the UI thread via
    ///     Control.BeginInvoke before touching any WinForms objects.
    ///
    /// Lifecycle:
    ///   1. Construct with initial settings and index.
    ///   2. Call <see cref="StartAsync"/> — first slide appears almost immediately.
    ///   3. Call <see cref="PauseAsync"/> / <see cref="ResumeAsync"/> from tray icon.
    ///   4. Call <see cref="DisposeAsync"/> on app exit.
    /// </summary>
    public sealed class SlideshowEngine : IAsyncDisposable
    {
        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised on a thread-pool thread when a new slide is ready to display.
        /// The bitmap is owned by the engine until the next slide — do not dispose it.
        /// </summary>
        public event Action<SlideEventArgs>? SlideReady;

        /// <summary>
        /// Raised when the engine encounters a non-fatal error (e.g. network
        /// failure). The tray icon can surface this as a balloon notification.
        /// </summary>
        public event Action<string>? ErrorOccurred;

        // ── State ──────────────────────────────────────────────────────────────

        private readonly AppSettings _settings;
        private readonly Func<CancellationToken, Task<ImageIndex>> _indexRefreshFactory;
        private readonly Func<CloudImageEntry, CancellationToken, Task<System.IO.Stream>> _downloadFactory;

        private DiskCache? _diskCache;
        private PrefetchQueue? _prefetchQueue;
        private System.Threading.Timer? _slideTimer;
        private System.Threading.Timer? _indexRefreshTimer;
        private CancellationTokenSource _cts = new();

        // Currently displayed bitmap — disposed when the next slide arrives.
        private PrefetchedImage? _current;

        private bool _paused;
        private bool _disposed;
        private volatile bool _firstSlideShown;   // set true once the first slide is displayed
        private readonly SemaphoreSlim _advanceLock = new(1, 1);

        // Recent slide history for ← navigation. Items are kept alive (not disposed)
        // until evicted. Guarded by _advanceLock.
        private readonly List<PrefetchedImage> _historyStack = new();
        private const int MaxHistory = 10;

        public SlideshowEngine(
            AppSettings settings,
            Func<CancellationToken, Task<ImageIndex>> indexRefreshFactory,
            Func<CloudImageEntry, CancellationToken, Task<System.IO.Stream>> downloadFactory)
        {
            _settings = settings;
            _indexRefreshFactory = indexRefreshFactory;
            _downloadFactory = downloadFactory;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the engine. Builds the initial index (from disk cache if
        /// available, then refreshes from cloud in the background).
        /// The first <see cref="SlideReady"/> event fires as soon as the first
        /// prefetched image is ready — typically well under one second.
        /// </summary>
        public async Task StartAsync(ImageIndex initialIndex, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Initialise the disk cache.
            string cacheDir = ResolveCacheDir();
            _diskCache = new DiskCache(
                cacheDir,
                _settings.DiskCacheLimitMb,
                _settings.CacheMaxDimensionPixels);

            // Start the prefetch queue with the initial (possibly cached) index.
            _prefetchQueue = new PrefetchQueue(
                initialIndex,
                _diskCache,
                _downloadFactory,
                _settings.PrefetchCount);

            // Start the slide timer. The first tick fires after one full interval
            // so the prefetch queue has time to download at least one image.
            // We do NOT await AdvanceSlideAsync() here — with an empty index it
            // would block forever waiting for the queue, preventing the cloud
            // refresh from ever starting.
            int intervalMs = Math.Max(1000, _settings.SlideDurationSeconds * 1000);
            _slideTimer = new System.Threading.Timer(
                _ => _ = AdvanceSlideAsync(),
                null,
                dueTime: intervalMs,   // first tick after one full interval
                period: intervalMs);

            // Fast-start: poll every 500 ms until the first slide is shown.
            // Without this the user has to wait a full SlideDurationSeconds (e.g.
            // 30 s) before seeing anything if the first download takes > 2 s.
            _ = Task.Run(async () =>
            {
                var ct = _cts.Token;
                Trace.TraceInformation("[Engine] Fast-start: waiting for first image…");
                while (!_firstSlideShown && !ct.IsCancellationRequested)
                {
                    await AdvanceSlideAsync().ConfigureAwait(false);
                    if (!_firstSlideShown)
                        await Task.Delay(500, ct).ConfigureAwait(false);
                }
                Trace.TraceInformation("[Engine] Fast-start: first image shown, handing off to slide timer.");
            });

            // Schedule periodic cloud index refreshes.
            if (_settings.IndexRefreshIntervalMinutes > 0)
            {
                int refreshMs = _settings.IndexRefreshIntervalMinutes * 60_000;
                _indexRefreshTimer = new System.Threading.Timer(
                    _ => _ = RefreshIndexAsync(),
                    null,
                    refreshMs,
                    refreshMs);
            }
        }

        /// <summary>Pauses slide advancement without stopping the background prefetch.</summary>
        public Task PauseAsync()
        {
            _paused = true;
            _slideTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        /// <summary>Resumes slide advancement after a pause.</summary>
        public Task ResumeAsync()
        {
            _paused = false;
            int intervalMs = _settings.SlideDurationSeconds * 1000;
            _slideTimer?.Change(intervalMs, intervalMs);
            return Task.CompletedTask;
        }

        /// <summary>Immediately advances to the next slide (e.g. tray menu "Next").</summary>
        public Task NextSlideAsync() => AdvanceSlideAsync();

        /// <summary>Goes back to the previously shown slide.</summary>
        public Task PreviousSlideAsync() => GoBackAsync();

        /// <summary>
        /// Feeds a freshly-built <see cref="ImageIndex"/> into the prefetch
        /// queue. Called by <see cref="IndexService.IndexRefreshed"/>.
        /// </summary>
        public void UpdateIndex(ImageIndex newIndex)
            => _prefetchQueue?.UpdateIndex(newIndex);

        // ── Core slide advance ─────────────────────────────────────────────────

        private async Task AdvanceSlideAsync()
        {
            if (_prefetchQueue is null) return;

            // Guard against the timer firing while a previous advance is still
            // in progress (e.g. slow network on first run).
            if (!await _advanceLock.WaitAsync(0).ConfigureAwait(false))
                return;

            try
            {
                var ct = _cts.Token;

                // Try to get a pre-fetched image with a short timeout so we
                // never block indefinitely when the index is empty (e.g. on
                // first run before the cloud scan completes). The fast-start
                // loop or the slide timer will retry.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

                PrefetchedImage next;
                try
                {
                    next = await _prefetchQueue.TakeAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timed out waiting — prefetch not ready yet. Caller will retry.
                    Trace.TraceInformation("[Engine] AdvanceSlide: timed out waiting for prefetch queue.");
                    return;
                }

                // Push the outgoing slide onto the history stack before disposing it.
                var previous = Interlocked.Exchange(ref _current, next);
                if (previous is not null)
                {
                    _historyStack.Add(previous);
                    // Evict oldest entry when history is full.
                    if (_historyStack.Count > MaxHistory)
                    {
                        _historyStack[0].Dispose();
                        _historyStack.RemoveAt(0);
                    }
                }

                _firstSlideShown = true;
                Trace.TraceInformation("[Engine] SlideReady: '{0}'.", next.Entry.Name);
                SlideReady?.Invoke(new SlideEventArgs(next.Bitmap, next.Entry));
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Failed to load next slide: {ex.Message}");
            }
            finally
            {
                _advanceLock.Release();
            }
        }

        // ── Previous slide ─────────────────────────────────────────────────────

        private async Task GoBackAsync()
        {
            if (_prefetchQueue is null) return;
            if (!await _advanceLock.WaitAsync(0).ConfigureAwait(false)) return;
            try
            {
                if (_historyStack.Count == 0) return;

                // Pop the most-recently-shown slide.
                int idx = _historyStack.Count - 1;
                var prev = _historyStack[idx];
                _historyStack.RemoveAt(idx);

                // Discard current — it'll reappear naturally via the prefetch queue.
                var current = Interlocked.Exchange(ref _current, prev);
                current?.Dispose();

                Trace.TraceInformation("[Engine] PreviousSlide: '{0}'.", prev.Entry.Name);
                SlideReady?.Invoke(new SlideEventArgs(prev.Bitmap, prev.Entry));
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Failed to go back: {ex.Message}");
            }
            finally
            {
                _advanceLock.Release();
            }
        }

        // ── Index refresh ──────────────────────────────────────────────────────

        private async Task RefreshIndexAsync()
        {
            try
            {
                var newIndex = await _indexRefreshFactory(_cts.Token).ConfigureAwait(false);
                _prefetchQueue?.UpdateIndex(newIndex);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Index refresh failed: {ex.Message}");
            }
        }

        // ── Dispose ────────────────────────────────────────────────────────────

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();

            _slideTimer?.Dispose();
            _indexRefreshTimer?.Dispose();

            if (_prefetchQueue is not null)
                await _prefetchQueue.DisposeAsync().ConfigureAwait(false);

            // Dispose any bitmaps still in history.
            foreach (var item in _historyStack)
                item.Dispose();
            _historyStack.Clear();

            _current?.Dispose();
            _diskCache?.Dispose();
            _advanceLock.Dispose();
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private string ResolveCacheDir()
        {
            if (!string.IsNullOrWhiteSpace(_settings.DiskCachePath))
                return _settings.DiskCachePath;

            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudFrame",
                "ImageCache");
        }
    }

    /// <summary>Event data for <see cref="SlideshowEngine.SlideReady"/>.</summary>
    public sealed class SlideEventArgs : EventArgs
    {
        /// <summary>
        /// The bitmap to display. Owned by the engine — do not dispose.
        /// Valid only until the next <see cref="SlideshowEngine.SlideReady"/> event.
        /// Copy it if you need it longer.
        /// </summary>
        public Bitmap Bitmap { get; }

        /// <summary>Metadata for the image currently displayed.</summary>
        public CloudImageEntry Entry { get; }

        public SlideEventArgs(Bitmap bitmap, CloudImageEntry entry)
        {
            Bitmap = bitmap;
            Entry = entry;
        }
    }
}