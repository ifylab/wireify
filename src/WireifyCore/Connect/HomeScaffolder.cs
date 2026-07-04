// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Text;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Renders the packaged <c>home-template/</c> into a per-.gh-file home directory and seeds
    /// the shared defaults file. Idempotent across re-Connects: files that accumulate user
    /// state (lessons, edits) are written only when missing; versioned assets (the skills brain,
    /// the read-only-tool allowlist) are refreshed every Connect.
    ///
    /// The project <c>.mcp.json</c> is intentionally NOT written here — <see cref="ConfigMerger"/>
    /// owns it, because the port and secret change every session and that file demands the
    /// never-clobber merge.
    /// </summary>
    public sealed class HomeScaffolder
    {
        readonly string _templateRoot;

        public HomeScaffolder(string templateRoot)
        {
            if (string.IsNullOrEmpty(templateRoot)) throw new ArgumentException("templateRoot required", nameof(templateRoot));
            _templateRoot = templateRoot;
        }

        public sealed record Substitutions(int Port, string Secret, string GhFile);

        /// <summary>Render or refresh the per-.gh home at <paramref name="homeDir"/>.</summary>
        public void ScaffoldHome(string homeDir, Substitutions subs)
        {
            if (string.IsNullOrEmpty(homeDir)) throw new ArgumentException("homeDir required", nameof(homeDir));
            if (subs is null) throw new ArgumentNullException(nameof(subs));
            if (!Directory.Exists(_templateRoot))
                throw new DirectoryNotFoundException($"home-template not found at '{_templateRoot}'");

            Directory.CreateDirectory(homeDir);

            // Managed block between wireify:begin/end markers is refreshed every Connect (so
            // guidance fixes reach existing homes); anything the user wrote outside it is preserved.
            RenderManagedClaudeMd(Path.Combine(homeDir, "CLAUDE.md"), subs);
            // Static read-only-tool allowlist; safe to refresh.
            RenderTemplate("settings.json.tmpl", Path.Combine(homeDir, ".claude", "settings.json"), subs, overwrite: true);
            // The home's standing spawn options (claude --model/--effort), in a file only Wireify
            // owns (NOT .claude/settings.local.json — Claude Code creates that itself for permission
            // decisions, which silently defeated a write-if-missing seed there). Per-KEY merge:
            // missing keys are added every Connect (so new options reach existing homes), existing
            // values are never changed; set a value to "default" to use your own Claude setting.
            EnsureWireifyJson(Path.Combine(homeDir, "wireify.json"), subs);
            // This definition's accumulated lessons; NEVER clobber.
            RenderTemplate("memory-seed.md", Path.Combine(homeDir, "MEMORY.md"), subs, overwrite: false);

            // The versioned GH brain; refreshed every Connect. User-added skills are preserved
            // (we overwrite our files, we don't wipe the directory).
            CopyTree(Path.Combine(_templateRoot, "skills"), Path.Combine(homeDir, ".claude", "skills"));
        }

        /// <summary>Seed the shared defaults file only if it does not already exist (it is user-edited).</summary>
        public void SeedSharedDefaults(string defaultsPath)
        {
            if (string.IsNullOrEmpty(defaultsPath)) throw new ArgumentException("defaultsPath required", nameof(defaultsPath));
            if (File.Exists(defaultsPath)) return;
            var src = Path.Combine(_templateRoot, "defaults-seed.md");
            if (!File.Exists(src)) throw new FileNotFoundException("defaults-seed.md missing from home-template", src);
            var dir = Path.GetDirectoryName(Path.GetFullPath(defaultsPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, defaultsPath);
        }

        /// <summary>Per-key merge of wireify.json: template keys missing from the user's file are
        /// added; keys the user already has (any value, including "default") are never touched. A
        /// missing file gets the full template; an unparseable file is left alone (never destroy a
        /// user edit — the read side treats it as no-flags anyway).</summary>
        void EnsureWireifyJson(string destPath, Substitutions subs)
        {
            var rendered = RenderText("wireify.json.tmpl", subs);
            if (!File.Exists(destPath))
            {
                WriteAllText(destPath, rendered);
                return;
            }

            try
            {
                var defaults = System.Text.Json.Nodes.JsonNode.Parse(rendered)!.AsObject();
                var existing = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(destPath)) as System.Text.Json.Nodes.JsonObject;
                if (existing is null) return;

                var changed = false;
                foreach (var pair in defaults)
                {
                    if (existing.ContainsKey(pair.Key)) continue;
                    existing[pair.Key] = pair.Value?.DeepClone();
                    changed = true;
                }
                if (changed)
                    WriteAllText(destPath, existing.ToJsonString(
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
            catch { /* malformed user file — leave it; reads fall back to the user's defaults */ }
        }

        const string BlockBegin = "<!-- wireify:begin";
        const string BlockEnd = "<!-- wireify:end -->";

        /// <summary>CLAUDE.md with a managed block: fresh homes get the full render; existing homes
        /// get ONLY the marker-delimited block replaced (user text outside it is untouched); legacy
        /// files without markers keep their entire content and get the block appended — never a
        /// destructive rewrite of a file the user may have edited.</summary>
        void RenderManagedClaudeMd(string destPath, Substitutions subs)
        {
            var rendered = RenderText("CLAUDE.md.tmpl", subs);
            var bi = rendered.IndexOf(BlockBegin, StringComparison.Ordinal);
            var ei = rendered.IndexOf(BlockEnd, StringComparison.Ordinal);
            if (bi < 0 || ei <= bi)
                throw new InvalidOperationException("CLAUDE.md.tmpl is missing the wireify:begin/end markers.");
            var block = rendered.Substring(bi, ei + BlockEnd.Length - bi);

            if (!File.Exists(destPath))
            {
                WriteAllText(destPath, rendered);
                return;
            }

            var existing = File.ReadAllText(destPath);
            var xb = existing.IndexOf(BlockBegin, StringComparison.Ordinal);
            var xe = existing.IndexOf(BlockEnd, StringComparison.Ordinal);
            var next = xb >= 0 && xe > xb
                ? existing.Substring(0, xb) + block + existing.Substring(xe + BlockEnd.Length)
                : existing.TrimEnd() + "\n\n" + block + "\n";
            if (!string.Equals(next, existing, StringComparison.Ordinal))
                WriteAllText(destPath, next);
        }

        string RenderText(string templateName, Substitutions subs)
        {
            var src = Path.Combine(_templateRoot, templateName);
            if (!File.Exists(src)) throw new FileNotFoundException($"template '{templateName}' missing from home-template", src);
            return File.ReadAllText(src)
                .Replace("{{PORT}}", subs.Port.ToString())
                .Replace("{{SECRET}}", subs.Secret)
                .Replace("{{GH_FILE}}", subs.GhFile);
        }

        static void WriteAllText(string destPath, string text)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(destPath, text, new UTF8Encoding(false));
        }

        void RenderTemplate(string templateName, string destPath, Substitutions subs, bool overwrite)
        {
            if (!overwrite && File.Exists(destPath)) return;
            var src = Path.Combine(_templateRoot, templateName);
            if (!File.Exists(src)) throw new FileNotFoundException($"template '{templateName}' missing from home-template", src);

            var text = File.ReadAllText(src)
                .Replace("{{PORT}}", subs.Port.ToString())
                .Replace("{{SECRET}}", subs.Secret)
                .Replace("{{GH_FILE}}", subs.GhFile);

            var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(destPath, text, new UTF8Encoding(false));
        }

        static void CopyTree(string sourceDir, string destDir)
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            foreach (var sub in Directory.GetDirectories(sourceDir))
                CopyTree(sub, Path.Combine(destDir, Path.GetFileName(sub)));
        }
    }
}
