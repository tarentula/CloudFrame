using System;
using System.Drawing;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CloudFrame.Core.Cloud;
using CloudFrame.Core.Index;

namespace CloudFrame.App.Engine
{
    /// <summary>
    /// Maintains a bounded queue of pre-downloaded, pre-decoded
    /// <see cref="Bitmap"/> objects so <see cref="SlideshowEngine"/> can
    /// always advance to the next image without waiting for I/O.
    ///
    /// How it works:
    ///   A single background <see cref="Task"/> (the "filler") runs a tight
    ///   loop: pick a random entry from <see cref="ImageIndex"/>, check the
    ///   <see cref="DiskCache"/>, download if needed, then write the decoded
    ///   bitmap into a bounded <see cref="Channel{T}"/>. The channel blocks
    ///   the filler when it is full — no runaway downloads.
    ///
    ///   <see cref="SlideshowEngine"/> calls <see cref="TakeAsync"/> which
    ///   reads from the channel's consumer end. If the channel is empty
    ///   (first run, or very slow connection) the engine awaits it — the UI
    ///   is never blocked because the engine runs on a background thread.
    ///
    /// Ownership of bitmaps:
    ///   Each <see cref="Bitmap"/> yielded by <see cref="TakeAsync"/> is
    ///   owned by the caller. The caller must dispose it after use.
    /// </summary>
    public sealed class PrefetchQueue : IAsyncDisposable
    {
        private readonly Channel<PrefetchedImage> _channel;
        private readonly DiskCache _diskCache;
        private readonly Func<CloudImageEntry, CancellationToken, Task<System.IO.Stream>> _downloadFactory;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _fillerTask;

        // Replaced atomically when the index is refreshed.
        private volatile ImageIndex _index;

        public PrefetchQueue(
            ImageIndex initialIndex,
            DiskCache diskCache,
            Func<CloudImageEntry, CancellationToken, Task<System.IO.Stream>> downloadFactory,
            int prefetchCount)
        {
            if (prefetchCount < 1) prefetchCount = 1;

            _index = initialIndex;
            _diskCache = diskCache;
            _downloadFactory = downloadFactory;

            // Bounded channel: filler blocks when full, consumer blocks when empty.
            _channel = Channel.CreateBounded<PrefetchedImage>(new BoundedChannelOptions(prefetchCount)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            _fillerTask = Task.Run(FillLoopAsync);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the next pre-fetched image, waiting if the queue is empty.
        /// The caller takes ownership of the <see cref="Bitmap"/> and must
        /// dispose it when the image is no longer displayed.
        /// </summary>
        public ValueTask<PrefetchedImage> TakeAsync(CancellationToken ct = default)
            => _channel.Reader.ReadAsync(ct);

        /// <summary>
        /// Replaces the image index (e.g. after a background refresh from the
        /// cloud). The filler picks up the new index on its next iteration —
        /// no restart needed.
        /// </summary>
        public void UpdateIndex(ImageIndex newIndex)
            => _index = newIndex;

        /// <summary>
        /// Stops the background filler and releases resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            try { await _fillerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }

            _cts.Dispose();

            // Drain any buffered bitmaps to avoid leaking GDI handles.
            while (_channel.Reader.TryRead(out var leftover))
                leftover.Bitmap.Dispose();
        }

        // ── Background filler ─────────────────────────────────────────────────

        private async Task FillLoopAsync()
        {
            var ct = _cts.Token;

            while (!ct.IsCancellationRequested)
            {
                var entry = _index.Pick();

                if (entry is null)
                {
                    // Index is empty — wait a bit and retry.
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    System.Diagnostics.Trace.TraceInformation(
                        "[PrefetchQueue] Downloading '{0}'…", entry.Name);

                    var bitmap = await _diskCache
                        .GetOrAddAsync(entry, _downloadFactory, ct)
                        .ConfigureAwait(false);

                    System.Diagnostics.Trace.TraceInformation(
                        "[PrefetchQueue] Ready '{0}'.", entry.Name);

                    var prefetched = new PrefetchedImage(entry, bitmap);

                    // WriteAsync blocks here if the channel is full — this is
                    // the backpressure mechanism. No extra code needed.
                    await _channel.Writer.WriteAsync(prefetched, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Log and skip this image — don't crash the filler.
                    System.Diagnostics.Trace.TraceWarning(
                        "[PrefetchQueue] Download failed for '{0}': {1}: {2}",
                        entry?.Name, ex.GetType().Name, ex.Message);

                    // Brief pause to avoid hammering on persistent errors.
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
            }

            _channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// A pre-downloaded, pre-decoded image ready to display.
    /// The receiver owns the <see cref="Bitmap"/> and must dispose it.
    /// </summary>
    public sealed record PrefetchedImage(
        CloudImageEntry Entry,
        Bitmap Bitmap) : IDisposable
    {
        public void Dispose() => Bitmap.Dispose();
    }
}