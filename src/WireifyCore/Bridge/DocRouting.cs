// SPDX-License-Identifier: Apache-2.0
using System;

namespace WireifyCore.Bridge
{
    /// <summary>What a session is bound to: the Grasshopper document it was Connected for.
    /// <c>DocumentId</c> is the GH_Document's id captured at Connect (survives a mid-session
    /// SaveAs); <c>GhPath</c> is the fallback key (a re-opened document keeps its path but mints
    /// a new id); <c>FileName</c> is for human-readable errors.</summary>
    public sealed record SessionBinding(Guid DocumentId, string GhPath, string FileName);

    /// <summary>Where a call should land, decided from what is known about the caller and the
    /// open documents. Pure — the safety-critical routing table is unit-tested without Rhino.</summary>
    public enum DocResolution
    {
        /// <summary>No session context (legacy/debug client): today's active-document behavior.</summary>
        UseActive,
        /// <summary>Route to the session's bound document.</summary>
        UseBound,
        /// <summary>Context named a session the server does not know — refuse (hand-crafted client).</summary>
        NoSession,
        /// <summary>The bound document is not open in this Rhino — refuse with recovery.</summary>
        NotOpen,
        /// <summary>Mutation requested while the bound document is open but not the active canvas —
        /// refuse; the document is untouched (reads route instead).</summary>
        NotActive,
    }

    public static class DocRouting
    {
        /// <summary>The routing decision. Reads route to the bound document wherever it sits;
        /// mutations additionally require it to be the active canvas — changes only ever happen
        /// on the canvas the user is looking at.</summary>
        public static DocResolution Decide(
            bool hasContext, bool bindingKnown, bool docOpen, bool docIsActive, bool forMutation)
        {
            if (!hasContext) return DocResolution.UseActive;
            if (!bindingKnown) return DocResolution.NoSession;
            if (!docOpen) return DocResolution.NotOpen;
            if (forMutation && !docIsActive) return DocResolution.NotActive;
            return DocResolution.UseBound;
        }
    }
}
