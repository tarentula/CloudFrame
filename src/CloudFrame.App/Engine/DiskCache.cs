using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CloudFrame.Core.Cloud;

namespace CloudFrame.App.Engine
{
    /// <summary>
    /// On-disk LRU image cache. Stores images as screen-resolution JPEGs so
    /// we never keep 24 MP originals around. Evicts least-recently-used files
    /// when the size limit is approached.
    ///
    /// Thread safety: all public methods are safe to call from any thread.
    /// The LRU index is protected by a single <see cref="ReaderWriterLockSlim"/>.
    ///
    /// Cache key: "{accountId}_{itemId}.jpg" — stable across sessions.
    /// </summary>
    public sealed class DiskCache : IDisposable
    {
        private readonly string _cacheDir;
        private readonly long _limitBytes;
        private readonly int _maxDimension;
        private readonly ReaderWriterLockSlim _lock = new();

        // LRU index: key → (file path, size in bytes, last access tick).
        // Ordered by insertion; we move entries to the end on access.
        private readonly Dictionary<string, CacheEntry> _index = new();

        private long _totalBytes;
        private bool _disposed;

        private sealed record CacheEntry(string Path, long SizeBytes)
        {
            public long LastAccessTicks { get; set; } = Environment.TickCount64;
        }

        /// <param name="cacheDir">Folder to store cached images.</param>
        /// <param name="limitMb">Maximum cache size in megabytes.</param>
        /// <param name="maxDimension">
        /// Images are downscaled so their longest edge fits within this value.
        /// Pass 0 to use the primary screen's longest edge automatically.
        /// </param>
        public DiskCache(string cacheDir, int limitMb, int maxDimension = 0)
        {
            _cacheDir = cacheDir;
            _limitBytes = (long)limitMb * 1024 * 1024;
            _maxDimension = maxDimension > 0
                ? maxDimension
                : Math.Max(
                    System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920,
                    System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080);

            Directory.CreateDirectory(_cacheDir);
            RebuildIndexFromDisk();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Tries to load a cached <see cref="Bitmap"/> for the given entry.
        /// Returns null on a cache miss.
        /// </summary>
        public Bitmap? TryGet(CloudImageEntry entry)
        {
            string key = MakeKey(entry);
            string? filePath = null;

            _lock.EnterReadLock();
            try
            {
                if (_index.TryGetValue(key, out var cached))
                {
                    cached.LastAccessTicks = Environment.TickCount64;
                    filePath = cached.Path;
                }
            }
            finally { _lock.ExitReadLock(); }

            if (filePath is null) return null;

            try
            {
                // Load into a MemoryStream first so the file handle is released
                // immediately — avoids "file in use" errors during eviction.
                var bytes = File.ReadAllBytes(filePath);
                return new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception ex) when (ex is IOException or OutOfMemoryException)
            {
                // File was deleted between the index lookup and the read
                // (e.g. evicted by another thread). Treat as a cache miss.
                RemoveFromIndex(key);
                return null;
            }
        }

        /// <summary>
        /// Downloads the image stream, downscales it to screen resolution,
        /// writes it to the cache, and returns the decoded <see cref="Bitmap"/>.
        /// If the entry is already cached, returns the cached copy.
        /// </summary>
        public async Task<Bitmap> GetOrAddAsync(
            CloudImageEntry entry,
            Func<CloudImageEntry, CancellationToken, Task<Stream>> downloadFactory,
            CancellationToken ct = default)
        {
            // Check cache first.
            var cached = TryGet(entry);
            if (cached is not null) return cached;

            // Download and decode.
            await using var stream = await downloadFactory(entry, ct).ConfigureAwait(false);
            using var original = new Bitmap(stream);

            var resized = Downscale(original, _maxDimension);

            // Write to disk.
            string key = MakeKey(entry);
            string filePath = Path.Combine(_cacheDir, key + ".jpg");

            await Task.Run(() => SaveAsJpeg(resized, filePath), ct).ConfigureAwait(false);

            long sizeBytes = new FileInfo(filePath).Length;
            AddToIndex(key, filePath, sizeBytes);
            await EvictIfNeededAsync(ct).ConfigureAwait(false);

            return resized;
        }

        /// <summary>
        /// Removes all cached files and resets the index.
        /// </summary>
        public void Clear()
        {
            _lock.EnterWriteLock();
            try
            {
                foreach (var entry in _index.Values)
                    TryDeleteFile(entry.Path);

                _index.Clear();
                _totalBytes = 0;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lock.Dispose();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void RebuildIndexFromDisk()
        {
            // On startup, scan the cache folder and rebuild the LRU index from
            // existing files. This means the cache survives app restarts.
            var files = Directory.GetFiles(_cacheDir, "*.jpg");

            _lock.EnterWriteLock();
            try
            {
                foreach (var path in files)
                {
                    var info = new FileInfo(path);
                    string key = Path.GetFileNameWithoutExtension(path);
                    var entry = new CacheEntry(path, info.Length)
                    {
                        LastAccessTicks = info.LastAccessTimeUtc.Ticks
                    };
                    _index[key] = entry;
                    _totalBytes += info.Length;
                }
            }
            finally { _lock.ExitWriteLock(); }

            // Evict synchronously on startup if we're already over limit.
            // (This can happen if the limit was reduced in settings.)
            EvictIfNeededSync();
        }

        private void AddToIndex(string key, string path, long sizeBytes)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_index.TryGetValue(key, out var existing))
                    _totalBytes -= existing.SizeBytes;

                _index[key] = new CacheEntry(path, sizeBytes);
                _totalBytes += sizeBytes;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private void RemoveFromIndex(string key)
        {
            _lock.EnterWriteLock();
            try
            {
                if (_index.Remove(key, out var entry))
                    _totalBytes -= entry.SizeBytes;
            }
            finally { _lock.ExitWriteLock(); }
        }

        private Task EvictIfNeededAsync(CancellationToken ct)
        {
            if (_totalBytes <= _limitBytes) return Task.CompletedTask;
            return Task.Run(EvictIfNeededSync, ct);
        }

        private void EvictIfNeededSync()
        {
            _lock.EnterWriteLock();
            try
            {
                while (_totalBytes > _limitBytes && _index.Count > 0)
                {
                    // Find the least-recently-used entry.
                    var lru = _index.Values
                        .OrderBy(e => e.LastAccessTicks)
                        .First();

                    string keyToRemove = _index
                        .First(kv => kv.Value == lru).Key;

                    _index.Remove(keyToRemove);
                    _totalBytes -= lru.SizeBytes;
                    TryDeleteFile(lru.Path);
                }
            }
            finally { _lock.ExitWriteLock(); }
        }

        private static Bitmap Downscale(Bitmap source, int maxDimension)
        {
            int w = source.Width;
            int h = source.Height;
            int longest = Math.Max(w, h);

            if (longest <= maxDimension) return new Bitmap(source);

            double scale = (double)maxDimension / longest;
            int newW = (int)(w * scale);
            int newH = (int)(h * scale);

            var result = new Bitmap(newW, newH);
            using var g = Graphics.FromImage(result);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, newW, newH);
            return result;
        }

        private static void SaveAsJpeg(Bitmap bmp, string path)
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 88L);

            bmp.Save(path, encoder, parameters);
        }

        private static void TryDeleteFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { /* ignore — file may be in use */ }
        }

        private static string MakeKey(CloudImageEntry entry)
            => $"{entry.AccountId}_{entry.Id}"
                .Replace("/", "_")
                .Replace("\\", "_");
    }
}
