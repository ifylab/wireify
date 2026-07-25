// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Deterministic, LLM-free maintenance for a home's MEMORY.md: drop byte-identical duplicate
    /// lessons and move the oldest entries out to an archive when the file is over budget. Pure
    /// string-in/string-out so it is unit-testable without Rhino; all file IO (snapshots, the
    /// atomic write, the archive append) belongs to the caller.
    ///
    /// The shape guard is the safety contract: anything that does not parse as
    /// "managed header .. wireify:end .. '## Lessons' .. '### '-delimited entries" — legacy free
    /// text, a foreign format, user-added sections — is left completely untouched. Code fences
    /// inside entries are respected (a '###' or '#' line inside ``` fences is content, not
    /// structure). Archive-only, never delete.
    /// </summary>
    public static class MemoryLedger
    {
        const string BlockEnd = "<!-- wireify:end -->";
        const string LessonsHeading = "## Lessons";
        const string EntryPrefix = "### ";

        /// <param name="Text">The maintained file text (equal to the input when nothing changed).</param>
        /// <param name="ArchiveAppend">Entries removed by overflow, in file order, to append to the archive; null when none.</param>
        public sealed record Result(
            string Text,
            string? ArchiveAppend,
            int DroppedDuplicates,
            int ArchivedEntries,
            bool Conforming,
            bool Changed);

        public static Result Maintain(string text, int budgetChars)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));

            var parsed = Parse(text);
            if (parsed is null)
                return new Result(text, null, 0, 0, Conforming: false, Changed: false);

            var (prefix, entries) = parsed.Value;

            // Belt-and-braces round-trip: if reassembly is not byte-identical, the parse missed
            // something — refuse to touch the file.
            if (!string.Equals(prefix + string.Concat(entries), text, StringComparison.Ordinal))
                return new Result(text, null, 0, 0, Conforming: false, Changed: false);

            // Exact-duplicate collapse, first occurrence kept (entries are newest-first, so the
            // surviving copy is the one the agent sees first).
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<string>();
            var dropped = 0;
            foreach (var entry in entries)
            {
                if (seen.Add(entry.TrimEnd('\n', '\r'))) kept.Add(entry);
                else dropped++;
            }

            // Overflow: move the OLDEST whole entries (the tail — newest-first file order) out to
            // the archive until the file fits the budget. Never below one surviving entry: a
            // single oversized lesson stays where the agent can consolidate it.
            var archived = new List<string>();
            while (kept.Count > 1 && prefix.Length + kept.Sum(e => e.Length) > budgetChars)
            {
                archived.Insert(0, kept[kept.Count - 1]);
                kept.RemoveAt(kept.Count - 1);
            }

            var changed = dropped > 0 || archived.Count > 0;
            var newText = changed ? prefix + string.Concat(kept) : text;
            var archiveAppend = archived.Count > 0 ? string.Concat(archived) : null;
            return new Result(newText, archiveAppend, dropped, archived.Count, Conforming: true, Changed: changed);
        }

        /// <summary>Stats for the Connect memory glance: entry count plus the newest entry's date
        /// token (entries are newest-first by the ledger contract; null when the first heading
        /// carries no parseable date). A null result means the file is not tool-shaped — the
        /// caller's maintenance note already reports that state.</summary>
        public sealed record LedgerStats(int Entries, string? NewestDate);

        public static LedgerStats? Stats(string text)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));
            var parsed = Parse(text);
            if (parsed is null) return null;

            var entries = parsed.Value.Entries;
            string? newest = null;
            if (entries.Count > 0)
            {
                var token = entries[0].Substring(EntryPrefix.Length)
                    .Split(' ', '\t', '\r', '\n')[0];
                if (DateTime.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _))
                    newest = token;
            }
            return new LedgerStats(entries.Count, newest);
        }

        /// <summary>Split the file into (prefix = header + Lessons heading + preamble) and the raw
        /// entry chunks. Null when the file is not tool-shaped. Slice-based over physical lines
        /// (line endings kept), so concatenating the parts reproduces the input byte-for-byte.</summary>
        static (string Prefix, List<string> Entries)? Parse(string text)
        {
            var end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
            if (end < 0) return null;
            var bodyStart = end + BlockEnd.Length;

            var lines = SplitKeepEol(text, bodyStart);
            var i = 0;

            // Between the end marker and '## Lessons': whitespace only.
            while (i < lines.Count && lines[i].Trim().Length == 0) i++;
            if (i == lines.Count) return null;
            if (lines[i].TrimEnd('\r', '\n') != LessonsHeading) return null;
            i++;

            // After the heading: blank lines and single-line HTML comments until the first entry.
            // Any other content (another section, free text) is not ours — refuse.
            while (i < lines.Count && !lines[i].StartsWith(EntryPrefix, StringComparison.Ordinal))
            {
                var t = lines[i].Trim();
                var filler = t.Length == 0 ||
                    (t.StartsWith("<!--", StringComparison.Ordinal) && t.EndsWith("-->", StringComparison.Ordinal));
                if (!filler) return null;
                i++;
            }

            var prefixBuilder = new StringBuilder(text.Substring(0, bodyStart));
            for (var k = 0; k < i; k++) prefixBuilder.Append(lines[k]);
            var prefix = prefixBuilder.ToString();

            // Entries: '### '-headed chunks. Fence state tracked so '#'-leading lines inside
            // ``` blocks are content; top-level '## '/'# ' lines are foreign structure — refuse.
            var entries = new List<string>();
            var current = new StringBuilder();
            var fence = false;
            for (; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!fence)
                {
                    if (line.StartsWith(EntryPrefix, StringComparison.Ordinal))
                    {
                        if (current.Length > 0) { entries.Add(current.ToString()); current.Clear(); }
                    }
                    else if (current.Length == 0)
                    {
                        return null; // content before the first entry heading — not tool-shaped
                    }
                    else if (line.StartsWith("## ", StringComparison.Ordinal) ||
                             line.StartsWith("# ", StringComparison.Ordinal))
                    {
                        return null; // user structure inside the lessons zone — leave the file alone
                    }
                }
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) fence = !fence;
                current.Append(line);
            }
            if (current.Length > 0) entries.Add(current.ToString());
            if (fence) return null; // unterminated fence — too ambiguous to maintain safely

            return (prefix, entries);
        }

        static List<string> SplitKeepEol(string text, int from)
        {
            var lines = new List<string>();
            var pos = from;
            while (pos < text.Length)
            {
                var nl = text.IndexOf('\n', pos);
                if (nl < 0) { lines.Add(text.Substring(pos)); break; }
                lines.Add(text.Substring(pos, nl - pos + 1));
                pos = nl + 1;
            }
            return lines;
        }
    }
}
