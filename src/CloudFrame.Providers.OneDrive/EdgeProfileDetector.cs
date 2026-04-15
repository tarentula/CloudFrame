using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CloudFrame.Providers.OneDrive
{
    /// <summary>
    /// Discovers installed Microsoft Edge profiles from the user's local
    /// app data directory.
    ///
    /// Edge stores profiles as sub-folders under:
    ///   %LOCALAPPDATA%\Microsoft\Edge\User Data\
    ///
    /// Each profile folder contains a "Preferences" JSON file with a
    /// human-readable name under account.name or profile.name.
    /// The special folder "Default" is always the first profile.
    /// </summary>
    public sealed class EdgeProfile
    {
        /// <summary>
        /// The folder name used by Edge, e.g. "Default", "Profile 1".
        /// Pass this as --profile-directory="{FolderName}" to Edge.
        /// </summary>
        public string FolderName { get; init; } = string.Empty;

        /// <summary>Human-readable name shown in the UI, e.g. "Personal".</summary>
        public string DisplayName { get; init; } = string.Empty;

        public override string ToString() => DisplayName;
    }

    public static class EdgeProfileDetector
    {
        private static readonly string s_edgeUserDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data");

        /// <summary>
        /// Returns all Edge profiles found on this machine, ordered with
        /// "Default" first. Returns an empty list if Edge is not installed.
        /// </summary>
        public static IReadOnlyList<EdgeProfile> GetProfiles()
        {
            var results = new List<EdgeProfile>();

            if (!Directory.Exists(s_edgeUserDataPath))
                return results;

            // Process "Default" first, then "Profile N" folders.
            var folders = new List<string>();
            string defaultPath = Path.Combine(s_edgeUserDataPath, "Default");
            if (Directory.Exists(defaultPath))
                folders.Add("Default");

            foreach (var dir in Directory.GetDirectories(s_edgeUserDataPath))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                    folders.Add(name);
            }

            foreach (var folderName in folders)
            {
                string prefsPath = Path.Combine(
                    s_edgeUserDataPath, folderName, "Preferences");

                string displayName = ReadDisplayName(prefsPath, folderName);
                results.Add(new EdgeProfile
                {
                    FolderName = folderName,
                    DisplayName = displayName
                });
            }

            return results;
        }

        /// <summary>
        /// Returns the full path to the msedge.exe executable, or null if
        /// Edge is not installed.
        /// </summary>
        public static string? GetEdgeExecutablePath()
        {
            // Edge (stable) typical install locations.
            var candidates = new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft", "Edge", "Application", "msedge.exe"),
            };

            foreach (var path in candidates)
                if (File.Exists(path))
                    return path;

            return null;
        }

        // ── Private ────────────────────────────────────────────────────────────

        private static string ReadDisplayName(string prefsPath, string folderName)
        {
            if (!File.Exists(prefsPath))
                return folderName;

            try
            {
                using var stream = File.OpenRead(prefsPath);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                // Try account.name first (signed-in Microsoft account name).
                if (root.TryGetProperty("account_info", out var accounts) &&
                    accounts.ValueKind == JsonValueKind.Array &&
                    accounts.GetArrayLength() > 0)
                {
                    var first = accounts[0];
                    if (first.TryGetProperty("full_name", out var fullName) &&
                        fullName.GetString() is { Length: > 0 } name)
                        return $"{name} ({folderName})";

                    if (first.TryGetProperty("email", out var email) &&
                        email.GetString() is { Length: > 0 } mail)
                        return $"{mail} ({folderName})";
                }

                // Fall back to profile.name.
                if (root.TryGetProperty("profile", out var profile) &&
                    profile.TryGetProperty("name", out var profileName) &&
                    profileName.GetString() is { Length: > 0 } pName)
                    return $"{pName} ({folderName})";
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Preferences file locked or malformed — fall through.
            }

            return folderName;
        }
    }
}