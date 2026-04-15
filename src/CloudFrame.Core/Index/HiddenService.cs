using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudFrame.Core.Index
{
    /// <summary>
    /// Persists a set of cloud item IDs that the user has chosen to hide from
    /// the slideshow. Stored in <c>hidden.json</c> next to the index cache.
    /// The file is a simple JSON array of strings and is easy to copy between
    /// machines.
    /// </summary>
    public sealed class HiddenService
    {
        private static readonly JsonSerializerOptions s_opts = new()
        {
            WriteIndented = true
        };

        private readonly string _path;
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _loaded;

        public HiddenService(string? basePath = null)
        {
            string dir = basePath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudFrame");
            _path = Path.Combine(dir, "hidden.json");
        }

        /// <summary>Returns true if the given item ID is in the hidden list.</summary>
        public bool IsHidden(string itemId) => _hidden.Contains(itemId);

        /// <summary>Number of hidden items.</summary>
        public int Count => _hidden.Count;

        /// <summary>
        /// Loads the hidden list from disk. Safe to call multiple times —
        /// subsequent calls are no-ops.
        /// </summary>
        public async Task LoadAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_loaded) return;
                _loaded = true;

                if (!File.Exists(_path)) return;

                await using var stream = File.OpenRead(_path);
                var ids = await JsonSerializer
                    .DeserializeAsync<List<string>>(stream, s_opts, ct)
                    .ConfigureAwait(false);

                if (ids is not null)
                    foreach (var id in ids)
                        _hidden.Add(id);

                Trace.TraceInformation(
                    "[Hidden] Loaded {0} hidden item(s) from '{1}'.", _hidden.Count, _path);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "[Hidden] Failed to load '{0}': {1}: {2}", _path, ex.GetType().Name, ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Adds an item ID to the hidden set and persists to disk immediately.
        /// </summary>
        public async Task HideAsync(string itemId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _loaded = true; // treat as loaded even if LoadAsync was skipped
                if (!_hidden.Add(itemId)) return; // already hidden

                await SaveLockedAsync(ct).ConfigureAwait(false);

                Trace.TraceInformation(
                    "[Hidden] Item '{0}' hidden — {1} total.", itemId, _hidden.Count);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task SaveLockedAsync(CancellationToken ct)
        {
            var ids = new List<string>(_hidden);
            var json = JsonSerializer.Serialize(ids, s_opts);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
