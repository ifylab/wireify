// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Reflection;

namespace WireifyCore
{
    /// <summary>
    /// The loaded build's identity, for humans and agents alike: the assembly version plus a
    /// build stamp baked into the informational version at COMPILE time (UTC) — file write times
    /// looked usable but are rewritten by Unblock-File and copies (live-observed: a 23:22 zip
    /// read as 23:35 after the unblock sweep), so only assembly metadata is authoritative.
    /// Surfaced in the panel's listening line, the MCP serverInfo, and get_runtime_info — the
    /// ten-second answer to "did the zip swap actually take?" after round 17 burned an evening
    /// on a stale install that failed silently.
    /// </summary>
    public static class WireifyBuild
    {
        const string StampMarker = "+build.";

        public static string Version { get; } = ResolveVersion();

        /// <summary>The compile-time stamp ("2026-07-10-0140", UTC) from the informational
        /// version's metadata; falls back to the DLL's write time (marked as such) on builds
        /// without the baked stamp, or "unknown" when the assembly has no file path.</summary>
        public static string Stamp { get; } = ResolveStamp();

        public static string Describe() => $"{Version} build {Stamp}";

        static string? Informational()
        {
            try
            {
                return typeof(WireifyBuild).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            }
            catch { return null; }
        }

        static string ResolveVersion()
        {
            try
            {
                var informational = Informational();
                if (!string.IsNullOrWhiteSpace(informational))
                {
                    var plus = informational!.IndexOf('+'); // metadata is the stamp, not the version
                    return plus > 0 ? informational.Substring(0, plus) : informational;
                }
                return typeof(WireifyBuild).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            }
            catch { return "unknown"; }
        }

        static string ResolveStamp()
        {
            var informational = Informational();
            var at = informational?.IndexOf(StampMarker, StringComparison.Ordinal) ?? -1;
            if (informational is not null && at >= 0)
            {
                var stamp = informational.Substring(at + StampMarker.Length).Trim();
                if (stamp.Length > 0) return stamp;
            }

            try
            {
                var location = typeof(WireifyBuild).Assembly.Location;
                if (string.IsNullOrEmpty(location) || !File.Exists(location)) return "unknown";
                return File.GetLastWriteTime(location).ToString(
                    "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " (file time)";
            }
            catch { return "unknown"; }
        }
    }
}
