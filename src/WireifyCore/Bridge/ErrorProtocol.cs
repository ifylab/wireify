// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Stable error codes with their recovery protocol lines, in-band. The loop skill keys on the
    /// WIREIFY_* prefixes (never on free prose — the phrases differed between throw sites before
    /// codes existed), and every message tells the agent how to recover without a follow-up call.
    /// WIREIFY_NOT_FOUND additionally carries the current W-registry so a stale id (the systematic
    /// case: socket ids vanish on convert, new ids appear on undo) self-repairs with zero
    /// re-orientation calls. Pure string building — unit-tested without Rhino.
    /// </summary>
    public static class ErrorProtocol
    {
        public const string BusyCode = "WIREIFY_BUSY";
        public const string QueueTimeoutCode = "WIREIFY_QUEUE_TIMEOUT";
        public const string NotFoundCode = "WIREIFY_NOT_FOUND";
        public const string NoDocCode = "WIREIFY_NO_DOC";
        public const string NotASocketCode = "WIREIFY_NOT_A_SOCKET";
        public const string InputWiredCode = "WIREIFY_INPUT_WIRED";
        public const string ExternalEditCode = "WIREIFY_EXTERNAL_EDIT";
        public const string DocNotOpenCode = "WIREIFY_DOC_NOT_OPEN";
        public const string DocNotActiveCode = "WIREIFY_DOC_NOT_ACTIVE";

        /// <summary>Appended to a mutation error from the second consecutive failure on the same
        /// component — the prose two-strikes rule made mechanical. Advisory: it instructs, it
        /// never blocks (the user may have said keep going).</summary>
        public const string LeashLine =
            "LEASH: second consecutive failed mutation on this component — stop, report this exact " +
            "error to the user verbatim, and ask how to proceed.";

        const int MaxRegistryEntries = 20;

        public static string Busy(double pickupSeconds) =>
            $"{BusyCode}: Rhino's UI thread did not pick this call up within {pickupSeconds:0}s — " +
            "busy or blocked (a long solve, a modal dialog, or a hung operation). The call was NOT executed. " +
            "Recovery: let Rhino go idle, retry once; if it times out again, stop and tell the user " +
            "(a Rhino restart clears a wedged engine).";

        public static string QueueTimeout(string tool, double queueSeconds) =>
            $"{QueueTimeoutCode}: {tool} — another wireify call has held Grasshopper for over " +
            $"{queueSeconds:0}s (a long solve, or Rhino is blocked); this call was NOT started. " +
            "Recovery: wait for the canvas to settle, retry once; if it repeats, stop and report it.";

        public static string NoDoc() =>
            $"{NoDocCode}: no active Grasshopper document. Recovery: ask the user to open the " +
            "definition in Grasshopper, then retry.";

        /// <summary>This session's document is not open in this Rhino — the session outlived its
        /// definition. Nothing is retried against another canvas, ever.</summary>
        public static string DocNotOpen(string fileName) =>
            $"{DocNotOpenCode}: this session is connected to '{fileName}', which is not open in this " +
            "Rhino. This session's tools only ever touch that definition. Recovery: ask the user to " +
            "reopen it, or to Connect from the definition they mean to work on (its socket button or " +
            "_Wireify) — that spawns a separate session with that definition's own memory.";

        /// <summary>A mutation while the session's document is open but not the front canvas.
        /// Reads route to the bound document anywhere; changes only happen where the user is
        /// looking. The document is untouched.</summary>
        public static string DocNotActive(string fileName) =>
            $"{DocNotActiveCode}: this session is connected to '{fileName}', which is open but not the " +
            "active canvas — mutations only run on the canvas the user is looking at, and the document " +
            "is untouched. Reads still work from the background: keep orienting and investigating freely, " +
            $"and ask the user to bring '{fileName}' to front (its Grasshopper tab) only when you are ready " +
            "to mutate, then call again. Never dodge this by retargeting components on the front canvas.";

        /// <summary>The request carried a session header the server does not know — a hand-crafted
        /// client, or a stale config. Not agent-recoverable; no stable code.</summary>
        public static string NoSession(string homeId) =>
            $"no session is registered for '{homeId}' on this server — Connect from Rhino (socket " +
            "button or _Wireify) to establish one; a hand-written client config cannot route.";

        /// <summary>set_source refused a blind overwrite: the component's stamped body hash no
        /// longer matches — the user edited the code outside Wireify (the GH script editor). The
        /// current source rides in the error so the agent merges without a follow-up read; nothing
        /// changed on the document.</summary>
        public static string ExternalEdit(string nickName, Guid id, string currentSource)
        {
            const int maxEmbedded = 16000;
            var source = currentSource ?? "";
            var truncated = source.Length > maxEmbedded;
            if (truncated) source = source.Substring(0, maxEmbedded);
            return $"{ExternalEditCode}: '{nickName}' ({id}) was modified outside this session since Wireify last " +
                "wrote it — overwriting now would destroy those edits. Recovery: the component's CURRENT code is " +
                "included below; merge your change into it, get the user's explicit OK, and call set_source again " +
                "with overwriteExternalEdits: true — the guard refuses EVERY write while hand-edited code sits on " +
                "the component, a correct merge included, so the flag is required for the write that follows the " +
                "merge (that write re-stamps and re-arms the guard). Never pass the flag without the user's OK: " +
                "on an unmerged write it discards their edits." +
                (truncated ? $" (current source truncated at {maxEmbedded:N0} chars — get_source has the rest)" : "") +
                "\n--- current source ---\n" + source;
        }

        /// <summary>A Strict-mode wire refused an occupied input (the round-15 contamination
        /// class: a merge the agent never chose). Nothing changed; the recovery line names the
        /// two explicit modes.</summary>
        public static string InputWired(string inputParam, Guid componentId, IReadOnlyList<WireEndInfo> sources)
        {
            const int maxListed = 10;
            var listed = string.Join(", ", sources.Take(maxListed).Select(s => $"{s.NickName}.{s.Param}"));
            if (sources.Count > maxListed) listed += $" …(+{sources.Count - maxListed} more)";
            return $"{InputWiredCode}: input '{inputParam}' on {componentId} already has {sources.Count} " +
                $"source(s): {listed}. Recovery: pass mode 'replace' to swap the existing wire(s) out (one undo), " +
                "or 'add' to merge deliberately (branches combine); wiring without a mode never touches an occupied input.";
        }

        public static string NotASocket(Guid id, string actualName) =>
            $"{NotASocketCode}: component {id} is not a Wireify socket (it is '{actualName}'). " +
            "Recovery: revise an existing Python component with set_source in place; convert only ids " +
            "listed as staged in get_document_summary's wireify registry.";

        public static string NotFound(Guid id, IReadOnlyList<WireifyComponentInfo> registry) =>
            $"{NotFoundCode}: no object with id {id} in the document — ids go stale after " +
            "convert_staged (the socket id is replaced by the new component's) and after undo. " +
            "Recovery: take the current id from the registry here; re-orient with get_document_summary " +
            $"only if it is not listed. Registry: {FormatRegistry(registry)}";

        static string FormatRegistry(IReadOnlyList<WireifyComponentInfo> registry)
        {
            if (registry is null || registry.Count == 0) return "(no Wireify components on the canvas)";
            var sb = new StringBuilder();
            var shown = 0;
            foreach (var entry in registry.OrderBy(e => e.Number))
            {
                if (shown == MaxRegistryEntries) { sb.Append($" …(+{registry.Count - shown} more)"); break; }
                if (shown > 0) sb.Append("; ");
                sb.Append($"W{entry.Number} '{entry.NickName}' {entry.State} id={entry.Id}");
                shown++;
            }
            return sb.ToString();
        }
    }
}
