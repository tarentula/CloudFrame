using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CloudFrame.Core.Cloud
{
    /// <summary>
    /// Represents a single image file entry retrieved from a cloud provider.
    /// </summary>
    public sealed class CloudImageEntry
    {
        /// <summary>Unique identifier within the provider (e.g. OneDrive item ID).</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>File name including extension, e.g. "IMG_1234.jpg".</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Full path relative to the provider root, using forward slashes.
        /// Example: "Holidays/2023/Summer/IMG_1234.jpg"
        /// </summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>File size in bytes. Used for cache decisions.</summary>
        public long SizeBytes { get; init; }

        /// <summary>Last modified timestamp from the cloud provider.</summary>
        public System.DateTimeOffset LastModified { get; init; }

        /// <summary>Which account/provider this entry belongs to.</summary>
        public string AccountId { get; init; } = string.Empty;
    }

    /// <summary>
    /// Contract that every cloud storage provider must implement.
    /// Adding a new provider (Google Photos, Dropbox, etc.) means implementing
    /// this interface — nothing else in CloudFrame needs to change.
    /// </summary>
    public interface ICloudProvider
    {
        /// <summary>Stable identifier matching <see cref="Config.AccountConfig.AccountId"/>.</summary>
        string AccountId { get; }

        /// <summary>Current access token, or null if not authenticated.</summary>
        string? AccessToken { get; }

        /// <summary>Human-readable name shown in the UI, e.g. "OneDrive – work".</summary>
        string DisplayName { get; }

        /// <summary>
        /// Returns all image files found under the configured root folders,
        /// recursively. The caller is responsible for applying filters.
        /// Implementations should yield results incrementally so the caller
        /// can start displaying images before the full index is built.
        /// </summary>
        IAsyncEnumerable<CloudImageEntry> ListFilesAsync(
            IReadOnlyList<string> rootFolderPaths,
            Action<int, int>? onProgress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Opens a readable stream for the given image entry, downloading
        /// it at the specified maximum dimension (longest edge in pixels).
        /// Providers should request a server-side thumbnail when available
        /// to avoid downloading multi-megapixel originals.
        /// </summary>
        Task<Stream> GetStreamAsync(
            CloudImageEntry entry,
            int maxDimensionPixels,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Called once on startup and periodically in the background.
        /// Implementations should silently refresh their auth token if needed.
        /// Returns false if the user needs to re-authenticate interactively.
        /// </summary>
        Task<bool> EnsureAuthenticatedAsync(CancellationToken cancellationToken = default);
    }
}