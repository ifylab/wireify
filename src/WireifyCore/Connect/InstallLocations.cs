// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Detects a second Wireify install. Two roots can hold one: the manual folder
    /// (%APPDATA%\Grasshopper\Libraries\Wireify) and the Package Manager tree
    /// (%APPDATA%\McNeel\Rhinoceros\packages\&lt;version&gt;\wireify). Rhino loads whichever .rhp
    /// registers first and fails the other with "ID already in use" — and the losing copy silently
    /// never updates. Pure core (<see cref="Warnings"/>) plus a thin filesystem probe, so the
    /// logic is unit-tested without touching the machine.
    /// </summary>
    public static class InstallLocations
    {
        public static IReadOnlyList<string> Warnings(string? runningAssemblyPath, IEnumerable<string> installRoots)
        {
            var roots = installRoots.Where(r => !string.IsNullOrEmpty(r)).ToList();
            if (roots.Count < 2) return Array.Empty<string>(); // one install is the healthy state

            var running = Normalize(runningAssemblyPath);
            return roots
                .Where(root => running is null
                    || !running.StartsWith(Normalize(root)!, StringComparison.OrdinalIgnoreCase))
                .Select(root =>
                    $"second Wireify install detected at {root} — Rhino loads only one copy " +
                    "('ID already in use' at startup) and the other never updates. " +
                    "Remove the one you don't want: README > Troubleshooting.")
                .ToList();
        }

        /// <summary>The known install roots that exist on this machine.</summary>
        public static IEnumerable<string> ExistingRoots()
            => ExistingRoots(Directory.Exists, Directory.EnumerateDirectories);

        public static IEnumerable<string> ExistingRoots(
            Func<string, bool> dirExists, Func<string, IEnumerable<string>> subdirs)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) yield break;

            var manual = Path.Combine(appData, "Grasshopper", "Libraries", "Wireify");
            if (dirExists(manual)) yield return manual;

            var packages = Path.Combine(appData, "McNeel", "Rhinoceros", "packages");
            if (!dirExists(packages)) yield break;

            IEnumerable<string> versionDirs = Array.Empty<string>();
            try { versionDirs = subdirs(packages).ToList(); }
            catch { /* inaccessible tree: nothing to report */ }

            foreach (var versionDir in versionDirs)
            {
                var candidate = Path.Combine(versionDir, "wireify");
                if (dirExists(candidate)) yield return candidate;
            }
        }

        static string? Normalize(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetFullPath(path!).Replace('\\', '/'); }
            catch { return path!.Replace('\\', '/'); }
        }
    }
}
