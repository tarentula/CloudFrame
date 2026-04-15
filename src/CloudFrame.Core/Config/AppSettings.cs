using System;
using System.Collections.Generic;
using CloudFrame.Core.Filtering;

namespace CloudFrame.Core.Config
{
    /// <summary>
    /// Configuration for one cloud account (e.g. a personal OneDrive).
    /// Multiple accounts of different provider types can coexist.
    /// </summary>
    public sealed class AccountConfig
    {
        /// <summary>
        /// Stable GUID assigned when the account is first added.
        /// Must match the AccountId reported by the corresponding ICloudProvider.
        /// </summary>
        public string AccountId { get; init; } = Guid.NewGuid().ToString();

        /// <summary>User-chosen label shown in the UI, e.g. "Personal OneDrive".</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Provider type discriminator used to instantiate the correct
        /// ICloudProvider implementation at runtime.
        /// Known values: "OneDrive" | "GooglePhotos" | "Dropbox"
        /// </summary>
        public string ProviderType { get; init; } = "OneDrive";

        /// <summary>Whether this account participates in the slideshow.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Root folders within this account to scan, using forward slashes.
        /// Example: [ "Pictures", "Phone Camera/2023" ]
        /// An empty list means the provider's entire root is scanned.
        /// </summary>
        public List<string> RootFolders { get; init; } = new();

        /// <summary>
        /// Ordered filter rules applied to this account after listing.
        /// Evaluated top-to-bottom; first match wins.
        /// See <see cref="FilterEngine"/> for full evaluation semantics.
        /// </summary>
        public List<FilterRule> FilterRules { get; init; } = new();

        /// <summary>
        /// Weight used when randomly selecting the next image across accounts.
        /// Default 1.0 means accounts are weighted equally (per-account, not
        /// per-image). Increase to favour images from this account.
        /// </summary>
        public double SelectionWeight { get; set; } = 1.0;

        /// <summary>
        /// Provider-specific key/value bag for things like tenant IDs,
        /// OAuth client IDs, or other settings that don't belong in the
        /// generic model. Never store secrets here — use the OS credential store.
        /// </summary>
        public Dictionary<string, string> ProviderSettings { get; init; } = new();
    }

    /// <summary>
    /// Root application settings. Serialized as JSON to:
    ///   %LOCALAPPDATA%\CloudFrame\settings.json
    /// Loaded synchronously at startup before anything else runs.
    /// </summary>
    public sealed class AppSettings
    {
        // ── Slideshow behaviour ───────────────────────────────────────────────

        /// <summary>How long each image is displayed, in seconds.</summary>
        public int SlideDurationSeconds { get; set; } = 30;

        /// <summary>
        /// Number of images to pre-download and decode in the background
        /// before they are needed. Higher = smoother at the cost of memory.
        /// Recommended range: 2–10.
        /// </summary>
        public int PrefetchCount { get; set; } = 3;

        /// <summary>Transition style between slides.</summary>
        public TransitionStyle Transition { get; set; } = TransitionStyle.CrossFade;

        /// <summary>Duration of the transition animation in milliseconds.</summary>
        public int TransitionDurationMs { get; set; } = 800;

        // ── Cache settings ────────────────────────────────────────────────────

        /// <summary>
        /// Maximum size of the on-disk image cache in megabytes.
        /// LRU eviction kicks in when this limit is approached.
        /// </summary>
        public int DiskCacheLimitMb { get; set; } = 200;

        /// <summary>
        /// Images are downscaled to fit within this dimension (longest edge)
        /// before caching. Set to your screen's longest edge.
        /// 0 = use primary screen resolution automatically.
        /// </summary>
        public int CacheMaxDimensionPixels { get; set; } = 0;

        // ── Index refresh ─────────────────────────────────────────────────────

        /// <summary>
        /// How often to refresh the file index from cloud providers, in minutes.
        /// 0 = only refresh on startup.
        /// </summary>
        public int IndexRefreshIntervalMinutes { get; set; } = 60;

        // ── Accounts ──────────────────────────────────────────────────────────

        /// <summary>All configured cloud accounts.</summary>
        public List<AccountConfig> Accounts { get; init; } = new();

        // ── Startup ───────────────────────────────────────────────────────────

        /// <summary>Launch CloudFrame when Windows starts.</summary>
        public bool RunOnStartup { get; set; } = true;

        /// <summary>
        /// Path to the cached image index on disk.
        /// Defaults to %LOCALAPPDATA%\CloudFrame\index.json if empty.
        /// </summary>
        public string IndexCachePath { get; set; } = string.Empty;

        /// <summary>
        /// Path to the on-disk image cache folder.
        /// Defaults to %LOCALAPPDATA%\CloudFrame\ImageCache if empty.
        /// </summary>
        public string DiskCachePath { get; set; } = string.Empty;
    }

    public enum TransitionStyle
    {
        None,
        CrossFade,
        SlideLeft,
        SlideUp
    }
}
