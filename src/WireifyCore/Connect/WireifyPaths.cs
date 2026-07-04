// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Resolves the <c>~/.ify/wireify</c> layout: the per-.gh home dir (keyed by a stable hash of
    /// the file's absolute path, so a definition always maps to the same home), the shared defaults
    /// file, and the logs dir. The root is injectable so tests point it at a temp directory; we
    /// never write into the user's own project folders.
    /// </summary>
    public sealed class WireifyPaths
    {
        public string Root { get; }

        /// <summary>Claude Code's user config (<c>~/.claude.json</c>) — where the per-home trust
        /// pre-seed lands. Injectable so tests never touch the real one.</summary>
        public string ClaudeJson { get; }

        public WireifyPaths(string? root = null, string? claudeJson = null)
        {
            Root = root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ify", "wireify");
            ClaudeJson = claudeJson ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        }

        public string ProjectsDir => Path.Combine(Root, "projects");
        public string SharedDefaults => Path.Combine(Root, "defaults.md");
        public string LogsDir => Path.Combine(Root, "logs");

        public string HomeFor(string ghFilePath) => Path.Combine(ProjectsDir, HomeId(ghFilePath));

        /// <summary>A stable, readable id for a definition: <c>&lt;stem&gt;-&lt;8 hex of sha256(abs path)&gt;</c>.</summary>
        public static string HomeId(string ghFilePath)
        {
            if (string.IsNullOrWhiteSpace(ghFilePath)) return "untitled";

            string abs;
            try { abs = Path.GetFullPath(ghFilePath); } catch { abs = ghFilePath; }
            var key = abs.Replace('\\', '/').ToLowerInvariant();

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var hex = new StringBuilder(8);
            for (var i = 0; i < 4; i++) hex.Append(hash[i].ToString("x2"));

            var stem = Sanitize(Path.GetFileNameWithoutExtension(abs));
            if (string.IsNullOrEmpty(stem)) stem = "gh";
            return $"{stem}-{hex}";
        }

        static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            return sb.ToString().Trim('-');
        }
    }
}
