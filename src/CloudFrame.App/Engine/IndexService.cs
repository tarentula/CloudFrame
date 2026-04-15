using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CloudFrame.Core.Cloud;
using CloudFrame.Providers.OneDrive;
using CloudFrame.Core.Config;
using CloudFrame.Core.Index;

namespace CloudFrame.App.Engine
{
    /// <summary>
    /// Orchestrates the two-phase index strategy that keeps startup fast:
    ///
    ///   Phase 1 (synchronous-ish, &lt;100 ms):
    ///     Load the last saved index from <see cref="IndexCacheService"/>,
    ///     apply current filters, and return an <see cref="ImageIndex"/> so
    ///     the slideshow can start immediately.
    ///
    ///   Phase 2 (background, seconds):
    ///     Call each enabled cloud provider's <c>ListFilesAsync</c>, merge the
    ///     results, re-apply filters, update the running
    ///     <see cref="PrefetchQueue"/>, and persist the refreshed index to disk.
    ///
    /// The engine receives a fresh <see cref="ImageIndex"/> via the
    /// <see cref="IndexRefreshed"/> event whenever Phase 2 completes.
    /// </summary>
    public sealed class IndexService
    {
        private readonly AppSettings _settings;
        private readonly IReadOnlyList<ICloudProvider> _providers;
        private readonly IndexCacheService _cache;
        private readonly DeltaSyncService _delta;
        private readonly CloudFrame.Core.Index.HiddenService _hidden;

        /// <summary>
        /// Raised on a thread-pool thread when a background refresh completes.
        /// </summary>
        public event Action<ImageIndex>? IndexRefreshed;

        public IndexService(
            AppSettings settings,
            IReadOnlyList<ICloudProvider> providers,
            IndexCacheService cache,
            CloudFrame.Core.Index.HiddenService? hidden = null,
            System.Net.Http.HttpClient? http = null)
        {
            _settings = settings;
            _providers = providers;
            _cache = cache;
            _hidden = hidden ?? new CloudFrame.Core.Index.HiddenService(
                string.IsNullOrEmpty(settings.IndexCachePath) ? null : settings.IndexCachePath);
            _delta = new DeltaSyncService(
                http ?? new System.Net.Http.HttpClient(),
                string.IsNullOrEmpty(settings.IndexCachePath) ? null : settings.IndexCachePath);
        }

        // ── Phase 1: fast startup ─────────────────────────────────────────────

        /// <summary>
        /// Loads the cached index from disk and returns an
        /// <see cref="ImageIndex"/> immediately. Returns
        /// <see cref="ImageIndex.Empty"/> if no cache exists yet.
        /// </summary>
        public async Task<ImageIndex> LoadCachedIndexAsync(CancellationToken ct = default)
        {
            // Load hidden list once before building any index.
            await _hidden.LoadAsync(ct).ConfigureAwait(false);
            var raw = await _cache.TryLoadAsync(ct).ConfigureAwait(false);
            return BuildIndex(raw);
        }

        /// <summary>
        /// Marks the given item ID as hidden, persists to disk, and returns an
        /// updated <see cref="ImageIndex"/> built from the last cached raw data
        /// with the newly hidden item excluded.
        /// </summary>
        public async Task<ImageIndex> HideAndRebuildAsync(
            string itemId, CancellationToken ct = default)
        {
            await _hidden.HideAsync(itemId, ct).ConfigureAwait(false);
            var raw = await _cache.TryLoadAsync(ct).ConfigureAwait(false);
            return BuildIndex(raw);
        }

        // ── Phase 2: background cloud refresh ─────────────────────────────────

        /// <summary>
        /// Refreshes the index from all enabled cloud providers in the
        /// background. Raises <see cref="IndexRefreshed"/> when done.
        /// Safe to call repeatedly (e.g. on a timer).
        /// </summary>
        /// <param name="onProgress">
        /// Optional callback invoked periodically during the scan with a
        /// human-readable progress string, e.g. "Scanning 'Pictures'… 142 images".
        /// Invoked on the thread pool — caller must marshal to UI thread if needed.
        /// Throttled to at most once per 50 items to avoid flooding the UI.
        /// </param>
        public async Task<ImageIndex> RefreshFromCloudAsync(
            CancellationToken ct = default,
            Action<string>? onProgress = null)
        {
            // Load existing cached entries once — used as the baseline for delta sync.
            var existingByAccount = await _cache.TryLoadAsync(ct).ConfigureAwait(false);

            var raw = new Dictionary<string, List<CloudImageEntry>>();
            int totalFound = 0;

            // Track whether we have already fired IndexRefreshed early (first-run
            // fast-start). Once fired we do not fire again until the full scan ends.
            bool earlyStartFired = false;
            object earlyStartLock = new();

            var overallSw = Stopwatch.StartNew();
            Trace.TraceInformation(
                "[IndexService] Cloud refresh started — {0} provider(s), cached accounts: {1}.",
                _providers.Count, existingByAccount.Count);

            foreach (var provider in _providers)
            {
                var accountConfig = _settings.Accounts
                    .FirstOrDefault(a => a.AccountId == provider.AccountId);

                if (accountConfig is null || !accountConfig.IsEnabled) continue;

                bool authenticated = await provider
                    .EnsureAuthenticatedAsync(ct)
                    .ConfigureAwait(false);

                if (!authenticated) continue;

                var token = provider.AccessToken!;

                var existingEntries = existingByAccount.TryGetValue(provider.AccountId, out var cached)
                    ? cached : new List<CloudImageEntry>();

                var folders = accountConfig.RootFolders.Count > 0
                    ? (IReadOnlyList<string>)accountConfig.RootFolders
                    : (IReadOnlyList<string>)[""];

                // Throttle UI progress callbacks to at most once per 500 ms.
                long lastProgressTick = 0;
                Action<int, int>? graphProgress = onProgress is null ? null :
                    (images, folders2) =>
                    {
                        long now = System.Diagnostics.Stopwatch.GetTimestamp();
                        long last = System.Threading.Interlocked.Read(ref lastProgressTick);
                        long elapsed = (now - last) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                        if (elapsed < 500) return;
                        if (System.Threading.Interlocked.CompareExchange(ref lastProgressTick, now, last) != last)
                            return;
                        onProgress($"Scanning OneDrive — {images:N0} images, {folders2:N0} folders found…");
                    };

                var accountSw = Stopwatch.StartNew();
                var entries = new List<CloudImageEntry>();

                Trace.TraceInformation(
                    "[IndexService] Account '{0}' ({1}): {2} root folder(s), {3} existing entries.",
                    accountConfig.AccountId, provider.DisplayName, folders.Count, existingEntries.Count);

                foreach (var folder in folders)
                {
                    // onEarlyResults: fire IndexRefreshed with the first batch of
                    // images so the slideshow can start before the scan is done.
                    // Only useful when we had no cached entries to begin with —
                    // if the engine is already running with thousands of cached
                    // images, replacing that index with a first-page batch of ~200
                    // would make the slideshow worse, not better.
                    Action<List<CloudImageEntry>> onEarly = earlyBatch =>
                    {
                        lock (earlyStartLock)
                        {
                            if (earlyStartFired) return;
                            // Skip early-start if the engine already has a meaningful
                            // cached index — the cached images are better than a
                            // tiny first-page sample.
                            if (existingEntries.Count > 3) return;
                            earlyStartFired = true;
                        }
                        var earlyRaw = new Dictionary<string, List<CloudImageEntry>>
                        {
                            [provider.AccountId] = earlyBatch
                        };
                        var earlyIndex = BuildIndex(earlyRaw);
                        if (earlyIndex.TotalCount > 3)
                        {
                            Trace.TraceInformation(
                                "[IndexService] Early-start: firing IndexRefreshed with {0} images.",
                                earlyIndex.TotalCount);
                            try { IndexRefreshed?.Invoke(earlyIndex); }
                            catch (Exception ex)
                            {
                                Trace.TraceError(
                                    "[IndexService] Exception in early IndexRefreshed handler: {0}: {1}",
                                    ex.GetType().Name, ex.Message);
                            }
                        }
                    };

                    var folderEntries = await ScanOneFolderAsync(
                        provider, token, accountConfig, folder,
                        existingEntries, graphProgress, onProgress,
                        onEarlyResults: onEarly, ct)
                        .ConfigureAwait(false);
                    entries.AddRange(folderEntries);
                }

                totalFound += entries.Count;

                Trace.TraceInformation(
                    "[IndexService] Finished account '{0}': {1:N0} images in {2:mm\\:ss}.",
                    accountConfig.AccountId, entries.Count, accountSw.Elapsed);

                if (entries.Count > 0)
                    raw[provider.AccountId] = entries;
            }

            Trace.TraceInformation(
                "[IndexService] Cloud refresh complete — {0:N0} images total in {1:mm\\:ss}.",
                totalFound, overallSw.Elapsed);

            // Persist raw (unfiltered) entries.
            Trace.TraceInformation("[IndexService] Saving index cache ({0} account(s))…", raw.Count);
            try
            {
                await _cache.SaveAsync(raw, ct).ConfigureAwait(false);
                Trace.TraceInformation("[IndexService] Index cache saved successfully.");
            }
            catch (Exception ex)
            {
                Trace.TraceError("[IndexService] Failed to save index cache: {0}: {1}",
                    ex.GetType().Name, ex.Message);
            }

            var index = BuildIndex(raw);

            // Fire the event in a try/catch so an exception in any subscriber
            // never prevents the built index from being returned to the caller.
            try
            {
                IndexRefreshed?.Invoke(index);
            }
            catch (Exception ex)
            {
                Trace.TraceError(
                    "[IndexService] Exception in IndexRefreshed handler — index was still built: {0}: {1}",
                    ex.GetType().Name, ex.Message);
            }

            return index;
        }

        /// <summary>
        /// Scans a single root folder for one account.
        /// Uses delta sync when a saved token exists (fast — only changes).
        /// Falls back to a full parallel scan when no token exists or when
        /// Graph returns 410 Gone (token invalidated).
        /// </summary>
        private async Task<List<CloudImageEntry>> ScanOneFolderAsync(
            ICloudProvider provider,
            string accessToken,
            AccountConfig accountConfig,
            string folder,
            List<CloudImageEntry> existingForAccount,
            Action<int, int>? graphProgress,
            Action<string>? onProgress,
            Action<List<CloudImageEntry>>? onEarlyResults,
            CancellationToken ct)
        {
            // Split existing entries to just those under this root folder.
            string prefix = string.IsNullOrEmpty(folder) ? "" : folder + "/";
            var folderExisting = string.IsNullOrEmpty(prefix)
                ? existingForAccount
                : existingForAccount
                    .Where(e => e.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            // Always route through DeltaSyncService so the delta token is saved at the
            // end of every scan (initial or incremental). When no token exists,
            // SyncAsync performs a full scan via the Graph Delta API and saves the
            // resulting @odata.deltaLink. Subsequent runs use that token for fast
            // incremental sync.
            bool hasToken = _delta.HasToken(accountConfig.AccountId, folder);

            // If a delta token exists but the local index is empty, the incremental
            // sync would return only recent changes (not the full image list), giving
            // a tiny result set instead of the full index. Force a full scan instead.
            if (hasToken && folderExisting.Count == 0)
            {
                _delta.InvalidateToken(accountConfig.AccountId, folder);
                hasToken = false;
                Trace.TraceWarning(
                    "[IndexService] Delta token exists but no cached entries for '{0}' folder '{1}' — forcing full scan to rebuild index.",
                    accountConfig.AccountId, folder);
            }

            string folderLabel = folder.Length == 0 ? "/" : folder;

            Trace.TraceInformation(
                "[IndexService] {0} for '{1}' folder '{2}'.",
                hasToken ? "Delta sync" : "Full scan",
                accountConfig.AccountId, folder);

            onProgress?.Invoke(hasToken
                ? $"Checking for changes ({provider.DisplayName}, '{folderLabel}') — {folderExisting.Count:N0} images in last index…"
                : $"Full scan starting for '{folderLabel}'…");

            try
            {
                var (changed, tokenExpired, deltaEntries) = await _delta
                    .SyncAsync(accessToken, accountConfig.AccountId, folder,
                               folderExisting, graphProgress,
                               onEarlyResults: onEarlyResults, ct)
                    .ConfigureAwait(false);

                if (!tokenExpired)
                {
                    onProgress?.Invoke(changed
                        ? $"Sync complete — {deltaEntries.Count:N0} images (updated)."
                        : $"No changes — {deltaEntries.Count:N0} images unchanged.");
                    return deltaEntries;
                }

                // 410 Gone — saved delta token was rejected. Invalidate it and
                // retry with an empty baseline so a fresh full scan is performed
                // and a new token is saved.
                _delta.InvalidateToken(accountConfig.AccountId, folder);
                Trace.TraceWarning(
                    "[IndexService] Delta token expired for '{0}' folder '{1}' — retrying with fresh scan.",
                    accountConfig.AccountId, folder);

                onProgress?.Invoke($"Token expired — rescanning '{folderLabel}'…");

                var (_, __, freshEntries) = await _delta
                    .SyncAsync(accessToken, accountConfig.AccountId, folder,
                               [], graphProgress,
                               onEarlyResults: onEarlyResults, ct)
                    .ConfigureAwait(false);

                onProgress?.Invoke($"Fresh scan complete — {freshEntries.Count:N0} images.");
                return freshEntries;
            }
            catch (OperationCanceledException)
            {
                Trace.TraceInformation(
                    "[IndexService] Scan cancelled for '{0}' folder '{1}'.",
                    accountConfig.AccountId, folder);
                throw;
            }
            catch (Exception ex)
            {
                Trace.TraceError(
                    "[IndexService] Scan failed for '{0}' folder '{1}': {2}: {3}.",
                    accountConfig.AccountId, folder, ex.GetType().Name, ex.Message);
                onProgress?.Invoke($"Scan error: {ex.Message}");
                // Return whatever was cached — better than nothing.
                return folderExisting;
            }
        }

        // ── Private ────────────────────────────────────────────────────────────

        /// <summary>
        /// Applies per-account filters and builds a weighted
        /// <see cref="ImageIndex"/> from raw entry lists.
        /// </summary>
        private ImageIndex BuildIndex(Dictionary<string, List<CloudImageEntry>> raw)
        {
            if (raw.Count == 0) return ImageIndex.Empty;

            int totalRaw = raw.Values.Sum(l => l.Count);

            var accountData = raw
                .Select(kv =>
                {
                    var config = _settings.Accounts
                        .FirstOrDefault(a => a.AccountId == kv.Key);

                    // Skip accounts removed from settings since last cache save.
                    if (config is null || !config.IsEnabled)
                        return ((AccountConfig Config, IReadOnlyList<CloudImageEntry> Entries)?)null;

                    // Filter out hidden items.
                    var entries = _hidden.Count == 0
                        ? (IReadOnlyList<CloudImageEntry>)kv.Value
                        : kv.Value.Where(e => !_hidden.IsHidden(e.Id)).ToList();

                    return (config, entries);
                })
                .Where(x => x is not null)
                .Select(x => x!.Value);

            var index = ImageIndex.Build(accountData);

            Trace.TraceInformation(
                "[IndexService] BuildIndex: {0} raw → {1} after filtering.",
                totalRaw, index.TotalCount);

            return index;
        }
    }
}