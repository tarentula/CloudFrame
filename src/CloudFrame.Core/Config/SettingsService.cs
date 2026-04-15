using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CloudFrame.Core.Config
{
    /// <summary>
    /// Loads and persists <see cref="AppSettings"/> as JSON.
    ///
    /// Fast-startup contract:
    ///   Call <see cref="LoadOrCreateAsync"/> once at process start (before
    ///   showing any UI). It completes in &lt;10 ms on spinning disk because
    ///   the file is tiny. The rest of the app can then read
    ///   <see cref="Current"/> from any thread without locking.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _settingsPath;

        // Cached after first load. Written only from SaveAsync (UI thread or
        // background — callers are responsible for not racing saves).
        public AppSettings Current { get; private set; } = new();

        public SettingsService(string? settingsPath = null)
        {
            _settingsPath = settingsPath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudFrame",
                    "settings.json");
        }

        /// <summary>
        /// Loads settings from disk, or creates and saves defaults if the file
        /// does not exist. Called once at startup — synchronous I/O is
        /// intentional here to keep the startup code path simple and fast.
        /// </summary>
        public AppSettings LoadOrCreate()
        {
            EnsureDirectory();

            if (!File.Exists(_settingsPath))
            {
                Current = CreateDefaults();
                SaveSync(Current);
                return Current;
            }

            try
            {
                var json = File.ReadAllText(_settingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions)
                          ?? CreateDefaults();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Corrupt or unreadable file — fall back to defaults.
                // The old file is left in place so the user can inspect it.
                Current = CreateDefaults();
            }

            return Current;
        }

        /// <summary>
        /// Persists the current settings to disk asynchronously.
        /// Safe to call from the UI thread.
        /// </summary>
        public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            EnsureDirectory();

            var json = JsonSerializer.Serialize(settings, s_jsonOptions);

            // Write to a temp file then atomically replace to avoid corruption
            // if the process is killed mid-write.
            var tmp = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _settingsPath, overwrite: true);

            Current = settings;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SaveSync(AppSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, s_jsonOptions);
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _settingsPath, overwrite: true);
        }

        private void EnsureDirectory()
        {
            var dir = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(dir);
        }

        private static AppSettings CreateDefaults() => new()
        {
            SlideDurationSeconds = 30,
            PrefetchCount = 3,
            Transition = TransitionStyle.CrossFade,
            TransitionDurationMs = 800,
            DiskCacheLimitMb = 200,
            CacheMaxDimensionPixels = 0,   // auto-detect screen resolution
            IndexRefreshIntervalMinutes = 60,
            RunOnStartup = true
        };
    }
}
