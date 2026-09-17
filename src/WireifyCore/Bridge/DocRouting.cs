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
        /// <summary>Context named a home that neither a session binding nor any open document's
        /// path resolves — refuse with the closed-definition code (open the file and it routes).</summary>
        UnknownHome,
        /// <summary>The bound document is not open in this Rhino — refuse with recovery.</summary>
        NotOpen,
        /// <summary>Mutation requested while the bound document is open but not the active canvas —
        /// refuse; the document is untouched (reads route instead).</summary>
        NotActive,
    }

    /// <summary>A routing refusal with its parts kept apart: the protocol <see cref="Code"/> and
    /// the <see cref="FileName"/> the surface should name (null when no open document names
    /// it), beside the agent-facing message every MCP tool still throws verbatim. The app
    /// surface renders its own page-shaped copy from the parts — the MCP session's sentence
    /// ("this session is connected to …") reached a browser user in round 10 (S10.7/S10.3), an
    /// audience with no terminal, no tools, and no memory ledger.</summary>
    public sealed class DocRoutingException : InvalidOperationException
    {
        public DocRoutingException(string code, DocResolution decision, string? fileName, string message)
            : base(message)
        {
            Code = code;
            Decision = decision;
            FileName = fileName;
        }

        public string Code { get; }
        public DocResolution Decision { get; }
        public string? FileName { get; }
    }

    /// <summary>What a live solution feed does when the document its home resolves to is no
    /// longer the one it is attached to. Pure — the feed's rebinding rule is pinned without
    /// Rhino (round-10 S10.7: a feed attached once at stream-open kept streaming the renamed
    /// copy while the state GET read the reopened original, and the page showed two files).</summary>
    public enum FeedRebind
    {
        /// <summary>The attached document is still the resolved one: nothing to do.</summary>
        Keep,
        /// <summary>Another open document resolves for the home: move the handlers and push a
        /// fresh frame so the page flips to the right file.</summary>
        Switch,
        /// <summary>Nothing resolves any more: end the feed honestly (the page polls and heals
        /// when the file is back).</summary>
        Close,
    }

    public static class DocRouting
    {
        /// <summary>The routing decision. Reads route to the bound document wherever it sits;
        /// mutations additionally require it to be the active canvas — changes only ever happen
        /// on the canvas the user is looking at. The companion app's value path is a mutation
        /// like any other: round 7 exempted it for one build and measured the cost — pushes
        /// LANDED on a background tab but nothing downstream SOLVED until the tab was fronted
        /// (Grasshopper solves only the front document), so the page showed a new input beside
        /// empty outputs while its pill read live. Refusing is the honest answer (round-7
        /// finding 1, Hossein's call: the definition must be in front to interact).</summary>
        public static DocResolution Decide(
            bool hasContext, bool bindingKnown, bool docOpen, bool docIsActive, bool forMutation)
        {
            if (!hasContext) return DocResolution.UseActive;
            if (!bindingKnown) return DocResolution.UnknownHome;
            if (!docOpen) return DocResolution.NotOpen;
            if (forMutation && !docIsActive) return DocResolution.NotActive;
            return DocResolution.UseBound;
        }

        /// <summary>Which open document a home resolves to when two candidates exist: the one a
        /// Connect bound (the instance the terminal was launched for) and the one whose CURRENT
        /// path hashes to the home. The companion app is path-first AND path-only — the app
        /// belongs to the file, needs no Connect at all (round-9 S9.47: a page served before any
        /// Build could not read its document), and after a Save As it reads "closed" until the
        /// original is reopened rather than following the terminal's instance to the copy
        /// (round-10 S10.7: the binding fallback is how the page ended up rendering two files).
        /// The MCP path is binding-first so a terminal keeps editing the instance it was launched
        /// for across a Save As, with the path match as its fallback for a re-opened file.
        /// Null when nothing resolves.</summary>
        public static T? Pick<T>(bool pathFirst, T? bound, T? byPath) where T : class
            => pathFirst ? byPath : bound ?? byPath;

        /// <summary>The file a refusal names. Path-first surfaces name the path-matched document
        /// only — a session binding may have followed a Save As to another file, and naming it
        /// told a page user the wrong file was "not the active canvas" at the exact moment it was
        /// (round-10 S10.7). Null means no open document names the file; the surface falls back
        /// to what it knows (the home's own record).</summary>
        public static string? FileNameFor(bool pathFirst, string? boundName, string? targetName)
            => pathFirst ? targetName : boundName ?? targetName;

        /// <summary>The feed's rebinding rule, evaluated whenever the open documents change or a
        /// frame is about to be built.</summary>
        public static FeedRebind DecideFeed(bool attachedIsResolved, bool resolvedExists)
        {
            if (attachedIsResolved) return FeedRebind.Keep;
            return resolvedExists ? FeedRebind.Switch : FeedRebind.Close;
        }
    }
}
