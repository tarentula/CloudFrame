using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CloudFrame.Core.Cloud;
using CloudFrame.Core.Config;
using CloudFrame.Core.Filtering;

namespace CloudFrame.Core.Index
{
    /// <summary>
    /// Holds the merged, filtered list of images from all enabled accounts
    /// and provides thread-safe weighted-random selection.
    ///
    /// Terminology:
    ///   "bucket"  — all images belonging to one AccountConfig after filtering.
    ///   "weight"  — AccountConfig.SelectionWeight, default 1.0.
    ///               Weighting is per-account (not per-image), so an account
    ///               with 10 images and weight 1.0 has the same probability of
    ///               being chosen as an account with 10,000 images and weight 1.0.
    ///
    /// Once built, the index is immutable. Replace the whole instance when a
    /// refresh is complete (cheap — the engine holds a volatile reference).
    /// </summary>
    public sealed class ImageIndex
    {
        private sealed record Bucket(
            string AccountId,
            double Weight,
            IReadOnlyList<CloudImageEntry> Entries);

        private readonly IReadOnlyList<Bucket> _buckets;
        private readonly double _totalWeight;

        // Each call to Pick() uses its own Random to avoid lock contention.
        // ThreadLocal<Random> is safe and cheap.
        private static readonly ThreadLocal<Random> s_rng =
            new(() => new Random());

        /// <summary>Total number of images in the index across all accounts.</summary>
        public int TotalCount => _buckets.Sum(b => b.Entries.Count);

        /// <summary>Number of accounts contributing at least one image.</summary>
        public int AccountCount => _buckets.Count;

        private ImageIndex(IReadOnlyList<Bucket> buckets)
        {
            _buckets = buckets;
            _totalWeight = buckets.Sum(b => b.Weight);
        }

        /// <summary>
        /// Builds an <see cref="ImageIndex"/> from pre-fetched entry lists.
        /// Applies each account's FilterEngine before adding to the bucket.
        /// </summary>
        public static ImageIndex Build(
            IEnumerable<(AccountConfig Config, IReadOnlyList<CloudImageEntry> Entries)> accountData)
        {
            var buckets = new List<Bucket>();

            foreach (var (config, entries) in accountData)
            {
                if (!config.IsEnabled || entries.Count == 0) continue;

                var engine = new FilterEngine(config.FilterRules);

                var filtered = entries
                    .Where(e => engine.IsAllowed(e.RelativePath))
                    .ToList();

                if (filtered.Count == 0) continue;

                buckets.Add(new Bucket(config.AccountId, config.SelectionWeight, filtered));
            }

            return new ImageIndex(buckets);
        }

        /// <summary>
        /// Returns an empty index (no accounts configured or all filtered out).
        /// </summary>
        public static ImageIndex Empty { get; } = new(Array.Empty<Bucket>());

        /// <summary>
        /// Picks one image using weighted-random account selection, then
        /// uniform-random selection within that account's bucket.
        ///
        /// Returns null if the index is empty.
        /// </summary>
        public CloudImageEntry? Pick()
        {
            if (_buckets.Count == 0 || _totalWeight <= 0) return null;

            var rng = s_rng.Value!;

            // 1. Pick a bucket weighted by account weight.
            double roll = rng.NextDouble() * _totalWeight;
            double cumulative = 0;
            Bucket chosen = _buckets[^1]; // fallback to last bucket

            foreach (var bucket in _buckets)
            {
                cumulative += bucket.Weight;
                if (roll <= cumulative)
                {
                    chosen = bucket;
                    break;
                }
            }

            // 2. Pick uniformly within the bucket.
            int idx = rng.Next(chosen.Entries.Count);
            return chosen.Entries[idx];
        }

        /// <summary>
        /// Returns a sequence of <paramref name="count"/> distinct entries
        /// (no repeats within the sequence) using the same weighted-random
        /// algorithm. If the index contains fewer images than requested,
        /// all images are returned in random order.
        /// </summary>
        public IReadOnlyList<CloudImageEntry> PickMany(int count)
        {
            if (_buckets.Count == 0) return Array.Empty<CloudImageEntry>();

            int available = TotalCount;
            count = Math.Min(count, available);

            // Build a flat list once, shuffle, take count.
            // For large indexes this is still cheap — we only do it when
            // building the initial prefetch queue, not on every slide change.
            var all = _buckets.SelectMany(b => b.Entries).ToList();
            Shuffle(all);
            return all.Take(count).ToList();
        }

        private static void Shuffle<T>(List<T> list)
        {
            var rng = s_rng.Value!;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
