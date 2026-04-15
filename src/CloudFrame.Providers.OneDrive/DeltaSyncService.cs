using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CloudFrame.Core.Cloud;

namespace CloudFrame.Providers.OneDrive
{
    /// <summary>
    /// Uses the Microsoft Graph Delta API to fetch only changes since the
    /// last sync rather than re-scanning the entire folder tree.
    ///
    /// How it works:
    ///   1. First call: no delta token exists → performs a full scan and
    ///      saves the delta token returned by Graph at the end.
    ///   2. Subsequent calls: sends the saved token → Graph returns only
    ///      items added, modified, or deleted since last sync.
    ///   3. The caller merges the delta into the existing raw index.
    ///
    /// Delta tokens are per-folder-root and stored in the index cache
    /// directory as "{accountId}_{folderHash}.deltatoken".
    ///
    /// Reference: https://learn.microsoft.com/graph/delta-query-files
    /// </summary>
    public sealed class DeltaSyncService
    {
        private const string GraphBase = "https://graph.microsoft.com/v1.0";

        private static readonly string[] s_imageExtensions =
            [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tiff", ".tif"];

        private static readonly JsonSerializerOptions s_json = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _http;
        private readonly string _tokenDir;

        public DeltaSyncService(HttpClient http, string? tokenDirectory = null)
        {
            _http = http;
            _tokenDir = tokenDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudFrame");

            Directory.CreateDirectory(_tokenDir);
        }

        /// <summary>
        /// Syncs changes for the given folder root into <paramref name="existing"/>.
        /// On first call (no saved token) performs a full scan.
        /// On subsequent calls fetches only the delta.
        ///
        /// Returns (changed, tokenExpired, entries).
        /// <c>tokenExpired = true</c> means the saved delta token was rejected (410 Gone)
        /// and the caller should fall back to a full scan and reset the token.
        /// </summary>
        public async Task<(bool changed, bool tokenExpired, List<CloudImageEntry> entries)> SyncAsync(
            string accessToken,
            string accountId,
            string folderPath,
            List<CloudImageEntry> existing,
            Action<int, int>? onProgress = null,
            Action<List<CloudImageEntry>>? onEarlyResults = null,
            CancellationToken ct = default)
        {
            string tokenPath = GetTokenPath(accountId, folderPath);
            string? savedToken = LoadToken(tokenPath);

            Trace.TraceInformation(
                "[DeltaSync] '{0}' folder '{1}': {2}, existing {3} entries.",
                accountId, folderPath,
                savedToken is null ? "full scan (no token)" : "incremental delta",
                existing.Count);

            // Build the starting URL — either from the saved delta token or
            // a fresh delta request for this folder.
            string startUrl = savedToken is not null
                ? savedToken   // Graph delta tokens are full URLs
                : BuildDeltaUrl(folderPath);

            var added = new Dictionary<string, CloudImageEntry>();
            var deleted = new HashSet<string>();
            string? newToken = null;
            int imageCount = existing.Count;
            int folderCount = 0;
            bool changed = false;
            bool earlyResultsFired = false;

            string? nextUrl = startUrl;

            while (nextUrl is not null && !ct.IsCancellationRequested)
            {
                var (page, tokenExpired) = await FetchPageAsync(accessToken, nextUrl, ct)
                    .ConfigureAwait(false);

                if (tokenExpired)
                {
                    // 410 Gone — our delta token is stale. Signal caller to fall back.
                    Trace.TraceWarning(
                        "[DeltaSync] 410 Gone for '{0}' folder '{1}' — token expired.",
                        accountId, folderPath);
                    return (false, true, existing);
                }

                if (page is null) break;

                foreach (var item in page.Value ?? [])
                {
                    if (item.Deleted is not null)
                    {
                        // Item was deleted — remove from index.
                        deleted.Add(item.Id);
                        changed = true;
                    }
                    else if (item.Folder is not null)
                    {
                        folderCount++;
                    }
                    else if (IsImage(item.Name))
                    {
                        // Build the full relative path using parentReference so
                        // nested items (e.g. Photos/2023/Summer/img.jpg) get the
                        // correct path rather than just Photos/img.jpg.
                        string relativePath = BuildRelativePath(folderPath, item);

                        added[item.Id] = new CloudImageEntry
                        {
                            Id = item.Id,
                            Name = item.Name,
                            RelativePath = relativePath,
                            SizeBytes = item.Size,
                            LastModified = item.LastModified,
                            AccountId = accountId
                        };
                        imageCount++;
                        changed = true;
                    }
                }

                onProgress?.Invoke(imageCount, folderCount);

                // On a full scan (no saved token), fire onEarlyResults once as soon
                // as we have enough entries to start the slideshow — no need to wait
                // for all 50k+ pages before showing images.
                if (onEarlyResults is not null && !earlyResultsFired
                    && savedToken is null && added.Count > 3)
                {
                    earlyResultsFired = true;
                    onEarlyResults(new List<CloudImageEntry>(added.Values));
                }

                // Graph uses @odata.nextLink for more pages and
                // @odata.deltaLink when the sync is complete.
                if (page.DeltaLink is not null)
                {
                    newToken = page.DeltaLink;
                    break;
                }

                nextUrl = page.NextLink;
            }

            // Merge delta into existing list.
            // For a full scan (savedToken == null) Graph returns ALL current items,
            // so `added` already contains everything — merging `existing` on top would
            // create duplicates for every item already in the cache.  Skip `existing`
            // in that case and just use what Graph gave us.
            bool isFullScan = savedToken is null;
            var result = new List<CloudImageEntry>(isFullScan ? added.Count : existing.Count + added.Count);
            if (!isFullScan)
            {
                foreach (var entry in existing)
                {
                    if (!deleted.Contains(entry.Id) && !added.ContainsKey(entry.Id))
                        result.Add(entry);
                }
            }
            foreach (var entry in added.Values)
                result.Add(entry);

            // Save the new delta token for next time.
            if (newToken is not null)
                SaveToken(tokenPath, newToken);

            Trace.TraceInformation(
                "[DeltaSync] '{0}' folder '{1}' complete: {2} entries, changed={3}.",
                accountId, folderPath, result.Count, changed);

            return (changed, false, result);
        }

        /// <summary>
        /// Deletes the saved delta token for this account/folder, forcing a
        /// full re-scan on the next sync. Call when settings change.
        /// </summary>
        public void InvalidateToken(string accountId, string folderPath)
        {
            string path = GetTokenPath(accountId, folderPath);
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>Returns true if a delta token exists for this folder.</summary>
        public bool HasToken(string accountId, string folderPath)
            => File.Exists(GetTokenPath(accountId, folderPath));

        // ── Private ────────────────────────────────────────────────────────────

        private string BuildDeltaUrl(string folderPath)
        {
            // Include parentReference so we can reconstruct the full nested path
            // for items deep in the folder tree (e.g. Photos/2023/Summer/img.jpg).
            const string select = "$select=id,name,size,lastModifiedDateTime,folder,deleted,parentReference&$top=200";

            if (string.IsNullOrWhiteSpace(folderPath))
                return $"{GraphBase}/me/drive/root/delta?{select}";

            string encoded = Uri.EscapeDataString(folderPath).Replace("%2F", "/");
            return $"{GraphBase}/me/drive/root:/{encoded}:/delta?{select}";
        }

        /// <summary>
        /// Builds the full OneDrive-root-relative path for a delta item using
        /// its parentReference.path, which Graph returns as e.g.
        /// <c>/drive/root:/Photos/2023/Summer</c>.
        /// </summary>
        private static string BuildRelativePath(string folderPath, DeltaItem item)
        {
            const string driveRootPrefix = "/drive/root:";

            if (item.ParentReference?.Path is { Length: > 0 } parentPath
                && parentPath.StartsWith(driveRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string folder = parentPath[driveRootPrefix.Length..].Trim('/');
                return folder.Length == 0 ? item.Name : $"{folder}/{item.Name}";
            }

            // Fallback: simple folder + name (accurate for root-level items).
            return string.IsNullOrEmpty(folderPath) ? item.Name : $"{folderPath}/{item.Name}";
        }

        private async Task<(DeltaPage? page, bool tokenExpired)> FetchPageAsync(
            string accessToken, string url, CancellationToken ct)
        {
            const int maxAttempts = 5;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.Gone)
                {
                    // 410 Gone — delta token expired. Caller should invalidate
                    // and retry with a full scan.
                    return (null, true);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (null, false);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var delay = response.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 2));
                    Trace.TraceWarning(
                        "[DeltaSync] 429 throttled (attempt {0}/{1}); backing off {2:F0}s.",
                        attempt + 1, maxAttempts, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Trace.TraceError(
                        "[DeltaSync] HTTP {0} {1} for '{2}' (attempt {3}/{4}).",
                        (int)response.StatusCode, response.ReasonPhrase, url,
                        attempt + 1, maxAttempts);
                    return (null, false);
                }

                await using var stream = await response.Content
                    .ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);

                return (await JsonSerializer
                    .DeserializeAsync<DeltaPage>(stream, s_json, ct)
                    .ConfigureAwait(false), false);
            }

            Trace.TraceWarning("[DeltaSync] All retry attempts exhausted for '{0}'; skipping.", url);
            return (null, false);
        }

        private string GetTokenPath(string accountId, string folderPath)
        {
            // Encode the folder path into a filesystem-safe name.
            // We cannot use string.GetHashCode() — it is randomised per-process in
            // .NET 5+ and would produce a different filename on every app launch.
            string safeName = string.IsNullOrEmpty(folderPath)
                ? "root"
                : folderPath.Replace('/', '-').Replace('\\', '-')
                            .Trim('-')
                            .ToLowerInvariant();
            return Path.Combine(_tokenDir, $"{accountId}_{safeName}.deltatoken");
        }

        private static string? LoadToken(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch { return null; }
        }

        private static void SaveToken(string path, string token)
        {
            try { File.WriteAllText(path, token); }
            catch { /* non-fatal */ }
        }

        private static bool IsImage(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var ext = Path.GetExtension(name);
            return Array.Exists(s_imageExtensions,
                e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        // ── Graph API DTOs ─────────────────────────────────────────────────────

        private sealed class DeltaPage
        {
            [JsonPropertyName("value")]
            public List<DeltaItem>? Value { get; init; }

            [JsonPropertyName("@odata.nextLink")]
            public string? NextLink { get; init; }

            [JsonPropertyName("@odata.deltaLink")]
            public string? DeltaLink { get; init; }
        }

        private sealed class DeltaItem
        {
            [JsonPropertyName("id")]
            public string Id { get; init; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; init; }

            [JsonPropertyName("lastModifiedDateTime")]
            public DateTimeOffset LastModified { get; init; }

            [JsonPropertyName("folder")]
            public object? Folder { get; init; }

            /// <summary>Non-null when the item was deleted.</summary>
            [JsonPropertyName("deleted")]
            public object? Deleted { get; init; }

            /// <summary>
            /// Parent folder reference — used to reconstruct the item's full
            /// relative path within the OneDrive root.
            /// </summary>
            [JsonPropertyName("parentReference")]
            public ParentRef? ParentReference { get; init; }
        }

        private sealed class ParentRef
        {
            /// <summary>
            /// Graph-encoded path such as <c>/drive/root:/Photos/2023/Summer</c>.
            /// </summary>
            [JsonPropertyName("path")]
            public string? Path { get; init; }
        }
    }
}