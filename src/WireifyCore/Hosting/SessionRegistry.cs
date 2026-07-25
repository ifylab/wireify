// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WireifyContract;
using WireifyCore.Bridge;
using WireifyCore.Connect;

namespace WireifyCore.Hosting
{
    /// <summary>
    /// The controller's book of live sessions: one entry per Connected definition (keyed by home
    /// id), holding what the bridge needs to route calls (the document binding) and what the UIs
    /// need to answer "is THIS definition's session live" (per-definition state, terminal
    /// liveness). Replaces the single global terminal/state that let a second definition's socket
    /// read "do #1" off another file's session. Pure bookkeeping — no Grasshopper types beyond
    /// the plain <see cref="SessionBinding"/> record — so it is unit-tested without Rhino.
    /// </summary>
    public sealed class SessionRegistry
    {
        sealed class Session
        {
            public Session(string homeId, string ghPath, Guid documentId, ITerminalHandle? terminal,
                WireifyConnectionState state)
            {
                HomeId = homeId;
                GhPath = ghPath;
                DocumentId = documentId;
                Terminal = terminal;
                State = state;
            }

            public string HomeId { get; }
            public string GhPath { get; set; }
            public Guid DocumentId { get; set; }
            public ITerminalHandle? Terminal { get; set; }
            public WireifyConnectionState State { get; set; }
            public string FileName => FileNameOf(GhPath);
        }

        /// <summary>Display name for logs/errors — splits on either separator so a Windows path
        /// renders correctly even where '\\' is not the platform separator.</summary>
        static string FileNameOf(string path)
        {
            var i = path.LastIndexOfAny(new[] { '/', '\\' });
            return i < 0 ? path : path.Substring(i + 1);
        }

        readonly object _gate = new();
        readonly Dictionary<string, Session> _byHome = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A Connect happened for this home: create or replace its session. A replaced
        /// terminal re-arms the Connected transition (the fresh terminal must authenticate
        /// itself); the binding always reflects the latest Connect.</summary>
        public void Register(string homeId, string ghPath, Guid documentId, ITerminalHandle? terminal, bool launched)
        {
            if (string.IsNullOrEmpty(homeId)) throw new ArgumentException("homeId required", nameof(homeId));
            if (string.IsNullOrEmpty(ghPath)) throw new ArgumentException("ghPath required", nameof(ghPath));
            var state = launched ? WireifyConnectionState.TerminalLaunched : WireifyConnectionState.ServerListening;
            lock (_gate)
            {
                if (_byHome.TryGetValue(homeId, out var existing))
                {
                    existing.GhPath = ghPath;
                    existing.DocumentId = documentId;
                    existing.Terminal = terminal;
                    existing.State = state;
                }
                else
                {
                    _byHome[homeId] = new Session(homeId, ghPath, documentId, terminal, state);
                }
            }
        }

        /// <summary>An authenticated request named this session. Returns the definition's file
        /// name when the session just BECAME Connected (the log/state moment), else null.</summary>
        public string? MarkAuthenticated(string homeId)
        {
            lock (_gate)
            {
                if (!_byHome.TryGetValue(homeId, out var session)) return null;
                if (session.State >= WireifyConnectionState.Connected) return null;
                session.State = WireifyConnectionState.Connected;
                return session.FileName;
            }
        }

        /// <summary>A tracked terminal exited. When the handle is still the CURRENT terminal of a
        /// session, that session demotes to ServerListening (its socket reads Connect again, the
        /// auth transition re-arms) and its file name is returned; a superseded handle is null.</summary>
        public string? HandleExit(ITerminalHandle handle)
        {
            lock (_gate)
            {
                var session = _byHome.Values.FirstOrDefault(s => ReferenceEquals(s.Terminal, handle));
                if (session is null) return null;
                session.Terminal = null;
                if (session.State < WireifyConnectionState.TerminalLaunched) return null;
                session.State = WireifyConnectionState.ServerListening;
                return session.FileName;
            }
        }

        /// <summary>The session state for a definition (by path), or ServerStopped when it has no
        /// session — the caller layers the server-level state on top.</summary>
        public WireifyConnectionState StateFor(string? ghPath)
        {
            if (string.IsNullOrEmpty(ghPath)) return WireifyConnectionState.ServerStopped;
            lock (_gate)
            {
                var session = _byHome.Values.FirstOrDefault(s => PathsEqual(s.GhPath, ghPath));
                return session?.State ?? WireifyConnectionState.ServerStopped;
            }
        }

        /// <summary>The document binding for a session, or null when the id is unknown — feeds
        /// <see cref="SessionDocumentResolver"/>.</summary>
        public SessionBinding? Binding(string homeId)
        {
            lock (_gate)
            {
                return _byHome.TryGetValue(homeId, out var s)
                    ? new SessionBinding(s.DocumentId, s.GhPath, s.FileName)
                    : null;
            }
        }

        /// <summary>The most-advanced session state — the global state is the max of this and the
        /// server level, so the panel dot keeps its existing meaning ("something is live").</summary>
        public WireifyConnectionState MaxState
        {
            get
            {
                lock (_gate)
                {
                    return _byHome.Count == 0
                        ? WireifyConnectionState.ServerStopped
                        : _byHome.Values.Max(s => s.State);
                }
            }
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
