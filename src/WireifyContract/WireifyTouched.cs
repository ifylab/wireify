// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace WireifyContract
{
    /// <summary>
    /// Codec for the document's "touched by Wireify" set: instance guids of objects Wireify
    /// created or whose content it wrote, stored as ONE comma-joined string under
    /// <see cref="Key"/> in the document's own value table. Deliberately inert metadata: the
    /// value table is core Grasshopper, saves into the .gh with the user's own save, and a file
    /// carrying it opens stock everywhere — the badge overlay draws from the set when Wireify is
    /// present and nothing reads it otherwise. Append-only by design: a stale guid (its object
    /// undone away or deleted) draws nothing and costs nothing, and a delete + ctrl-Z restore
    /// keeps its badge without any undo-record coupling. Pure string/set work, no GH types.
    /// </summary>
    public static class WireifyTouched
    {
        public const string Key = "WireifyTouched";

        public static HashSet<Guid> Parse(string? raw)
        {
            var set = new HashSet<Guid>();
            if (string.IsNullOrWhiteSpace(raw)) return set;
            foreach (var part in raw!.Split(','))
                if (Guid.TryParse(part.Trim(), out var id) && id != Guid.Empty)
                    set.Add(id);
            return set;
        }

        /// <summary>The raw value with <paramref name="id"/> appended, or null when it is already
        /// present (or empty) — null tells the caller no write is needed.</summary>
        public static string? Append(string? raw, Guid id)
        {
            if (id == Guid.Empty) return null;
            var set = Parse(raw);
            if (!set.Add(id)) return null;
            return Join(set);
        }

        /// <summary>The raw value with <paramref name="id"/> removed, or null when it was not
        /// present — null tells the caller no write is needed. An emptied set joins to "",
        /// which <see cref="Parse"/> reads back as empty.</summary>
        public static string? Remove(string? raw, Guid id)
        {
            var set = Parse(raw);
            if (!set.Remove(id)) return null;
            return Join(set);
        }

        static string Join(HashSet<Guid> set)
        {
            var parts = new List<string>(set.Count);
            foreach (var g in set) parts.Add(g.ToString("D"));
            parts.Sort(StringComparer.Ordinal); // deterministic output — diff- and test-friendly
            return string.Join(",", parts);
        }
    }
}
