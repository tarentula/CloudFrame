using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CloudFrame.Providers.OneDrive
{
    /// <summary>
    /// Minimal Microsoft Graph API client scoped to the operations CloudFrame needs.
    /// Uses a single shared <see cref="HttpClient"/> (injected) to respect the
    /// "create once, reuse" best practice and avoid socket exhaustion.
    ///
    /// Only two operations are needed:
    ///   1. List image files under a folder path (with paging).
    ///   2. Get a download URL / thumbnail URL for a specific item.
    /// </summary>
    internal sealed class GraphApiClient
    {
        private const string GraphBase = "https://graph.microsoft.com/v1.0";

        // Supported image extensions — Graph filter is case-insensitive.
        private static readonly string[] s_imageExtensions =
            [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tiff", ".tif"];

        private static readonly JsonSerializerOptions s_json = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _http;

        public GraphApiClient(HttpClient http)
        {
            _http = http;
        }

        // ── File listing ──────────────────────────────────────────────────────

        /// <summary>
        /// Yields all image files found recursively under <paramref name="folderPath"/>
        /// in the user's OneDrive. Handles Graph API paging transparently.
        ///
        /// <paramref name="folderPath"/> is relative to the OneDrive root,
        /// e.g. "Pictures" or "Phone Camera/2023". Use empty string for root.
        ///
        /// Graph API is called with $select to fetch only the fields we need,
        /// keeping response payloads small.
        /// </summary>
        // Max concurrent Graph API requests. 8 keeps us well under Microsoft's
        // throttle limit (10 000 requests per 10 minutes per app) while still
        // scanning a large folder tree many times faster than sequential.
        private const int MaxConcurrency = 8;

        /// <summary>
        /// Yields all image files found recursively under <paramref name="folderPath"/>.
        /// Scans sub-folders in parallel (up to <see cref="MaxConcurrency"/> at once)
        /// for significantly faster traversal of large folder trees.
        ///
        /// <paramref name="onProgress"/> is called each time a batch of items is
        /// received, with the current running totals. It is called on thread-pool
        /// threads — callers must marshal to the UI thread themselves.
        /// </summary>
        public async IAsyncEnumerable<GraphItem> ListImagesAsync(
            string accessToken,
            string folderPath,
            Action<int, int>? onProgress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // We collect results into a Channel so parallel folder-scanning tasks
            // can write concurrently while we yield results to the caller sequentially.
            var channel = System.Threading.Channels.Channel.CreateUnbounded<GraphItem>(
                new System.Threading.Channels.UnboundedChannelOptions
                {
                    SingleReader = true,
                    AllowSynchronousContinuations = false
                });

            string startRef = string.IsNullOrWhiteSpace(folderPath)
                ? "root"
                : $"root:/{Uri.EscapeDataString(folderPath).Replace("%2F", "/")}:";

            // Semaphore caps parallel Graph API requests.
            var sem = new System.Threading.SemaphoreSlim(MaxConcurrency, MaxConcurrency);

            var counters = new ScanCounters();

            // Run the recursive scan on a background task so we can yield
            // from the channel concurrently.
            var scanTask = Task.Run(async () =>
            {
                try
                {
                    await ScanFolderAsync(
                        accessToken, startRef, channel.Writer,
                        sem, counters, onProgress, ct);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, ct);

            // Yield results as they arrive from the channel.
            await foreach (var item in channel.Reader.ReadAllAsync(ct))
                yield return item;

            // Propagate any exception from the scan task.
            await scanTask;
        }

        // Shared mutable counters for parallel scan tasks.
        // Using a class avoids the ref-in-async restriction.
        private sealed class ScanCounters
        {
            public int Images;
            public int Folders;
        }

        // Milestone interval for scan progress trace entries.
        private const int TraceMilestone = 10_000;

        private async Task ScanFolderAsync(
            string accessToken,
            string itemRef,
            System.Threading.Channels.ChannelWriter<GraphItem> writer,
            System.Threading.SemaphoreSlim sem,
            ScanCounters counters,
            Action<int, int>? onProgress,
            CancellationToken ct)
        {
            var subFolders = new List<string>();
            string? nextPageUrl = BuildChildrenUrl(itemRef);

            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (nextPageUrl is not null && !ct.IsCancellationRequested)
                {
                    var page = await GetPageAsync<GraphItemPage>(accessToken, nextPageUrl, ct)
                        .ConfigureAwait(false);

                    if (page?.Value is null)
                    {
                        Trace.TraceWarning(
                            "[Graph] Null page returned for folder '{0}'; stopping paging.", itemRef);
                        break;
                    }

                    foreach (var item in page.Value)
                    {
                        if (item.Folder is not null)
                        {
                            subFolders.Add($"items/{item.Id}");
                        }
                        else if (IsImage(item.Name))
                        {
                            await writer.WriteAsync(item, ct).ConfigureAwait(false);
                            int newCount = System.Threading.Interlocked.Increment(ref counters.Images);

                            // Emit a trace entry at each milestone so progress
                            // is visible even when the UI callback is throttled.
                            if (newCount % TraceMilestone == 0)
                            {
                                Trace.TraceInformation(
                                    "[Graph] Scan milestone: {0:N0} images, {1:N0} folders enumerated.",
                                    newCount,
                                    System.Threading.Volatile.Read(ref counters.Folders));
                            }
                        }
                    }

                    onProgress?.Invoke(
                        System.Threading.Volatile.Read(ref counters.Images),
                        System.Threading.Volatile.Read(ref counters.Folders));

                    nextPageUrl = page.ODataNextLink;
                }
            }
            finally
            {
                sem.Release();
            }

            System.Threading.Interlocked.Add(ref counters.Folders, subFolders.Count);

            if (subFolders.Count > 0)
            {
                // Wrap each subfolder scan so one failure doesn't abort siblings.
                async Task ScanSafe(string sub)
                {
                    try
                    {
                        await ScanFolderAsync(accessToken, sub, writer, sem, counters, onProgress, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Log the failure but don't abort sibling folder scans.
                        Trace.TraceError(
                            "[Graph] Subfolder '{0}' skipped after error ({1}): {2}",
                            sub, ex.GetType().Name, ex.Message);
                    }
                }

                var tasks = new List<Task>(subFolders.Count);
                foreach (var sub in subFolders)
                    tasks.Add(ScanSafe(sub));

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        // ── Download / thumbnail ──────────────────────────────────────────────

        /// <summary>
        /// Returns the best available download URL for the item at the requested
        /// size. Prefers a server-generated thumbnail (avoids downloading the
        /// full original) and falls back to the direct download URL.
        ///
        /// The returned URL is a pre-authenticated short-lived link — valid for
        /// a few hours, no Authorization header needed.
        /// </summary>
        public async Task<string?> GetDownloadUrlAsync(
            string accessToken,
            string itemId,
            int maxDimensionPixels,
            CancellationToken ct = default)
        {
            // Custom thumbnail sizes (cNxN) are not supported by personal OneDrive
            // accounts and return an OData InternalServerError even when under the 1500px
            // limit. Use only the pre-built named sizes: "medium" (≤176px) or "large"
            // (800×800) for everything else. If the thumbnail request fails or returns no
            // URL, fall through to the direct download URL from the item metadata.
            string sizeTag = maxDimensionPixels <= 176 ? "medium" : "large";

            string url = $"{GraphBase}/me/drive/items/{itemId}/thumbnails/0/{sizeTag}";
            var thumb = await GetPageAsync<GraphThumbnail>(accessToken, url, ct)
                .ConfigureAwait(false);

            if (thumb?.Url is { Length: > 0 } thumbUrl)
                return thumbUrl;

            // Final fallback: get the direct download URL from the item metadata.
            string itemUrl = $"{GraphBase}/me/drive/items/{itemId}" +
                             "?$select=id,@microsoft.graph.downloadUrl";
            var item = await GetPageAsync<GraphItem>(accessToken, itemUrl, ct)
                .ConfigureAwait(false);

            return item?.DownloadUrl;
        }

        /// <summary>
        /// Opens and returns a readable stream from a pre-authenticated
        /// download URL. The caller is responsible for disposing the stream.
        /// </summary>
        public async Task<System.IO.Stream> OpenStreamAsync(
            string downloadUrl,
            CancellationToken ct = default)
        {
            // No Authorization header — the URL is self-authenticating.
            var response = await _http
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<T?> GetPageAsync<T>(
            string accessToken,
            string url,
            CancellationToken ct)
        {
            const int maxAttempts = 5;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                // 401 → token expired mid-scan; treat as a miss so the caller
                //        skips this folder rather than crashing the whole scan.
                // 404 → folder doesn't exist, skip gracefully.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return default;

                // 429 → Graph is throttling. Respect Retry-After if present,
                //        otherwise use exponential back-off (4 s, 8 s, 16 s, …).
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var delay = response.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 2));
                    Trace.TraceWarning(
                        "[Graph] 429 throttled (attempt {0}/{1}) for '{2}'; backing off {3:F0}s",
                        attempt + 1, maxAttempts, url, delay.TotalSeconds);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Trace.TraceError(
                        "[Graph] HTTP {0} {1} for '{2}' (attempt {3}/{4})",
                        (int)response.StatusCode, response.ReasonPhrase, url,
                        attempt + 1, maxAttempts);
                }

                response.EnsureSuccessStatusCode();

                // Read into memory so we can log the body if JSON parsing fails.
                string body = await response.Content
                    .ReadAsStringAsync(ct)
                    .ConfigureAwait(false);

                // Guard against non-JSON responses (e.g. binary images returned when
                // the thumbnail endpoint redirects to the file directly, or HTML error
                // pages). Treat them as a miss rather than crashing the whole pipeline.
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase) &&
                    body.Length > 0 && body[0] != '{' && body[0] != '[')
                {
                    Trace.TraceWarning(
                        "[Graph] Non-JSON response (Content-Type: '{0}') for '{1}'; treating as miss.",
                        contentType, url);
                    return default;
                }

                try
                {
                    // Parse into a JsonDocument first so we can cheaply detect an
                    // OData error envelope {"error":{"code":"...","message":"..."}}
                    // before attempting to deserialize into T.  Without this check
                    // the error object would be silently treated as a "miss" because
                    // JsonSerializer ignores unknown properties and returns a non-null
                    // result with all fields at their default values.
                    using var doc = JsonDocument.Parse(body);

                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("error", out var errEl))
                    {
                        string code = errEl.TryGetProperty("code", out var c)
                            ? c.GetString() ?? "?" : "?";
                        string message = errEl.TryGetProperty("message", out var m)
                            ? m.GetString() ?? "?" : "?";
                        Trace.TraceWarning(
                            "[Graph] OData error for '{0}': {1} — {2}",
                            url, code, message);
                        return default;
                    }

                    // No error envelope — deserialize from the already-parsed document.
                    return doc.Deserialize<T>(s_json);
                }
                catch (JsonException jsonEx)
                {
                    // Body is not valid JSON at all (e.g. binary prefix, malformed
                    // OData quirk). Log the first 300 chars to aid diagnosis.
                    string preview = body.Length <= 300
                        ? body
                        : string.Concat(body.AsSpan(0, 300), "...");
                    Trace.TraceWarning(
                        "[Graph] JSON parse failed for '{0}': {1}. Body prefix: {2}",
                        url, jsonEx.Message, preview);
                    return default;
                }
            }

            // All retry attempts exhausted — treat as a transient miss so the
            // caller skips this folder rather than crashing the whole scan.
            Trace.TraceWarning(
                "[Graph] All {0} retry attempts exhausted for '{1}'; skipping.",
                maxAttempts, url);
            return default;
        }

        private static string BuildChildrenUrl(string itemRef)
            => $"{GraphBase}/me/drive/{itemRef}/children" +
               "?$select=id,name,size,lastModifiedDateTime,folder,@microsoft.graph.downloadUrl" +
               "&$top=200";   // 200 is the Graph API maximum page size

        private static bool IsImage(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var ext = System.IO.Path.GetExtension(name);
            return Array.Exists(s_imageExtensions,
                e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        // ── Graph API response DTOs ───────────────────────────────────────────

        internal sealed class GraphItemPage
        {
            [JsonPropertyName("value")]
            public List<GraphItem>? Value { get; init; }

            [JsonPropertyName("@odata.nextLink")]
            public string? ODataNextLink { get; init; }
        }

        internal sealed class GraphItem
        {
            [JsonPropertyName("id")]
            public string Id { get; init; } = string.Empty;

            [JsonPropertyName("name")]
            public string Name { get; init; } = string.Empty;

            [JsonPropertyName("size")]
            public long Size { get; init; }

            [JsonPropertyName("lastModifiedDateTime")]
            public DateTimeOffset LastModified { get; init; }

            /// <summary>Non-null if this item is a folder.</summary>
            [JsonPropertyName("folder")]
            public object? Folder { get; init; }

            /// <summary>Pre-authenticated download URL included in /children responses.</summary>
            [JsonPropertyName("@microsoft.graph.downloadUrl")]
            public string? DownloadUrl { get; init; }
        }

        internal sealed class GraphThumbnail
        {
            [JsonPropertyName("url")]
            public string? Url { get; init; }

            [JsonPropertyName("width")]
            public int Width { get; init; }

            [JsonPropertyName("height")]
            public int Height { get; init; }
        }
    }
}