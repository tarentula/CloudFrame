using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CloudFrame.Core.Filtering
{
    /// <summary>Whether a rule includes or excludes matching images.</summary>
    public enum FilterAction { Include, Exclude }

    /// <summary>How the pattern string is interpreted.</summary>
    public enum FilterPatternType { Glob, Regex }

    /// <summary>
    /// A single named filter rule. Rules are evaluated top-to-bottom within
    /// an account; the first match wins.
    /// </summary>
    public sealed class FilterRule
    {
        /// <summary>Display name shown in SettingsForm, e.g. "Skip RAW files".</summary>
        public string Name { get; init; } = string.Empty;

        public FilterAction Action { get; init; } = FilterAction.Exclude;

        public FilterPatternType PatternType { get; init; } = FilterPatternType.Glob;

        /// <summary>
        /// The pattern itself.
        /// Glob examples : "*.arw"  |  "*/private/*"  |  "*thumbnail*"
        /// Regex examples: @"(?i)\.arw$"  |  @"/private/"
        /// Patterns are always matched against the image's RelativePath
        /// (forward-slash separated, no leading slash).
        /// </summary>
        public string Pattern { get; init; } = string.Empty;

        /// <summary>Disabled rules are kept in config but never evaluated.</summary>
        public bool IsEnabled { get; init; } = true;
    }

    /// <summary>
    /// Evaluates an ordered list of <see cref="FilterRule"/> objects against
    /// image paths. Thread-safe after construction.
    /// </summary>
    public sealed class FilterEngine
    {
        private readonly IReadOnlyList<FilterRule> _rules;

        // Pre-compiled regex objects, indexed parallel to _rules.
        // Null entries correspond to glob rules (converted lazily on first use).
        private readonly Regex?[] _compiled;

        public FilterEngine(IReadOnlyList<FilterRule> rules)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            _compiled = new Regex?[rules.Count];

            // Pre-compile all enabled regex rules at construction time so the
            // hot path (IsAllowed) pays no compilation cost.
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (!rule.IsEnabled) continue;

                if (rule.PatternType == FilterPatternType.Regex)
                {
                    _compiled[i] = new Regex(
                        rule.Pattern,
                        RegexOptions.Compiled | RegexOptions.IgnoreCase,
                        matchTimeout: TimeSpan.FromMilliseconds(100));
                }
                // Glob rules are converted to regex on first use (see GlobToRegex).
            }
        }

        /// <summary>
        /// Returns true if the image at <paramref name="relativePath"/> should
        /// be included in the slideshow given the current rule set.
        ///
        /// Algorithm:
        ///   1. Walk rules top to bottom.
        ///   2. First match wins: if the rule is Include → allow; Exclude → deny.
        ///      Each rule is tested against BOTH the full relative path AND the
        ///      filename alone, so a glob like *privat* correctly excludes files
        ///      named something_private.jpeg even when they are in a subfolder.
        ///   3. If no rule matches:
        ///        - If there are ANY Include rules → deny (whitelist mode).
        ///        - If there are only Exclude rules → allow (blacklist mode).
        /// </summary>
        public bool IsAllowed(string relativePath)
        {
            bool hasIncludeRule = false;

            for (int i = 0; i < _rules.Count; i++)
            {
                var rule = _rules[i];
                if (!rule.IsEnabled) continue;
                if (rule.Action == FilterAction.Include) hasIncludeRule = true;

                // Match against full path first; then each individual path segment
                // (directory components + filename) so that a single-star glob like
                // *hide* correctly matches a folder named "Amandas bilder (hide)"
                // even though the full path contains slashes.
                if (Matches(i, relativePath))
                    return rule.Action == FilterAction.Include;

                var segments = relativePath.Split('/');
                foreach (var segment in segments)
                {
                    if (segment.Length > 0 && Matches(i, segment))
                        return rule.Action == FilterAction.Include;
                }
            }

            // No rule matched.
            return !hasIncludeRule;
        }

        private bool Matches(int index, string path)
        {
            var rule = _rules[index];

            if (rule.PatternType == FilterPatternType.Regex)
            {
                return _compiled[index]!.IsMatch(path);
            }

            // Glob: convert and cache the regex on first use.
            if (_compiled[index] is null)
            {
                _compiled[index] = new Regex(
                    GlobToRegex(rule.Pattern),
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    matchTimeout: TimeSpan.FromMilliseconds(100));
            }

            return _compiled[index]!.IsMatch(path);
        }

        /// <summary>
        /// Converts a simple glob pattern to an equivalent regex.
        /// Supported wildcards:
        ///   *  → matches any sequence of characters except '/'
        ///   ** → matches any sequence of characters including '/'
        ///   ?  → matches exactly one character
        /// </summary>
        private static string GlobToRegex(string glob)
        {
            var sb = new System.Text.StringBuilder("^");

            int i = 0;
            while (i < glob.Length)
            {
                char c = glob[i];

                if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");   // ** matches everything including /
                    i += 2;
                    // Skip optional trailing slash after **
                    if (i < glob.Length && glob[i] == '/') i++;
                }
                else if (c == '*')
                {
                    sb.Append("[^/]*");  // * does not cross directory boundaries
                    i++;
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                    i++;
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                }
            }

            sb.Append('$');
            return sb.ToString();
        }
    }
}
