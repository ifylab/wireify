// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Renders the packaged <c>home-template/</c> into a per-.gh-file home directory and seeds
    /// the shared defaults file. Idempotent across re-Connects: files that accumulate user
    /// state (lessons, edits) are written only when missing; versioned assets (the skills brain,
    /// the read-only-tool allowlist) are refreshed every Connect. Managed marker blocks
    /// (CLAUDE.md, the MEMORY.md header) are refreshed in place so guidance upgrades reach
    /// existing homes without touching what the user wrote outside them.
    ///
    /// Every file write goes through one atomic choke point (temp file + replace), so a crash
    /// mid-write can never leave a truncated CLAUDE.md or a half-written wireify.json behind.
    ///
    /// The project <c>.mcp.json</c> is intentionally NOT written here — <see cref="ConfigMerger"/>
    /// owns it, because the port and secret change every session and that file demands the
    /// never-clobber merge.
    /// </summary>
    public sealed class HomeScaffolder
    {
        /// <summary>Whole-file char budget for MEMORY.md — roughly 2k tokens of always-loaded
        /// context. Measured at Connect; enforcement is prompt-side (the header's usage line and
        /// the over-budget directive in CLAUDE.md) plus the overflow archive in the ledger pass.</summary>
        public const int MemoryBudgetChars = 8000;

        const int BakKeep = 3;

        readonly string _templateRoot;

        public HomeScaffolder(string templateRoot)
        {
            if (string.IsNullOrEmpty(templateRoot)) throw new ArgumentException("templateRoot required", nameof(templateRoot));
            _templateRoot = templateRoot;
        }

        public sealed record Substitutions(int Port, string Secret, string GhFile);

        /// <summary>What the connect flow may want to surface: the ledger maintenance outcome
        /// (null when there was nothing to say — the common case) and the per-Connect memory
        /// glance (null when the ledger is not tool-shaped — the note covers that state).</summary>
        public sealed record ScaffoldResult(string? MemoryNote, bool MemoryNoteOk = true, string? MemoryStatus = null);

        /// <summary>Render or refresh the per-.gh home at <paramref name="homeDir"/>.
        /// <paramref name="sharedSkillsDir"/> is the user-owned cross-definition skill tier
        /// (<c>~/.ify/wireify/skills</c>) — merged into the home before the bundled tree so
        /// Wireify's own skills win name collisions.</summary>
        public ScaffoldResult ScaffoldHome(string homeDir, Substitutions subs, string? sharedSkillsDir = null)
        {
            if (string.IsNullOrEmpty(homeDir)) throw new ArgumentException("homeDir required", nameof(homeDir));
            if (subs is null) throw new ArgumentNullException(nameof(subs));
            if (!Directory.Exists(_templateRoot))
                throw new DirectoryNotFoundException($"home-template not found at '{_templateRoot}'");

            Directory.CreateDirectory(homeDir);

            var memoryPath = Path.Combine(homeDir, "MEMORY.md");

            // This definition's accumulated lessons; NEVER clobber. Fresh homes get the seed with
            // provisional header values — the managed refresh below recomputes them in this same call.
            RenderTemplate("memory-seed.md", memoryPath, subs, overwrite: false, ProvisionalMemoryTokens());

            // Deterministic ledger maintenance: dedup + overflow-archive, snapshot-first,
            // fail-open on anything that is not tool-shaped. Runs BEFORE the header refresh so
            // the usage line below reflects the maintained file.
            var memoryNote = MaintainMemoryLedger(memoryPath, subs.GhFile);

            var memoryText = File.Exists(memoryPath) ? File.ReadAllText(memoryPath) : "";
            var extras = MemoryTokens(memoryText.Length);

            // Managed block between wireify:begin/end markers is refreshed every Connect (so
            // guidance fixes reach existing homes); anything the user wrote outside it is preserved.
            // CLAUDE.md legacy files (no markers) get the block appended below their content;
            // MEMORY.md legacy files get it PREPENDED so the lesson text keeps reading top-down
            // under the header. Either way every user byte survives.
            RenderManagedBlock("CLAUDE.md.tmpl", Path.Combine(homeDir, "CLAUDE.md"), subs, legacyPrepend: false, extras);
            RenderManagedBlock("memory-seed.md", memoryPath, subs, legacyPrepend: true, extras);

            // Static read-only-tool allowlist; safe to refresh.
            RenderTemplate("settings.json.tmpl", Path.Combine(homeDir, ".claude", "settings.json"), subs, overwrite: true);
            // The home's standing spawn options (claude --model/--effort), in a file only Wireify
            // owns (NOT .claude/settings.local.json — Claude Code creates that itself for permission
            // decisions, which silently defeated a write-if-missing seed there). Per-KEY merge:
            // missing keys are added every Connect (so new options reach existing homes), existing
            // values are never changed; set a value to "default" to use your own Claude setting.
            EnsureWireifyJson(Path.Combine(homeDir, "wireify.json"), subs);

            // The user-owned shared skill tier first, then the versioned GH brain over it — the
            // bundled tree wins name collisions, so Wireify's own skills stay canonical. User-added
            // skills in the home are preserved either way (we overwrite our files, we don't wipe
            // the directory).
            if (!string.IsNullOrEmpty(sharedSkillsDir))
                CopyTree(sharedSkillsDir!, Path.Combine(homeDir, ".claude", "skills"));
            CopyTree(Path.Combine(_templateRoot, "skills"), Path.Combine(homeDir, ".claude", "skills"));

            return new ScaffoldResult(memoryNote, MemoryStatus: MemoryStatusLine(memoryText));
        }

        /// <summary>The Connect memory glance — lesson count, newest lesson date, and the same
        /// usage numbers the ledger header carries — so the compounding memory is visible where
        /// the user already looks, every Connect. Null when the ledger is not tool-shaped: the
        /// maintenance note reports that state, and one memory line per Connect is the budget.</summary>
        static string? MemoryStatusLine(string memoryText)
        {
            var stats = MemoryLedger.Stats(memoryText);
            if (stats is null) return null;
            if (stats.Entries == 0) return "memory: no lessons yet";
            return string.Format(CultureInfo.InvariantCulture,
                "memory: {0:N0} lesson{1}{2}, {3:N0}/{4:N0} chars",
                stats.Entries,
                stats.Entries == 1 ? "" : "s",
                stats.NewestDate is null ? "" : " (last " + stats.NewestDate + ")",
                memoryText.Length,
                MemoryBudgetChars);
        }

        /// <summary>Seed the shared defaults file only if it does not already exist (it is user-edited).</summary>
        public void SeedSharedDefaults(string defaultsPath)
        {
            if (string.IsNullOrEmpty(defaultsPath)) throw new ArgumentException("defaultsPath required", nameof(defaultsPath));
            if (File.Exists(defaultsPath)) return;
            var src = Path.Combine(_templateRoot, "defaults-seed.md");
            if (!File.Exists(src)) throw new FileNotFoundException("defaults-seed.md missing from home-template", src);
            WriteAllText(defaultsPath, File.ReadAllText(src));
        }

        /// <summary>Section-level append-if-missing merge for the existing defaults.md: seed
        /// sections (H2 <c>## </c> headings) absent from the user's file are appended at the end,
        /// in seed order — the wireify.json per-key merge lifted to Markdown granularity, so a new
        /// seed section reaches existing installs. User content is never modified, reordered, or
        /// deleted; a file with no H2 structure (rewritten freeform) is left entirely alone — the
        /// same fail-open posture as <see cref="EnsureWireifyJson"/> on unparseable JSON.</summary>
        public void MergeSharedDefaults(string defaultsPath)
        {
            if (string.IsNullOrEmpty(defaultsPath)) throw new ArgumentException("defaultsPath required", nameof(defaultsPath));
            if (!File.Exists(defaultsPath)) return; // seeding owns the fresh-file case
            var src = Path.Combine(_templateRoot, "defaults-seed.md");
            if (!File.Exists(src)) throw new FileNotFoundException("defaults-seed.md missing from home-template", src);

            var user = File.ReadAllText(defaultsPath);
            var userHeadings = new HashSet<string>(
                SplitSections(user).Select(s => s.Heading), StringComparer.Ordinal);
            if (userHeadings.Count == 0) return; // freeform rewrite — theirs, untouched

            var missing = SplitSections(File.ReadAllText(src))
                .Where(s => !userHeadings.Contains(s.Heading))
                .ToList();
            if (missing.Count == 0) return;

            SnapshotBak(defaultsPath, user);
            var sb = new StringBuilder(user.TrimEnd());
            foreach (var section in missing)
            {
                sb.Append("\n\n");
                sb.Append(section.Text.TrimEnd());
            }
            sb.Append('\n');
            WriteAllText(defaultsPath, sb.ToString());
        }

        /// <summary>H2 sections of a Markdown file: heading line (trimmed) + raw text through the
        /// line before the next heading. Content before the first <c>## </c> is not a section.</summary>
        static List<(string Heading, string Text)> SplitSections(string text)
        {
            var sections = new List<(string, string)>();
            var lines = text.Split('\n');
            var start = -1;
            var heading = "";
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("## ", StringComparison.Ordinal)) continue;
                if (start >= 0)
                    sections.Add((heading, string.Join("\n", lines.Skip(start).Take(i - start))));
                start = i;
                heading = lines[i].TrimEnd('\r').TrimEnd();
            }
            if (start >= 0)
                sections.Add((heading, string.Join("\n", lines.Skip(start))));
            return sections;
        }

        // ---- memory ledger -------------------------------------------------------------------

        static Dictionary<string, string> ProvisionalMemoryTokens() => new()
        {
            ["{{MEM_USAGE}}"] = "Ledger: new.",
            ["{{MEM_BUDGET}}"] = MemoryBudgetChars.ToString("N0", CultureInfo.InvariantCulture),
            ["{{MEM_DIRECTIVE}}"] = string.Empty,
        };

        static Dictionary<string, string> MemoryTokens(int memoryChars) => new()
        {
            ["{{MEM_USAGE}}"] = string.Format(CultureInfo.InvariantCulture,
                "Ledger: {0:N0} / {1:N0} chars.", memoryChars, MemoryBudgetChars),
            ["{{MEM_BUDGET}}"] = MemoryBudgetChars.ToString("N0", CultureInfo.InvariantCulture),
            ["{{MEM_DIRECTIVE}}"] = memoryChars > MemoryBudgetChars
                ? "**Ledger over budget — before new work this session: run the `wireify-retro` " +
                  "skill to consolidate MEMORY.md (merge duplicate lessons, rewrite stale ones, " +
                  "move cross-file rules to `~/.ify/wireify/defaults.md`), then proceed.**"
                : string.Empty,
        };

        /// <summary>Run the deterministic <see cref="MemoryLedger"/> pass over the home's ledger.
        /// Mutations are snapshot-first (.bak, newest <see cref="BakKeep"/> kept) and atomic;
        /// overflow entries append to MEMORY-archive.md beside the ledger, never deleted. Returns
        /// a one-line note for the connect panel, or null when there is nothing to report.</summary>
        static string? MaintainMemoryLedger(string memoryPath, string ghFile)
        {
            if (!File.Exists(memoryPath)) return null;

            var text = File.ReadAllText(memoryPath);
            var result = MemoryLedger.Maintain(text, MemoryBudgetChars);

            if (!result.Conforming)
            {
                // Legacy free text or a foreign format: never touched (fail-open). Logged so the
                // state is visible; the wireify-retro skill is the guided way to restructure.
                return "memory ledger: unmanaged format — maintenance skipped (lessons untouched)";
            }

            if (!result.Changed) return null;

            SnapshotBak(memoryPath, text);
            if (result.ArchiveAppend is not null)
            {
                var archivePath = Path.Combine(Path.GetDirectoryName(memoryPath)!, "MEMORY-archive.md");
                var archive = File.Exists(archivePath)
                    ? File.ReadAllText(archivePath)
                    : $"# Wireify memory archive — {ghFile}\n\nOldest entries moved here when MEMORY.md " +
                      "exceeds its budget. Still yours; never imported, never deleted.\n";
                WriteAllText(archivePath, archive.TrimEnd() + "\n\n" + result.ArchiveAppend.Trim() + "\n");
            }
            WriteAllText(memoryPath, result.Text);

            var parts = new List<string>();
            if (result.DroppedDuplicates > 0) parts.Add($"{result.DroppedDuplicates} duplicate(s) merged");
            if (result.ArchivedEntries > 0) parts.Add($"{result.ArchivedEntries} older entr{(result.ArchivedEntries == 1 ? "y" : "ies")} archived");
            return "memory ledger: " + string.Join(", ", parts) + " (bak kept)";
        }

        /// <summary>Pre-mutation snapshot of a user-state file: <c>&lt;name&gt;.bak.&lt;stamp&gt;</c>
        /// beside it, newest <see cref="BakKeep"/> retained.</summary>
        static void SnapshotBak(string path, string currentText)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            WriteAllText(path + ".bak." + stamp, currentText);

            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(dir)) return;
            var baks = Directory.GetFiles(dir, Path.GetFileName(path) + ".bak.*")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(BakKeep)
                .ToArray();
            foreach (var old in baks)
            {
                try { File.Delete(old); } catch { /* retention is best-effort */ }
            }
        }

        // ---- wireify.json --------------------------------------------------------------------

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

        // ---- managed blocks ------------------------------------------------------------------

        const string BlockBegin = "<!-- wireify:begin";
        const string BlockEnd = "<!-- wireify:end -->";

        /// <summary>A file with a managed block: fresh files get the full render; existing files
        /// get ONLY the marker-delimited block replaced (user text outside it is untouched); legacy
        /// files without markers keep their entire content and gain the block — appended below for
        /// CLAUDE.md, PREPENDED above for MEMORY.md (<paramref name="legacyPrepend"/>) — never a
        /// destructive rewrite of a file the user may have edited. Legacy transforms (the only case
        /// that restructures a user file) are snapshot-first.</summary>
        void RenderManagedBlock(string templateName, string destPath, Substitutions subs, bool legacyPrepend,
            IReadOnlyDictionary<string, string>? extras = null)
        {
            var rendered = RenderText(templateName, subs, extras);
            var bi = rendered.IndexOf(BlockBegin, StringComparison.Ordinal);
            var ei = rendered.IndexOf(BlockEnd, StringComparison.Ordinal);
            if (bi < 0 || ei <= bi)
                throw new InvalidOperationException($"{templateName} is missing the wireify:begin/end markers.");
            var block = rendered.Substring(bi, ei + BlockEnd.Length - bi);

            if (!File.Exists(destPath))
            {
                WriteAllText(destPath, rendered);
                return;
            }

            var existing = File.ReadAllText(destPath);
            var xb = existing.IndexOf(BlockBegin, StringComparison.Ordinal);
            var xe = existing.IndexOf(BlockEnd, StringComparison.Ordinal);
            string next;
            var legacyTransform = false;
            if (xb >= 0 && xe > xb)
            {
                next = existing.Substring(0, xb) + block + existing.Substring(xe + BlockEnd.Length);
            }
            else
            {
                legacyTransform = true;
                next = legacyPrepend
                    ? block + "\n\n" + existing
                    : existing.TrimEnd() + "\n\n" + block + "\n";
            }

            if (string.Equals(next, existing, StringComparison.Ordinal)) return;
            // Only the legacy transform restructures user bytes; a pure block refresh rewrites
            // Wireify-owned text, and the atomic write already rules out torn states.
            if (legacyTransform) SnapshotBak(destPath, existing);
            WriteAllText(destPath, next);
        }

        // ---- rendering + writes --------------------------------------------------------------

        string RenderText(string templateName, Substitutions subs, IReadOnlyDictionary<string, string>? extras = null)
        {
            var src = Path.Combine(_templateRoot, templateName);
            if (!File.Exists(src)) throw new FileNotFoundException($"template '{templateName}' missing from home-template", src);
            var text = File.ReadAllText(src)
                .Replace("{{PORT}}", subs.Port.ToString())
                .Replace("{{SECRET}}", subs.Secret)
                .Replace("{{GH_FILE}}", subs.GhFile);
            if (extras is not null)
                foreach (var pair in extras)
                    text = text.Replace(pair.Key, pair.Value);
            return text;
        }

        void RenderTemplate(string templateName, string destPath, Substitutions subs, bool overwrite,
            IReadOnlyDictionary<string, string>? extras = null)
        {
            if (!overwrite && File.Exists(destPath)) return;
            WriteAllText(destPath, RenderText(templateName, subs, extras));
        }

        /// <summary>The single write choke point: write a temp file beside the destination, flush
        /// to disk, then atomically replace/move it over the target. Readers (and crashes) see the
        /// old complete file or the new complete file — never a truncated one. Internal so the
        /// other Connect-side writers (<see cref="HomeIdentity"/>) share it instead of forking it.</summary>
        internal static void WriteAllText(string destPath, string text)
        {
            var full = Path.GetFullPath(destPath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = full + ".tmp-" + Path.GetRandomFileName();
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(full)) File.Replace(tmp, full, destinationBackupFileName: null);
                else File.Move(tmp, full);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
            }
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
