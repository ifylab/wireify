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
    /// <c>X-Wireify-Home</c> header), the call routes to the document that session was Connected
    /// for — by document id first (survives SaveAs), then by file path (survives close/reopen) —
    /// regardless of which canvas is in front. Without context (legacy/debug clients), the active
    /// document, as before. The decision table itself is <see cref="DocRouting.Decide"/> — pure
    /// and unit-tested; this class only supplies the Grasshopper facts.
    ///
    /// Reads route anywhere; mutations require the bound document to BE the active canvas, so
    /// changes only ever happen where the user is looking (<see cref="DocResolution.NotActive"/>
    /// refuses with recovery instead).
    /// </summary>
    public sealed class SessionDocumentResolver
    {
        readonly Func<GH_Document?> _activeDocument;
        readonly Func<string, SessionBinding?>? _bindings;

        // One slot, not a stack: MarshallingBridge serializes every bridge call, so exactly one
        // call's context exists at a time — set under its gate, cleared before release.
        string? _currentHomeId;

        public SessionDocumentResolver(
            Func<GH_Document?> activeDocument, Func<string, SessionBinding?>? bindings = null)
        {
            _activeDocument = activeDocument ?? throw new ArgumentNullException(nameof(activeDocument));
            _bindings = bindings;
        }

        /// <summary>Set (and clear, with null) by the marshalling seam around each serialized call.</summary>
        public void SetCallContext(string? homeId) => _currentHomeId = homeId;

        /// <summary>True when <paramref name="doc"/> is the canvas in front — reported on the
        /// document summary so the agent always knows whether its definition is the one on screen.</summary>
        public bool IsActive(GH_Document doc) => ReferenceEquals(doc, _activeDocument());

        public GH_Document Resolve(bool forMutation)
        {
            var homeId = _bindings is null ? null : _currentHomeId;
            var binding = homeId is null ? null : _bindings!(homeId);
            var bound = binding is null ? null : FindOpen(binding);
            var decision = DocRouting.Decide(
                hasContext: homeId is not null,
                bindingKnown: binding is not null,
                docOpen: bound is not null,
                docIsActive: bound is not null && IsActive(bound),
                forMutation: forMutation);

            switch (decision)
            {
                case DocResolution.UseBound:
                    return bound!;
                case DocResolution.NoSession:
                    throw new InvalidOperationException(ErrorProtocol.NoSession(homeId!));
                case DocResolution.NotOpen:
                    throw new InvalidOperationException(ErrorProtocol.DocNotOpen(binding!.FileName));
                case DocResolution.NotActive:
                    throw new InvalidOperationException(ErrorProtocol.DocNotActive(binding!.FileName));
                default:
                    return _activeDocument() ?? throw new InvalidOperationException(ErrorProtocol.NoDoc());
            }
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
