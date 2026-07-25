// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Home identity: the Wireify-owned <c>&lt;home&gt;/.wireify/home.json</c> record tying a home
    /// to its <c>.gh</c> file (absolute path + content hash + last Connect), and the two Connect-time
    /// passes built on it — adoption (a renamed or moved definition reconnects to its accumulated
    /// memory instead of silently minting a fresh home) and the orphan sweep (homes whose
    /// <c>.gh</c> vanished age active -> orphaned -> <c>archive/</c>, never deleted).
    ///
    /// Matching is deliberately conservative: only orphans (recorded path gone from disk) are ever
    /// adoption candidates, so a copied file — its original still present — always scaffolds fresh,
    /// and any ambiguity refuses. Everything here stays under <c>~/.ify/wireify</c>; the
    /// <c>.gh</c> itself is only ever hashed, never written. Deterministic, no model calls,
    /// fail-open per home: one unreadable record never breaks a Connect.
    /// </summary>
    public static class HomeIdentity
    {
        /// <summary>The home.json shape. <c>GhSha256</c> is null when the .gh bytes were not
        /// readable at Connect time; <c>OrphanedAtUtc</c> is set by the sweep when the recorded
        /// path stops existing and cleared when it comes back. <c>AdoptedInto</c> names the home
        /// this one's memory was copied into — a marked home never matches adoption again and
        /// ages out through the normal sweep (never deleted).</summary>
        public sealed record HomeRecord(
            string GhPath,
            string? GhSha256,
            DateTime LastConnectUtc,
            DateTime? OrphanedAtUtc = null,
            string? AdoptedInto = null);

        /// <summary>One orphaned home the adoption pass could not resolve automatically — handed
        /// to the session via <c>.wireify/adoption-candidates.json</c> so a user saying "my
        /// lessons are missing" gets a guided, user-confirmed recovery instead of a dead end.</summary>
        public sealed record CandidateInfo(
            string HomeId,
            string GhPath,
            DateTime? LastConnectUtc,
            DateTime? OrphanedAtUtc,
            int LedgerChars);

        /// <summary>What the adoption pass decided. <c>Note</c> is the connect-log line (null =
        /// nothing worth saying); <c>Ok</c> is false only when an adoption copied incompletely;
        /// <c>Unresolved</c> carries the orphans a refusal left behind (empty otherwise).</summary>
        public sealed record AdoptionOutcome(
            string? Note,
            bool Adopted,
            bool Ok,
            IReadOnlyList<CandidateInfo> Unresolved)
        {
            public static readonly AdoptionOutcome Nothing =
                new(null, false, true, Array.Empty<CandidateInfo>());
        }

        public static TimeSpan ArchiveAfter { get; } = TimeSpan.FromDays(90);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public static string RecordPath(string homeDir) => Path.Combine(homeDir, ".wireify", "home.json");

        /// <summary>Overwrite-every-Connect: the record always reflects the file just connected
        /// (which also clears any orphan stamp — the definition is demonstrably alive).</summary>
        public static void Write(string homeDir, string ghFilePath)
        {
            var abs = SafeFullPath(ghFilePath);
            WriteRecord(homeDir, new HomeRecord(abs, TrySha256(abs), DateTime.UtcNow));
        }

        static void WriteRecord(string homeDir, HomeRecord record)
            => HomeScaffolder.WriteAllText(RecordPath(homeDir), JsonSerializer.Serialize(record, JsonOptions) + "\n");

        /// <summary>Null when the record is missing or not readable as a home record (fail-open —
        /// a home without identity is simply invisible to adoption and the sweep).</summary>
        public static HomeRecord? Read(string homeDir)
        {
            try
            {
                var path = RecordPath(homeDir);
                if (!File.Exists(path)) return null;
                var record = JsonSerializer.Deserialize<HomeRecord>(File.ReadAllText(path), JsonOptions);
                return string.IsNullOrWhiteSpace(record?.GhPath) ? null : record;
            }
            catch { return null; }
        }

        // ---- adoption --------------------------------------------------------------------------

        /// <summary>
        /// When the target home does not exist yet, look for the home this definition USED to be:
        /// first an orphan whose recorded content hash matches the current bytes exactly (moved or
        /// renamed, unedited since its last Connect), else exactly one orphan whose recorded path
        /// sat in the same directory (renamed in place after edits). A single unambiguous candidate
        /// is COPIED onto <paramref name="homeDir"/> — BEFORE ScaffoldHome, so the managed refresh
        /// lands on the adopted files. Copy, never move: the original home may be a still-open
        /// terminal's working directory (live-observed — Windows refuses to move it), and a copy
        /// also means adoption can never destroy anything. The original is stamped
        /// <c>adoptedInto</c> (never matches again) and ages out through the normal sweep.
        /// Refusals stay conservative — a copied file (original alive) and ambiguity both scaffold
        /// fresh — but now name their candidates and hand them to the session via
        /// <see cref="WriteAdoptionCandidates"/>.
        /// </summary>
        public static AdoptionOutcome TryAdopt(WireifyPaths paths, string ghFilePath, string homeDir)
        {
            if (Directory.Exists(homeDir) || string.IsNullOrWhiteSpace(ghFilePath)) return AdoptionOutcome.Nothing;

            var abs = SafeFullPath(ghFilePath);
            var orphans = ScanHomes(paths)
                .Select(h => (h.Dir, h.FromArchive, Record: Read(h.Dir)))
                .Where(h => h.Record is not null
                    && h.Record!.AdoptedInto is null // already recovered elsewhere: never re-adopt
                    && !File.Exists(h.Record!.GhPath))
                .ToList();
            if (orphans.Count == 0) return AdoptionOutcome.Nothing;

            var hash = TrySha256(abs);
            var matches = hash is null
                ? new()
                : orphans.Where(o => string.Equals(o.Record!.GhSha256, hash, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                var dir = SafeDirectoryName(abs);
                matches = orphans.Where(o => PathsEqual(SafeDirectoryName(o.Record!.GhPath), dir)).ToList();
            }

            if (matches.Count > 1)
                return new AdoptionOutcome(
                    $"memory adoption skipped: {matches.Count} orphaned homes match this file equally " +
                    $"({NameList(matches.Select(m => m.Dir))}) — scaffolding fresh; candidates recorded in the home",
                    false, true, ToCandidates(matches));
            if (matches.Count == 0)
                return new AdoptionOutcome(
                    $"no prior home matched for adoption ({orphans.Count} orphaned home(s) on record: " +
                    $"{NameList(orphans.Select(o => o.Dir))}) — scaffolding fresh",
                    false, true, ToCandidates(orphans));

            var chosen = matches[0];
            var oldId = Path.GetFileName(chosen.Dir);
            var failures = CopyTreeBestEffort(chosen.Dir, homeDir);
            // Mark + start the archive clock in one write, so the sweep has nothing left to say
            // about a home whose memory just moved on.
            try
            {
                WriteRecord(chosen.Dir, chosen.Record! with
                {
                    AdoptedInto = Path.GetFileName(homeDir),
                    OrphanedAtUtc = chosen.Record!.OrphanedAtUtc ?? DateTime.UtcNow,
                });
            }
            catch { /* worst case it matches once more and re-copies — never blocks the adoption */ }

            var note = $"adopted memory from {(chosen.FromArchive ? "archive/" : "")}{oldId} " +
                "(copied; the original stays untouched until the sweep archives it)" +
                (failures > 0 ? $" — {failures} file(s) could not be copied, check the original home" : "");
            return new AdoptionOutcome(note, true, failures == 0, Array.Empty<CandidateInfo>());
        }

        /// <summary>The refusal handoff: persist the unresolved orphans into the (by now
        /// scaffolded) home at <c>.wireify/adoption-candidates.json</c>, where the loop skill
        /// offers a user-confirmed ledger recovery when someone says "my lessons are missing".
        /// An empty list removes any stale file from an earlier refusal.</summary>
        public static void WriteAdoptionCandidates(string homeDir, IReadOnlyList<CandidateInfo> candidates)
        {
            var path = Path.Combine(homeDir, ".wireify", "adoption-candidates.json");
            if (candidates.Count == 0)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* stale hygiene only */ }
                return;
            }
            var payload = new
            {
                note = "Wireify could not automatically identify a prior home for this definition. " +
                    "If lessons are missing after a rename or move, one of these orphaned homes may be it — " +
                    "confirm with the user before merging any ledger.",
                candidates,
            };
            HomeScaffolder.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions) + "\n");
        }

        static IReadOnlyList<CandidateInfo> ToCandidates(
            IEnumerable<(string Dir, bool FromArchive, HomeRecord? Record)> homes)
            => homes.Select(h => new CandidateInfo(
                    (h.FromArchive ? "archive/" : "") + Path.GetFileName(h.Dir),
                    h.Record!.GhPath,
                    h.Record.LastConnectUtc,
                    h.Record.OrphanedAtUtc,
                    TryLedgerChars(h.Dir)))
                .ToList();

        static int TryLedgerChars(string homeDir)
        {
            try
            {
                var ledger = Path.Combine(homeDir, "MEMORY.md");
                return File.Exists(ledger) ? File.ReadAllText(ledger).Length : 0;
            }
            catch { return 0; }
        }

        static string NameList(IEnumerable<string> dirs)
        {
            var names = dirs.Select(Path.GetFileName).ToList();
            const int maxListed = 4;
            var listed = string.Join(", ", names.Take(maxListed));
            return names.Count > maxListed ? listed + $" …(+{names.Count - maxListed} more)" : listed;
        }

        /// <summary>Recursive best-effort copy: a locked file (a live session can hold handles in
        /// the source home) is counted, never fatal — the caller reports the count honestly.</summary>
        static int CopyTreeBestEffort(string sourceDir, string destDir)
        {
            var failures = 0;
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                try { File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true); }
                catch { failures++; }
            }
            foreach (var sub in Directory.GetDirectories(sourceDir))
                failures += CopyTreeBestEffort(sub, Path.Combine(destDir, Path.GetFileName(sub)));
            return failures;
        }

        // ---- orphan sweep ----------------------------------------------------------------------

        /// <summary>
        /// Post-Connect pass over every OTHER home's record: a recorded .gh gone from disk gets an
        /// orphan stamp; a stamp older than <see cref="ArchiveAfter"/> moves the whole home into
        /// <c>archive/</c> (never delete); a path that exists again clears the stamp (restored from
        /// source control). Returns a one-line note only when something changed, else null.
        /// </summary>
        public static string? Sweep(WireifyPaths paths, string? currentHomeDir, DateTime utcNow)
        {
            if (!Directory.Exists(paths.ProjectsDir)) return null;

            int orphaned = 0, archived = 0, restored = 0;
            foreach (var dir in Directory.GetDirectories(paths.ProjectsDir))
            {
                if (PathsEqual(dir, currentHomeDir)) continue; // just connected — definitionally alive
                try
                {
                    var record = Read(dir);
                    if (record is null) continue; // pre-identity or foreign folder: untouched

                    if (File.Exists(record.GhPath))
                    {
                        if (record.OrphanedAtUtc is null) continue;
                        WriteRecord(dir, record with { OrphanedAtUtc = null });
                        restored++;
                    }
                    else if (record.OrphanedAtUtc is null)
                    {
                        WriteRecord(dir, record with { OrphanedAtUtc = utcNow });
                        orphaned++;
                    }
                    else if (utcNow - record.OrphanedAtUtc.Value > ArchiveAfter)
                    {
                        Directory.CreateDirectory(paths.ArchiveDir);
                        var target = Path.Combine(paths.ArchiveDir, Path.GetFileName(dir));
                        if (Directory.Exists(target))
                            target += "-" + utcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                        Directory.Move(dir, target);
                        archived++;
                    }
                }
                catch { /* fail-open per home: a locked or odd folder waits for the next Connect */ }
            }

            if (orphaned + archived + restored == 0) return null;
            var parts = new List<string>();
            if (orphaned > 0) parts.Add($"{orphaned} marked orphaned (its .gh is gone)");
            if (archived > 0) parts.Add($"{archived} archived (orphaned over {ArchiveAfter.TotalDays:0} days — moved to archive/, kept)");
            if (restored > 0) parts.Add($"{restored} restored (its .gh is back)");
            return "home sweep: " + string.Join(", ", parts);
        }

        // ---- homes index -----------------------------------------------------------------------

        /// <summary>
        /// Regenerate <c>~/.ify/wireify/homes.md</c>: one table row per home under
        /// <c>projects/</c> and <c>archive/</c>, from the per-home identity records. Strictly a
        /// one-way cache for human eyes — the records stay authoritative (an index can desync; a
        /// per-home record cannot), nothing ever reads this file back, and every Connect
        /// overwrites it whole. Best-effort: a failure is swallowed and the next Connect rewrites.
        /// </summary>
        public static void WriteHomesIndex(WireifyPaths paths, DateTime utcNow)
        {
            try
            {
                var rows = ScanHomes(paths)
                    .Select(h => (h.Dir, h.FromArchive, Record: Read(h.Dir), Ledger: TryLedgerChars(h.Dir)))
                    .OrderBy(h => h.FromArchive)
                    .ThenBy(h => h.Record is null)
                    .ThenByDescending(h => h.Record?.LastConnectUtc ?? DateTime.MinValue)
                    .ToList();

                var sb = new System.Text.StringBuilder();
                sb.Append("# Wireify homes — generated index\n\n");
                sb.Append("One row per agent home, from the per-home identity records " +
                    "(`projects/<home>/.wireify/home.json`) — those records are the authority; this " +
                    "file is a read-only snapshot, regenerated on every Connect, and edits here are " +
                    "overwritten. Last regenerated: " +
                    utcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC.\n\n");

                if (rows.Count == 0)
                {
                    sb.Append("No homes yet — Connect on a saved `.gh` creates the first one.\n");
                }
                else
                {
                    sb.Append("| Home | Definition (.gh) | Last Connect (UTC) | Status | Ledger |\n");
                    sb.Append("|---|---|---|---|---|\n");
                    foreach (var row in rows)
                    {
                        var id = (row.FromArchive ? "archive/" : "") + Path.GetFileName(row.Dir);
                        var path = row.Record is null ? "—" : "`" + Cell(row.Record.GhPath) + "`";
                        var last = row.Record is null
                            ? "—"
                            : row.Record.LastConnectUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                        var ledger = row.Ledger > 0
                            ? row.Ledger.ToString("N0", CultureInfo.InvariantCulture) + " chars"
                            : "—";
                        sb.Append($"| `{Cell(id)}` | {path} | {last} | {IndexStatus(row.Record)} | {ledger} |\n");
                    }
                }
                HomeScaffolder.WriteAllText(Path.Combine(paths.Root, "homes.md"), sb.ToString());
            }
            catch { /* a cache only — never let it near the Connect */ }
        }

        static string IndexStatus(HomeRecord? record)
            => record is null ? "no identity record (pre-0.2 home)"
                : record.AdoptedInto is not null ? "adopted into `" + record.AdoptedInto + "`"
                : record.OrphanedAtUtc is not null
                    ? "orphaned since " + record.OrphanedAtUtc.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : "active";

        static string Cell(string s) => s.Replace("|", "\\|");

        // ---- helpers ---------------------------------------------------------------------------

        static IEnumerable<(string Dir, bool FromArchive)> ScanHomes(WireifyPaths paths)
        {
            if (Directory.Exists(paths.ProjectsDir))
                foreach (var dir in Directory.GetDirectories(paths.ProjectsDir))
                    yield return (dir, false);
            if (Directory.Exists(paths.ArchiveDir))
                foreach (var dir in Directory.GetDirectories(paths.ArchiveDir))
                    yield return (dir, true);
        }

        /// <summary>Lowercase hex SHA-256 of the file's bytes (streamed — .gh files run to tens of
        /// MB), or null when unreadable. The read is the only touch the .gh ever gets.</summary>
        public static string? TrySha256(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha.ComputeHash(stream);
                var hex = new System.Text.StringBuilder(hash.Length * 2);
                foreach (var b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
            catch { return null; }
        }

        static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        static string SafeDirectoryName(string path)
        {
            try { return Path.GetDirectoryName(SafeFullPath(path)) ?? ""; } catch { return ""; }
        }

        static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(
                SafeFullPath(a!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                SafeFullPath(b!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
