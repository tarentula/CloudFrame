using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CloudFrame.Core.Cloud;
using CloudFrame.Core.Config;

namespace CloudFrame.Core.Index
{
    /// <summary>
    /// Persists the raw (unfiltered) list of <see cref="CloudImageEntry"/> objects
    /// per account to a JSON file on disk.
    ///
    /// Fast-startup strategy:
    ///   On subsequent launches, <see cref="TryLoadAsync"/> restores the last
    ///   known index from disk in &lt;100 ms. The app starts showing images
    ///   immediately while <see cref="SaveAsync"/> is called in the background
    ///   once a fresh listing from the cloud provider is available.
    /// </summary>
    public sealed class IndexCacheService
    {
        private static readonly JsonSerializerOptions s_opts = new()
        {
            WriteIndented = false,   // compact — file can be several MB
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string _cachePath;

        public IndexCacheService(string? cachePath = null)
        {
            _cachePath = cachePath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudFrame",
                    "index.json");
        }

        /// <summary>
        /// Attempts to load the cached index from disk.
        /// Returns an empty dictionary on any failure (missing file, parse error).
        /// Key = AccountId, Value = list of entries for that account.
        /// </summary>
        public async Task<Dictionary<string, List<CloudImageEntry>>> TryLoadAsync(
            CancellationToken ct = default)
        {
            if (!File.Exists(_cachePath))
                return new Dictionary<string, List<CloudImageEntry>>();

            try
            {
                await using var stream = File.OpenRead(_cachePath);
                var cached = await JsonSerializer
                    .DeserializeAsync<IndexCacheFile>(stream, s_opts, ct)
                    .ConfigureAwait(false);

                var accounts = cached?.Accounts
                    ?? new Dictionary<string, List<CloudImageEntry>>();

                int totalRaw = accounts.Values.Sum(list => list.Count);
                System.Diagnostics.Trace.TraceInformation(
                    "[IndexCache] Loaded {0} raw entries ({1} account(s)) saved at {2}.",
                    totalRaw, accounts.Count,
                    cached?.SavedAt.ToString("yyyy-MM-dd HH:mm:ss") ?? "?");

                return accounts;
            }
            catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "[IndexCache] Failed to load cache from '{0}': {1}: {2}",
                    _cachePath, ex.GetType().Name, ex.Message);
                return new Dictionary<string, List<CloudImageEntry>>();
            }
        }

        /// <summary>
        /// Writes the current entry lists to disk. Uses atomic replace (temp
        /// file + move) to avoid corruption if the process is killed mid-write.
        /// </summary>
        public async Task SaveAsync(
            Dictionary<string, List<CloudImageEntry>> accounts,
            CancellationToken ct = default)
        {
            var dir = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(dir);

            int totalRaw = accounts.Values.Sum(list => list.Count);
            System.Diagnostics.Trace.TraceInformation(
                "[IndexCache] Saving {0} raw entries ({1} account(s)) to '{2}'.",
                totalRaw, accounts.Count, _cachePath);

            var file = new IndexCacheFile
            {
                SavedAt = DateTimeOffset.UtcNow,
                Accounts = accounts
            };

            var tmp = _cachePath + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, file, s_opts, ct)
                    .ConfigureAwait(false);
            }

            File.Move(tmp, _cachePath, overwrite: true);

            long fileSizeKb = new System.IO.FileInfo(_cachePath).Length / 1024;
            System.Diagnostics.Trace.TraceInformation(
                "[IndexCache] Save complete — {0} KB on disk.", fileSizeKb);
        }

        /// <summary>Returns the UTC time the index was last written, or null.</summary>
        public async Task<DateTimeOffset?> GetLastSavedAtAsync(CancellationToken ct = default)
        {
            if (!File.Exists(_cachePath)) return null;

            try
            {
                await using var stream = File.OpenRead(_cachePath);
                var cached = await JsonSerializer
                    .DeserializeAsync<IndexCacheFile>(stream, s_opts, ct)
                    .ConfigureAwait(false);
                return cached?.SavedAt;
            }
            catch
            {
                return null;
            }
        }

        // ── Private DTO ───────────────────────────────────────────────────────

        private sealed class IndexCacheFile
        {
            public DateTimeOffset SavedAt { get; set; }
            public Dictionary<string, List<CloudImageEntry>> Accounts { get; set; } = new();
        }
    }
}
