// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using Grasshopper;
using Grasshopper.Kernel;

namespace WireifyCore.Bridge
{
    /// <summary>
    /// Resolves the Grasshopper document a bridge call should operate on. With a session context
    /// (set per serialized call by <see cref="MarshallingBridge"/> from the request's
    /// <c>X-Wireify-Home</c> header, or from the app URL's home segment), the call routes to the
    /// document that home names — regardless of which canvas is in front. Two candidates exist:
    /// the document a Connect bound (by document id first, so it survives a SaveAs; then by file
    /// path, so it survives close/reopen) and the open document whose CURRENT path hashes to the
    /// home (no Connect needed — the companion app is served and read without any Claude session,
    /// round-9 S9.47). <see cref="DocRouting.Pick"/> orders them per surface. Without context
    /// (legacy/debug clients), the active document, as before. The decision table itself is
    /// <see cref="DocRouting.Decide"/> — pure and unit-tested; this class only supplies the
    /// Grasshopper facts.
    ///
    /// Reads route anywhere; mutations require the bound document to BE the active canvas, so
    /// changes only ever happen where the user is looking (<see cref="DocResolution.NotActive"/>
    /// refuses with recovery instead). The companion app's value pushes are mutations too —
    /// Grasshopper solves only the front document, so a push that landed on a background tab
    /// would sit unsolved (round-7 finding 1); the page renders the refusal instead.
    ///
    /// Refusals throw <see cref="DocRoutingException"/>: the MCP tools rethrow its agent-facing
    /// message verbatim, the app surface renders page copy from its parts (round-10 S10.7).
    /// </summary>
    public sealed class SessionDocumentResolver
    {
        readonly Func<GH_Document?> _activeDocument;
        readonly Func<string, SessionBinding?>? _bindings;

        // One slot, not a stack: MarshallingBridge serializes every bridge call, so exactly one
        // call's context exists at a time — set under its gate, cleared before release.
        SessionCallContext? _current;

        public SessionDocumentResolver(
            Func<GH_Document?> activeDocument, Func<string, SessionBinding?>? bindings = null)
        {
            _activeDocument = activeDocument ?? throw new ArgumentNullException(nameof(activeDocument));
            _bindings = bindings;
        }

        /// <summary>Set (and clear, with null) by the marshalling seam around each serialized call.</summary>
        public void SetCallContext(SessionCallContext? context) => _current = context;

        /// <summary>The calling session's context while a marshalled call runs — a solution feed
        /// captures it at subscribe time so it can re-resolve its document later, on the UI
        /// thread, outside any call (round-10 S10.7).</summary>
        public SessionCallContext? CurrentContext => _bindings is null ? null : _current;

        /// <summary>True when <paramref name="doc"/> is the canvas in front — reported on the
        /// document summary so the agent always knows whether its definition is the one on screen.</summary>
        public bool IsActive(GH_Document doc) => ReferenceEquals(doc, _activeDocument());

        public GH_Document Resolve(bool forMutation) => Resolve(CurrentContext, forMutation);

        /// <summary>The open document a context resolves to for a read, or null when none does —
        /// the feed's rebinding question, asked without a marshalled call and without the
        /// exception a request path wants.</summary>
        public GH_Document? TryResolve(SessionCallContext? context)
        {
            if (context is null) return _activeDocument();
            var binding = _bindings?.Invoke(context.HomeId);
            var bound = binding is null ? null : FindOpen(binding);
            return DocRouting.Pick(context.PathFirst, bound, FindByHome(context.HomeId));
        }

        public GH_Document Resolve(SessionCallContext? context, bool forMutation)
        {
            var homeId = context?.HomeId;
            var binding = homeId is null ? null : _bindings?.Invoke(homeId);
            var bound = binding is null ? null : FindOpen(binding);
            var byPath = homeId is null ? null : FindByHome(homeId);
            var pathFirst = context?.PathFirst ?? false;
            var target = DocRouting.Pick(pathFirst, bound, byPath);
            // A binding whose document is closed still "knows" the home: the refusal names the
            // file. Only a home nobody has bound AND no open path resolves is unknown.
            var known = binding is not null || byPath is not null;
            var decision = DocRouting.Decide(
                hasContext: homeId is not null,
                bindingKnown: known,
                docOpen: target is not null,
                docIsActive: target is not null && IsActive(target),
                forMutation: forMutation);

            // Path-first surfaces name the path-matched document only: after a Save As the
            // binding names the copy, and the page was told the wrong file was "not the active
            // canvas" at the exact moment it was (round-10 S10.7).
            var named = DocRouting.FileNameFor(pathFirst, binding?.FileName, FileNameOf(target ?? byPath));
            var fileName = named ?? homeId ?? "";
            switch (decision)
            {
                case DocResolution.UseBound:
                    return target!;
                case DocResolution.UnknownHome:
                    throw new DocRoutingException(
                        ErrorProtocol.DocNotOpenCode, decision, named, ErrorProtocol.UnknownHome(homeId!));
                case DocResolution.NotOpen:
                    throw new DocRoutingException(
                        ErrorProtocol.DocNotOpenCode, decision, named, ErrorProtocol.DocNotOpen(fileName));
                case DocResolution.NotActive:
                    throw new DocRoutingException(
                        ErrorProtocol.DocNotActiveCode, decision, named, ErrorProtocol.DocNotActive(fileName));
                default:
                    return _activeDocument() ?? throw new InvalidOperationException(ErrorProtocol.NoDoc());
            }
        }

        /// <summary>The open document whose current file path hashes to <paramref name="homeId"/>
        /// — the inverse of <see cref="WireifyCore.Connect.WireifyPaths.HomeId"/>, computed live,
        /// so a definition that was merely OPENED (never Connected) resolves for its app.</summary>
        static GH_Document? FindByHome(string homeId)
        {
            foreach (var doc in OpenDocuments())
            {
                if (string.IsNullOrEmpty(doc.FilePath)) continue;
                if (string.Equals(WireifyCore.Connect.WireifyPaths.HomeId(doc.FilePath), homeId,
                        StringComparison.OrdinalIgnoreCase))
                    return doc;
            }
            return null;
        }

        internal static string? FileNameOf(GH_Document? doc)
        {
            if (doc is null || string.IsNullOrEmpty(doc.FilePath)) return null;
            try { return Path.GetFileName(doc.FilePath); } catch { return null; }
        }

        /// <summary>True while <paramref name="doc"/> is listed by the document server — a
        /// closing document unloads first and is removed after, so "unloaded" alone never means
        /// "backgrounded" (round-10 S10.3).</summary>
        internal static bool IsOpen(GH_Document doc)
        {
            foreach (var open in OpenDocuments())
                if (ReferenceEquals(open, doc)) return true;
            return false;
        }

        static GH_Document? FindOpen(SessionBinding binding)
        {
            GH_Document? byPath = null;
            foreach (var doc in OpenDocuments())
            {
                if (doc.DocumentID == binding.DocumentId) return doc;
                if (byPath is null && PathsEqual(doc.FilePath, binding.GhPath)) byPath = doc;
            }
            return byPath;
        }

        static System.Collections.Generic.IEnumerable<GH_Document> OpenDocuments()
        {
            var server = Instances.DocumentServer;
            if (server is null) yield break;
            foreach (var entry in (System.Collections.IEnumerable)server)
                if (entry is GH_Document doc) yield return doc;
        }

        static bool PathsEqual(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(
                SafeFullPath(a!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                SafeFullPath(b!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }
    }
}
