// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Text;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// The pure logic of the socket -> Python-component conversion, kept Rhino-free so it is unit
    /// tested headless: matching staged input names to script input names, and slugifying the
    /// nickname suffix. The bridge does the document work; this decides what maps where.
    /// </summary>
    public static class StagedConversion
    {
        public sealed record InputMatch(string Staged, string Script);

        public sealed record MatchResult(
            IReadOnlyList<InputMatch> Matched,
            IReadOnlyList<string> Unmatched);

        /// <summary>Match staged param names to script param names, case-insensitive, each script
        /// input claimable once. Unmatched staged names are reported (wired ones block conversion).</summary>
        public static MatchResult MatchInputs(IReadOnlyList<string> stagedNames, IReadOnlyList<string> scriptNames)
        {
            if (stagedNames is null) throw new ArgumentNullException(nameof(stagedNames));
            if (scriptNames is null) throw new ArgumentNullException(nameof(scriptNames));

            var matched = new List<InputMatch>();
            var unmatched = new List<string>();
            var claimed = new bool[scriptNames.Count];

            foreach (var staged in stagedNames)
            {
                var found = -1;
                for (var i = 0; i < scriptNames.Count; i++)
                {
                    if (claimed[i]) continue;
                    if (string.Equals(staged, scriptNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        found = i;
                        break;
                    }
                }
                if (found >= 0)
                {
                    claimed[found] = true;
                    matched.Add(new InputMatch(staged, scriptNames[found]));
                }
                else
                {
                    unmatched.Add(staged);
                }
            }
            return new MatchResult(matched, unmatched);
        }

        public sealed record ValidatedIo(
            IReadOnlyList<IoParamSpec> Inputs,
            IReadOnlyList<IoParamSpec> Outputs,
            string? Error);

        static readonly string[] AccessValues = { "item", "list", "tree" };

        /// <summary>Canonicalize an access string; null when invalid.</summary>
        public static string? ParseAccess(string? access)
        {
            if (string.IsNullOrWhiteSpace(access)) return null;
            var a = access!.Trim().ToLowerInvariant();
            return Array.IndexOf(AccessValues, a) >= 0 ? a : null;
        }

        /// <summary>
        /// Validate the explicit I/O specs for a conversion against the staged input names.
        /// Rules: outputs required, unique, valid access; inputs (when given) must cover every
        /// staged name exactly once (case-insensitive; the result carries the staged casing) with
        /// no extras; no name may appear as both input and output. On any violation the Error is
        /// set and the caller must make NO document changes.
        /// </summary>
        public static ValidatedIo ValidateIo(
            IReadOnlyList<string> stagedNames,
            IReadOnlyList<IoParamSpec>? inputs,
            IReadOnlyList<IoParamSpec>? outputs)
        {
            if (stagedNames is null) throw new ArgumentNullException(nameof(stagedNames));

            ValidatedIo Fail(string error) =>
                new(Array.Empty<IoParamSpec>(), Array.Empty<IoParamSpec>(), error);

            if (outputs is null || outputs.Count == 0)
                return Fail("outputs is required: name every output the script assigns (there is no output derivation from source).");

            var outNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedOutputs = new List<IoParamSpec>();
            foreach (var o in outputs)
            {
                if (string.IsNullOrWhiteSpace(o.Name)) return Fail("an output has an empty name.");
                if (!outNames.Add(o.Name.Trim())) return Fail($"duplicate output name '{o.Name.Trim()}'.");
                var access = ParseAccess(o.Access) ?? (string.IsNullOrWhiteSpace(o.Access) ? "item" : null);
                if (access is null) return Fail($"output '{o.Name.Trim()}' has invalid access '{o.Access}' (use item, list, or tree).");
                normalizedOutputs.Add(new IoParamSpec(o.Name.Trim(), access, NormalizeHint(o.TypeHint)));
            }

            List<IoParamSpec> normalizedInputs;
            if (inputs is null || inputs.Count == 0)
            {
                // Default: every staged input, tree access (never mangles), no hints.
                normalizedInputs = new List<IoParamSpec>();
                foreach (var name in stagedNames)
                    normalizedInputs.Add(new IoParamSpec(name, "tree"));
            }
            else
            {
                var byStagedName = new Dictionary<string, IoParamSpec>(StringComparer.OrdinalIgnoreCase);
                foreach (var i in inputs)
                {
                    if (string.IsNullOrWhiteSpace(i.Name)) return Fail("an input has an empty name.");
                    if (byStagedName.ContainsKey(i.Name.Trim())) return Fail($"duplicate input name '{i.Name.Trim()}'.");
                    byStagedName[i.Name.Trim()] = i;
                }

                normalizedInputs = new List<IoParamSpec>();
                foreach (var staged in stagedNames)
                {
                    if (!byStagedName.TryGetValue(staged, out var spec))
                        return Fail($"inputs must cover every staged input; '{staged}' is missing. Staged inputs: [{string.Join(", ", stagedNames)}].");
                    var access = ParseAccess(spec.Access) ?? (string.IsNullOrWhiteSpace(spec.Access) ? "item" : null);
                    if (access is null) return Fail($"input '{staged}' has invalid access '{spec.Access}' (use item, list, or tree).");
                    normalizedInputs.Add(new IoParamSpec(staged, access, NormalizeHint(spec.TypeHint)));
                    byStagedName.Remove(staged);
                }
                if (byStagedName.Count > 0)
                    return Fail($"inputs name(s) not staged on the socket: [{string.Join(", ", byStagedName.Keys)}]. Staged inputs: [{string.Join(", ", stagedNames)}].");
            }

            foreach (var i in normalizedInputs)
                if (outNames.Contains(i.Name))
                    return Fail($"'{i.Name}' is declared as both input and output — script variables must be unique.");

            return new ValidatedIo(normalizedInputs, normalizedOutputs, null);
        }

        static string? NormalizeHint(string? hint)
            => string.IsNullOrWhiteSpace(hint) ? null : hint!.Trim();

        const string HeaderPrefix = "# wireify ";

        /// <summary>
        /// Stamp (or refresh) the one-line provenance header — <c>// # wireify W3 cull-panels</c> —
        /// the durable in-file record of which socket a component came from. Any existing wireify
        /// header is replaced (never duplicated); a leading <c>#!</c> directive line stays first.
        /// </summary>
        public static string StampHeader(string code, string nickName)
        {
            if (code is null) throw new ArgumentNullException(nameof(code));
            if (string.IsNullOrWhiteSpace(nickName)) return code;

            var header = HeaderPrefix + nickName.Trim();
            var lines = new List<string>(code.Replace("\r\n", "\n").Split('\n'));
            lines.RemoveAll(l => l.TrimStart().StartsWith(HeaderPrefix, StringComparison.Ordinal));

            var insertAt = 0;
            while (insertAt < lines.Count && string.IsNullOrWhiteSpace(lines[insertAt])) insertAt++;
            if (insertAt < lines.Count && lines[insertAt].TrimStart().StartsWith("#!", StringComparison.Ordinal))
                insertAt++;

            lines.Insert(insertAt, header);
            return string.Join("\n", lines);
        }

        /// <summary>Normalize a nickname suffix to kebab-case (letters/digits, dash-joined, max 24
        /// chars); null when nothing usable remains.</summary>
        public static string? Slugify(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var sb = new StringBuilder(raw!.Length);
            var pendingDash = false;
            foreach (var c in raw)
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (pendingDash && sb.Length > 0) sb.Append('-');
                    pendingDash = false;
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    pendingDash = true;
                }
                if (sb.Length >= 24) break;
            }
            var slug = sb.ToString().Trim('-');
            return slug.Length == 0 ? null : slug;
        }
    }
}
