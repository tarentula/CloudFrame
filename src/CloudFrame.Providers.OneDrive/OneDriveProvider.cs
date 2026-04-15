using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CloudFrame.Core.Cloud;

namespace CloudFrame.Providers.OneDrive
{
    /// <summary>
    /// <see cref="ICloudProvider"/> implementation for personal Microsoft OneDrive
    /// accounts via the Microsoft Graph API.
    ///
    /// One instance per configured account. Injected with a shared
    /// <see cref="HttpClient"/> from the app's service root — never creates its own.
    ///
    /// Retry policy:
    ///   Graph API occasionally returns 429 (throttled) or 503 (transient).
    ///   We implement a simple exponential back-off for these cases rather than
    ///   pulling in a heavy resilience library.
    /// </summary>
    public sealed class OneDriveProvider : ICloudProvider
    {
        private readonly MsalAuthManager _auth;
        private readonly GraphApiClient _graph;

        // Retry settings for transient Graph API failures.
        private const int MaxRetries = 4;
        private static readonly TimeSpan[] s_retryDelays =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(20)
        ];

        public string AccountId { get; }
        public string DisplayName { get; }
        public string? AccessToken => _auth.AccessToken;

        public OneDriveProvider(
            string accountId,
            string displayName,
            HttpClient httpClient,
            string? edgeProfileFolder = null,
            string? cacheDirectory = null)
        {
            AccountId = accountId;
            DisplayName = displayName;
            _auth = new MsalAuthManager(accountId, edgeProfileFolder, cacheDirectory);
            _graph = new GraphApiClient(httpClient);
        }

        // ── ICloudProvider ────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<bool> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => _auth.EnsureAuthenticatedAsync(ct);

        /// <summary>
        /// Triggers the interactive browser login flow.
        /// Must be called from the UI thread (WinForms message loop).
        /// </summary>
        public Task<bool> SignInInteractiveAsync(CancellationToken ct = default)
            => _auth.AcquireTokenInteractiveAsync(ct);

        /// <summary>
        /// Removes cached credentials and signs the user out.
        /// </summary>
        public Task SignOutAsync() => _auth.SignOutAsync();

        /// <inheritdoc/>
        /// <remarks>
        /// Scans all configured root folders concurrently but yields results
        /// back to the caller sequentially to avoid overwhelming the index builder.
        ///
        /// The relative path stored in each <see cref="CloudImageEntry"/> is
        /// relative to the OneDrive root (not the individual root folder), so
        /// filter patterns like "*/private/*" work consistently regardless of
        /// which root folder the image lives in.
        /// </remarks>
        public async IAsyncEnumerable<CloudImageEntry> ListFilesAsync(
            IReadOnlyList<string> rootFolderPaths,
            Action<int, int>? onProgress = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var token = _auth.AccessToken
                ?? throw new InvalidOperationException(
                    "Not authenticated. Call EnsureAuthenticatedAsync first.");

            var folders = rootFolderPaths.Count > 0
                ? rootFolderPaths
                : (IReadOnlyList<string>)[""];

            foreach (var folder in folders)
            {
                await foreach (var item in _graph
                    .ListImagesAsync(token, folder, onProgress, ct)
                    .ConfigureAwait(false))
                {
                    string relativePath = string.IsNullOrEmpty(folder)
                        ? item.Name
                        : $"{folder}/{item.Name}";

                    yield return new CloudImageEntry
                    {
                        Id = item.Id,
                        Name = item.Name,
                        RelativePath = relativePath,
                        SizeBytes = item.Size,
                        LastModified = item.LastModified,
                        AccountId = AccountId
                    };
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Requests a server-side thumbnail at the requested size to avoid
        /// downloading full-resolution originals. Falls back to the direct
        /// download URL if thumbnail generation fails.
        ///
        /// Applies exponential back-off on 429 / 503 responses.
        /// </remarks>
        public async Task<Stream> GetStreamAsync(
            CloudImageEntry entry,
            int maxDimensionPixels,
            CancellationToken ct = default)
        {
            // Proactively refresh the token once before starting — guards against
            // tokens that silently expired during a long index scan.
            await _auth.EnsureAuthenticatedAsync(ct).ConfigureAwait(false);

            var token = _auth.AccessToken
                ?? throw new InvalidOperationException("Not authenticated.");

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var url = await _graph
                        .GetDownloadUrlAsync(token, entry.Id, maxDimensionPixels, ct)
                        .ConfigureAwait(false);

                    // null URL means the token was rejected (401) or the item is
                    // unreachable after retries inside GetDownloadUrlAsync. Treat it
                    // as a transient failure: refresh the token and back off.
                    if (url is null)
                    {
                        if (attempt >= MaxRetries)
                            throw new InvalidOperationException(
                                $"No download URL for item {entry.Id} after {MaxRetries + 1} attempts.");

                        await Task.Delay(s_retryDelays[attempt], ct).ConfigureAwait(false);
                        await _auth.EnsureAuthenticatedAsync(ct).ConfigureAwait(false);
                        token = _auth.AccessToken ?? token;
                        continue;
                    }

                    return await _graph.OpenStreamAsync(url, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                    when (IsTransient(ex) && attempt < MaxRetries)
                {
                    await Task.Delay(s_retryDelays[attempt], ct).ConfigureAwait(false);

                    // Silently refresh the token before retrying — a 401 on the
                    // download URL usually means the pre-auth token expired.
                    await _auth.EnsureAuthenticatedAsync(ct).ConfigureAwait(false);
                    token = _auth.AccessToken ?? token;
                }
            }

            throw new InvalidOperationException(
                $"Failed to download {entry.Name} after {MaxRetries} retries.");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static bool IsTransient(HttpRequestException ex)
        {
            // HttpRequestException wraps the status code in .NET 5+.
            return ex.StatusCode is
                System.Net.HttpStatusCode.TooManyRequests or   // 429
                System.Net.HttpStatusCode.ServiceUnavailable or // 503
                System.Net.HttpStatusCode.GatewayTimeout or    // 504
                System.Net.HttpStatusCode.Unauthorized;         // 401 — token expired
        }
    }
}