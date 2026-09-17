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
            public Session(string homeId, string ghPath, Guid documentId, WireifyConnectionState state)
            {
                HomeId = homeId;
                GhPath = ghPath;
                DocumentId = documentId;
                State = state;
            }

            public string HomeId { get; }
            public string GhPath { get; set; }
            public Guid DocumentId { get; set; }
            /// <summary>Every terminal launched for this definition that has not exited — a
            /// session is live while ANY of them is (round-9 S9.7: tracking only the newest read
            /// Build while an older terminal answered MCP calls, and the click spawned a third).</summary>
            public List<ITerminalHandle> Terminals { get; } = new();
            public WireifyConnectionState State { get; set; }
            public string FileName => FileNameOf(GhPath);
        }

        /// <summary>What a terminal exit meant for its session: the definition's file name and
        /// how many of its terminals are still open (zero = the session demoted).</summary>
        public sealed record TerminalExit(string FileName, int Remaining);

        /// <summary>Display name for logs/errors — splits on either separator so a Windows path
        /// renders correctly even where '\\' is not the platform separator.</summary>
        static string FileNameOf(string path)
        {
            var i = path.LastIndexOfAny(new[] { '/', '\\' });
            return i < 0 ? path : path.Substring(i + 1);
        }

        readonly object _gate = new();
        readonly Dictionary<string, Session> _byHome = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A Connect happened for this home: create or refresh its session. A new
        /// terminal joins the ones still open (the session stays Connected if one of them already
        /// authenticated; otherwise the fresh terminal re-arms the transition); the binding always
        /// reflects the latest Connect.</summary>
        public void Register(string homeId, string ghPath, Guid documentId, ITerminalHandle? terminal, bool launched)
        {
            if (string.IsNullOrEmpty(homeId)) throw new ArgumentException("homeId required", nameof(homeId));
            if (string.IsNullOrEmpty(ghPath)) throw new ArgumentException("ghPath required", nameof(ghPath));
            var state = launched ? WireifyConnectionState.TerminalLaunched : WireifyConnectionState.ServerListening;
            lock (_gate)
            {
                if (!_byHome.TryGetValue(homeId, out var existing))
                {
                    existing = new Session(homeId, ghPath, documentId, state);
                    _byHome[homeId] = existing;
                }
                else
                {
                    existing.GhPath = ghPath;
                    existing.DocumentId = documentId;
                    existing.State = existing.Terminals.Count > 0 && existing.State > state ? existing.State : state;
                }
                if (terminal is not null && !existing.Terminals.Contains(terminal))
                    existing.Terminals.Add(terminal);
            }
        }

        /// <summary>The definition was saved under a new path (Save As) while its session lives:
        /// the session follows the document instance, so its sockets keep reading "Session open"
        /// on the renamed file and the old path reads Build. Returns the new file name when a
        /// session moved, else null (nothing was registered for the old path).</summary>
        public string? Repath(string? oldPath, string? newPath)
        {
            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return null;
            if (PathsEqual(oldPath, newPath)) return null;
            lock (_gate)
            {
                var session = _byHome.Values.FirstOrDefault(s => PathsEqual(s.GhPath, oldPath));
                if (session is null) return null;
                session.GhPath = newPath!;
                return session.FileName;
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

        /// <summary>A tracked terminal exited. The session forgets that handle; only when it was
        /// the LAST open terminal does the session demote to ServerListening (its sockets read
        /// Build again, the auth transition re-arms). Null for a handle no session tracks.</summary>
        public TerminalExit? HandleExit(ITerminalHandle handle)
        {
            lock (_gate)
            {
                var session = _byHome.Values.FirstOrDefault(s => s.Terminals.Contains(handle));
                if (session is null) return null;
                session.Terminals.Remove(handle);
                var remaining = session.Terminals.Count;
                if (remaining == 0 && session.State >= WireifyConnectionState.TerminalLaunched)
                    session.State = WireifyConnectionState.ServerListening;
                return new TerminalExit(session.FileName, remaining);
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

        /// <summary>Every registered session as (home id, .gh path) — the Connect console
        /// iterates these to print an app line per live home, filtering to still-open documents
        /// itself (this registry knows registrations, not the document server).</summary>
        public IReadOnlyList<(string HomeId, string GhPath)> ActiveSessions()
        {
            lock (_gate)
            {
                return _byHome.Values.Select(s => (s.HomeId, s.GhPath)).ToList();
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
