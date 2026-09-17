// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WireifyCore.Bridge;

namespace WireifyCore.Hosting
{
    /// <summary>
    /// The companion-app surface on the wireify server: static serving of a home's generated app
    /// (<c>/app/&lt;homeId&gt;/…</c>) plus the live JSON/SSE API under the same root —
    /// <c>/app/&lt;homeId&gt;/api/{state,values,events}</c> — one path prefix, so one cookie
    /// scope covers the page and its API.
    ///
    /// - Auth: a per-home per-run token (fixed-time compared; the MCP secret never appears on
    ///   this surface). The entry link carries <c>?token=</c> once; a valid token on a static
    ///   grants an HttpOnly SameSite=Strict cookie scoped to <c>/app/&lt;homeId&gt;/</c>, so the
    ///   SSE stream and API authenticate without the token living in the URL (EventSource cannot
    ///   set headers — the cookie retires the logged-secret pattern). Header/query stay accepted.
    ///   Statics are gated too; without credentials they answer a small friendly 401 page, and
    ///   no served HTML ever embeds a token.
    /// - Host + Origin validation on every request (LoopbackGuard — the DNS-rebinding belt).
    /// - Statics are traversal-guarded and extension-allowlisted; home ids are charset-validated
    ///   before touching the filesystem.
    /// - Params are manifest-gated: the server reads and pushes ONLY what the home's
    ///   <c>app/manifest.json</c> declares — request bodies never choose params. The manifest is
    ///   also readable as a static, so the page takes labels/groups from it client-side.
    /// - Every API request binds the session to the home the URL names (never the front tab),
    ///   and value pushes run against that document wherever it sits — a background tab included
    ///   (the app is the user's own hand; only the MCP tool mutations keep the front-tab law).
    /// - One SolutionEnd subscription per home, fanned out to its SSE clients; broadcasts are
    ///   latest-wins coalesced and never write on the UI thread; a closed definition ends the
    ///   stream with an honest status frame instead of silence.
    ///
    /// This surface never raises the MCP host's AuthenticatedRequest ("Claude connected") — a
    /// browser is not the agent.
    /// </summary>
    public sealed class AppSurface : IDisposable
    {
        public const string UndeclaredCode = "WIREIFY_APP_UNDECLARED";
        public const string NoManifestCode = "WIREIFY_APP_NO_MANIFEST";
        // A manifest that EXISTS but does not parse is its own code with the parser's line in
        // the message: "no readable manifest" sent a user who typo'd a comma looking for a
        // missing file (round-6 S6.11d/S6.11k).
        public const string BadManifestCode = "WIREIFY_APP_BAD_MANIFEST";
        public const string BadRequestCode = "WIREIFY_APP_BAD_REQUEST";
        // Not-found is its own code: a caller keying on BAD_REQUEST could not tell "no page
        // here" from "malformed request" (round-5 S5.0d, repeating S4.31's note).
        public const string NotFoundCode = "WIREIFY_APP_NOT_FOUND";
        // A missing or wrong credential is its own code: BAD_REQUEST is reserved for shape
        // mistakes, and a client switching on the code (which KIT.md invites) misclassified a
        // bare-address 401 as one (round-10 S10.2).
        public const string UnauthorizedCode = "WIREIFY_APP_UNAUTHORIZED";

        static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
        static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

        // Everything a generated app legitimately serves; anything else 404s (an app dir should
        // never become a general file server).
        static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".map"] = "application/json; charset=utf-8",
            [".txt"] = "text/plain; charset=utf-8",
        };

        sealed class SseClient
        {
            public SseClient(string homeId, HttpListenerResponse response)
            {
                HomeId = homeId;
                Response = response;
            }

            public string HomeId { get; }
            public HttpListenerResponse Response { get; }
            public object WriteGate { get; } = new();
        }

        sealed class HomeFeed
        {
            public List<SseClient> Clients { get; } = new();
            public IDisposable? Subscription { get; set; }
            public bool SubscribePending { get; set; }
            public IDisposable? Watcher { get; set; }
            public bool WatcherPending { get; set; }
            public string? PendingJson { get; set; }
            public bool Flushing { get; set; }
        }

        readonly Func<string, string?> _resolveHomeDir;
        readonly IGrasshopperBridge _bridge;
        readonly Action<string, bool>? _log;
        readonly Func<string, Action, IDisposable> _watchManifest;
        readonly object _gate = new();
        readonly Dictionary<string, HomeFeed> _feeds = new(StringComparer.OrdinalIgnoreCase);
        // Last logged ignored-entry count per home — the per-solve manifest re-read must never
        // spam the panel, so the count is only reported when it changes (request/watcher paths).
        readonly Dictionary<string, int> _manifestSkipLogged = new(StringComparer.OrdinalIgnoreCase);
        // Per-home per-run tokens, minted lazily. In memory only — the link rotates with the
        // Rhino run and surfaces via the Connect console line and get_app_info, never a file.
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _tokens =
            new(StringComparer.OrdinalIgnoreCase);
        readonly Timer _keepAlive;
        bool _disposed;

        /// <summary>The bound port — set by the host after Start, used for the Origin check.</summary>
        public int Port { get; internal set; }

        public AppSurface(
            Func<string, string?> resolveHomeDir,
            IGrasshopperBridge bridge,
            Action<string, bool>? log = null,
            Func<string, Action, IDisposable>? watchManifest = null)
        {
            _resolveHomeDir = resolveHomeDir ?? throw new ArgumentNullException(nameof(resolveHomeDir));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _log = log;
            // Seam: tests trigger the change callback directly; the default is the real
            // FileSystemWatcher wrapper.
            _watchManifest = watchManifest ?? ManifestWatch.Start;
            _keepAlive = new Timer(_ => SendKeepAlives(), null, KeepAliveInterval, KeepAliveInterval);
        }

        /// <summary>This home's app token for the current run (minted on first use).</summary>
        public string TokenFor(string homeId)
            => _tokens.GetOrAdd(homeId, _ => Guid.NewGuid().ToString("N"));

        /// <summary>The app surface's answer to get_app_info: entry URL (with the token), what
        /// exists in the home, and the manifest's declared counts. Pure reads — no bridge.</summary>
        public AppSurfaceInfo Describe(string homeId)
        {
            if (!IsValidHomeId(homeId) || _resolveHomeDir(homeId) is not { } homeDir)
                return new AppSurfaceInfo(false, "", false, false, 0, 0);
            var load = LoadManifest(homeDir);
            return new AppSurfaceInfo(
                HomeExists: true,
                Url: $"http://127.0.0.1:{Port}/app/{homeId}/?token={TokenFor(homeId)}",
                AppDirExists: Directory.Exists(Path.Combine(homeDir, "app")),
                ManifestPresent: load.Query is not null || load.Error is not null,
                Controls: load.Query?.Controls.Count ?? 0,
                Views: load.Query?.Views.Count ?? 0,
                ManifestError: load.Error,
                ManifestWarnings: load.Query?.Warnings,
                PagePresent: File.Exists(Path.Combine(homeDir, "app", "index.html")));
        }

        public async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                // Host must be literally this server (the DNS-rebinding belt; R2 + the transport
                // spec's MUST). Applies to statics too: a rebound page must not even read the
                // app's HTML.
                if (!LoopbackGuard.IsAllowedHost(ctx.Request.UserHostName, Port))
                {
                    WriteError(ctx.Response, 403, BadRequestCode, "foreign host refused");
                    return;
                }

                var path = ctx.Request.Url?.AbsolutePath ?? "";
                if (!path.StartsWith("/app/", StringComparison.OrdinalIgnoreCase))
                {
                    WriteError(ctx.Response, 404, BadRequestCode, "unknown app path");
                    return;
                }

                var rest = path.Substring("/app/".Length);
                var slash = rest.IndexOf('/');
                var homeId = slash < 0 ? rest : rest.Substring(0, slash);
                var relative = slash < 0 ? "" : rest.Substring(slash + 1);
                if (!IsValidHomeId(homeId) || _resolveHomeDir(homeId) is not { } homeDir)
                {
                    WriteError(ctx.Response, 404, NotFoundCode, $"no home '{homeId}' on this server");
                    return;
                }

                if (relative == "api" || relative.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                {
                    var action = relative == "api" ? "" : relative.Substring("api/".Length);
                    await HandleApiAsync(ctx, homeId, homeDir, action).ConfigureAwait(false);
                }
                else
                {
                    HandleStatic(ctx, homeId, homeDir, relative);
                }
            }
            catch (Exception ex)
            {
                try { WriteError(ctx.Response, 500, BadRequestCode, CleanMessage(ex)); }
                catch { /* response already gone */ }
            }
        }

        // --- API ---------------------------------------------------------------------------

        async Task HandleApiAsync(HttpListenerContext ctx, string homeId, string homeDir, string action)
        {
            var response = ctx.Response;
            if (!CheckOrigin(ctx.Request))
            {
                WriteError(response, 403, BadRequestCode, "cross-origin request refused");
                return;
            }
            if (!Authorized(ctx.Request, homeId))
            {
                WriteError(response, 401, UnauthorizedCode, "missing or wrong app token");
                return;
            }

            // Session routing: the app path is a session too. Every bridge call on this request —
            // state reads, pushes, AND the solution subscribe — binds to the home the URL names,
            // never the front tab (the checkpoint's F-1). AsyncLocal, so it flows into the
            // marshalling seam's per-call snapshot exactly like the MCP header path. Path-first:
            // the app belongs to the FILE and needs no Connect — an opened definition resolves
            // for its page on its own (round-9 S9.47), and after a Save As the page follows the
            // path while the terminal follows the instance (S9.13).
            WireifySessionContext.CurrentHomeId = homeId;
            WireifySessionContext.PathFirst = true;

            var load = LoadManifestReporting(homeId, homeDir);
            if (load.Query is not { } query)
            {
                // Absent and unparseable are different repairs: the code and message say which.
                if (load.Error is { } parseError)
                    WriteError(response, 404, BadManifestCode, parseError);
                else
                    WriteError(response, 404, NoManifestCode,
                        "no app/manifest.json in this home — the app declares its params there");
                return;
            }

            switch (action.TrimEnd('/').ToLowerInvariant())
            {
                case "state" when ctx.Request.HttpMethod == "GET":
                    RunBridge(response, homeDir, () => WriteJson(response, 200, StateEnvelope(ApplyControlText(_bridge.ReadAppState(query), query))));
                    break;

                case "values" when ctx.Request.HttpMethod == "POST":
                    await HandleValuesAsync(ctx, homeDir, query).ConfigureAwait(false);
                    break;

                case "events" when ctx.Request.HttpMethod == "GET":
                    HandleEvents(ctx, homeId, homeDir, query);
                    break;

                case "geometry" when ctx.Request.HttpMethod == "GET":
                    HandleGeometry(ctx, homeDir, query);
                    break;

                default:
                    WriteError(response, 404, BadRequestCode,
                        $"unknown app API action '{action}' — expected state, values, events, or geometry");
                    break;
            }
        }

        /// <summary>Serve one geometry-marked view's meshes on demand. Pull, not push: SSE
        /// frames stay light during drags, meshing cost is paid per fetch off the solve path,
        /// and the page decides its own refresh cadence. The manifest stays the whole authority
        /// — only a view declared with <c>"geometry": true</c> answers here.</summary>
        void HandleGeometry(HttpListenerContext ctx, string homeDir, AppQuery query)
        {
            var raw = ctx.Request.QueryString["id"];
            if (!Guid.TryParse(raw, out var id))
            {
                WriteError(ctx.Response, 400, BadRequestCode,
                    "geometry needs ?id=<declared view guid> (optionally &param=<output name>)");
                return;
            }
            var param = ctx.Request.QueryString["param"];
            // Stepwise resolution so the refusal names the ACTUAL miss: one folded predicate
            // answered a wrong param name with "not declared with geometry:true", sending the
            // caller to edit a manifest that was already correct (round-6 S6.3d).
            var withId = query.Views.Where(v => v.Id == id).ToList();
            if (withId.Count == 0)
            {
                WriteError(ctx.Response, 403, UndeclaredCode,
                    $"no view with id {id} is declared in app/manifest.json — "
                    + "the viewport only renders declared views");
                return;
            }
            var declared = string.IsNullOrEmpty(param)
                ? withId.FirstOrDefault(v => v.Geometry) ?? withId[0]
                : withId.FirstOrDefault(v => MatchesViewParam(v, param!));
            if (declared is null)
            {
                var names = string.Join(", ", withId.Select(v => v.Param ?? "(default output)"));
                WriteError(ctx.Response, 403, UndeclaredCode,
                    $"no view named '{param}' is declared on {id} in app/manifest.json — declared: {names}");
                return;
            }
            if (!declared.Geometry)
            {
                WriteError(ctx.Response, 403, UndeclaredCode,
                    $"view '{declared.Param ?? id.ToString()}' is declared, but not with \"geometry\": true — "
                    + "add that to its entry in app/manifest.json (the viewport only renders views that opted in)");
                return;
            }

            RunBridge(ctx.Response, homeDir, () => WriteJson(ctx.Response, 200, _bridge.ReadAppGeometry(declared)));
        }

        /// <summary>A view answers to its manifest <c>param</c> AND to the qualified label the
        /// server itself mints ("&lt;component nick&gt; &lt;param&gt;") — the state frame hands
        /// a page both names, so both must open the door. The label match is space-anchored
        /// (ends with " " + param), so "member_solids" can never be claimed by "solids".</summary>
        static bool MatchesViewParam(AppViewRef view, string param)
            => !string.IsNullOrEmpty(view.Param)
               && (string.Equals(view.Param, param, StringComparison.OrdinalIgnoreCase)
                   || param.EndsWith(" " + view.Param, StringComparison.OrdinalIgnoreCase));

        async Task HandleValuesAsync(HttpListenerContext ctx, string homeDir, AppQuery query)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            Guid id;
            AppPushValue? push;
            string? gesture;
            try
            {
                using var doc = JsonDocument.Parse(body);
                id = doc.RootElement.GetProperty("id").GetGuid();
                // Optional explicit gesture boundary: "start" at pointer-down (marker-only
                // body), "end" riding the drag's final push — every push in the bracket amends
                // ONE undo record however slowly the hand moved. Pages that never send it get
                // the quiet-gap fallback.
                gesture = doc.RootElement.TryGetProperty("gesture", out var g) ? g.GetString() : null;
                if (gesture is not null && gesture != "start" && gesture != "end")
                    throw new FormatException("gesture must be \"start\" or \"end\"");
                if (doc.RootElement.TryGetProperty("value", out var value))
                {
                    push = value.ValueKind switch
                    {
                        JsonValueKind.Number => new AppPushValue(Number: value.GetDouble()),
                        JsonValueKind.String => new AppPushValue(Text: value.GetString()),
                        JsonValueKind.True => new AppPushValue(Flag: true),
                        JsonValueKind.False => new AppPushValue(Flag: false),
                        _ => throw new FormatException("value must be a number, string, or boolean"),
                    };
                }
                else
                {
                    push = null;
                    if (gesture is null)
                        throw new FormatException("a body without a value must carry a gesture marker");
                }
            }
            catch (Exception)
            {
                WriteError(ctx.Response, 400, BadRequestCode,
                    "body must be JSON: {\"id\":\"<param guid>\",\"value\":<number|string|boolean>} — "
                    + "optionally with \"gesture\":\"start\"|\"end\" (a gesture marker may omit the value)");
                return;
            }

            // The manifest is the whole authority: undeclared params refuse BEFORE any bridge
            // call, so the browser surface can never reach beyond what the user staged.
            if (!query.Controls.Contains(id))
            {
                WriteError(ctx.Response, 403, UndeclaredCode,
                    $"param {id} is not declared as a control in app/manifest.json — pushes only touch declared controls");
                return;
            }

            RunBridge(ctx.Response, homeDir, () =>
            {
                if (push is not null)
                {
                    if (gesture == "start") _bridge.SetAppGesture(id, true);
                    var receipt = _bridge.SetAppControlValue(id, push);
                    if (gesture == "end") _bridge.SetAppGesture(id, false);
                    WriteJson(ctx.Response, 200, receipt);
                }
                else
                {
                    // Marker-only bodies answer the SAME envelope as a value push (control +
                    // clamped) — two response shapes on one endpoint threw the obvious client
                    // code, `(await r.json()).control.value` (round-5 S5.1m). The gesture echo
                    // rides along so a page can tell the marker receipt apart when it wants to.
                    var control = _bridge.SetAppGesture(id, gesture == "start");
                    WriteJson(ctx.Response, 200, new { control, clamped = false, gesture });
                }
            });
        }

        void HandleEvents(HttpListenerContext ctx, string homeId, string homeDir, AppQuery query)
        {
            var response = ctx.Response;
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";
            response.KeepAlive = true;
            response.SendChunked = true;

            var client = new SseClient(homeId, response);

            // Initial frame first: the page renders current state immediately, before any solve.
            AppState initial;
            try
            {
                initial = ApplyControlText(_bridge.ReadAppState(query), query);
            }
            catch (Exception ex)
            {
                // Hit on every EventSource auto-reconnect while the definition is closed: the
                // frame carries the code and page copy, never the agent sentence (round-10 S10.3).
                TryWriteFrame(client, "status", StatusFrame(ex, homeDir));
                try { response.Close(); } catch { /* ignore */ }
                return;
            }
            if (!TryWriteFrame(client, "state", JsonSerializer.Serialize(initial, JsonOpts))) return;

            bool needsSubscription;
            bool needsWatcher;
            lock (_gate)
            {
                if (_disposed)
                {
                    try { response.Close(); } catch { /* ignore */ }
                    return;
                }
                if (!_feeds.TryGetValue(homeId, out var feed))
                    _feeds[homeId] = feed = new HomeFeed();
                feed.Clients.Add(client);
                needsSubscription = feed.Subscription is null && !feed.SubscribePending;
                if (needsSubscription) feed.SubscribePending = true;
                needsWatcher = feed.Watcher is null && !feed.WatcherPending;
                if (needsWatcher) feed.WatcherPending = true;
            }

            if (needsWatcher)
            {
                // Manifest edits go live even when nothing solves: the watcher pushes a fresh
                // state frame on save, so an agent (or hand) editing app/manifest.json reshapes
                // every open tab without waiting for a recompute or a reload.
                IDisposable? watcher = null;
                try
                {
                    watcher = _watchManifest(homeDir, () => OnManifestChanged(homeId, homeDir));
                }
                catch { /* watching is best-effort; per-solve re-reads still apply edits */ }

                IDisposable? orphanedWatcher = null;
                lock (_gate)
                {
                    if (_feeds.TryGetValue(homeId, out var feed))
                    {
                        feed.WatcherPending = false;
                        feed.Watcher = watcher;
                    }
                    else
                    {
                        orphanedWatcher = watcher; // every client left while we were starting it
                    }
                }
                orphanedWatcher?.Dispose();
            }

            if (needsSubscription)
            {
                // The subscribe marshals through the serialized gate (it can wait behind a long
                // solve), so it runs OUTSIDE our lock — broadcasts and keep-alives must never
                // stall behind it.
                IDisposable? subscription = null;
                string? failure = null;
                try
                {
                    // The provider re-reads the manifest per solve (the checkpoint's F-2): a
                    // manifest edit is live on the next recompute instead of frozen at
                    // stream-open. It runs on the UI thread inside the solve — a sub-millisecond
                    // read of a tiny file; cache by mtime only if profiling ever says otherwise.
                    // onClosed ends the stream honestly when the definition itself closes.
                    // onSolveStart/onActiveChanged fire ON the UI thread like onSolution;
                    // Broadcast and BroadcastSolving both hand their network writes off, so the
                    // canvas never waits on a socket.
                    subscription = _bridge.SubscribeSolutionEnd(
                        () => LoadManifest(homeDir).Query,
                        state => Broadcast(homeId, state),
                        reason => CloseFeed(homeId, reason),
                        () => BroadcastSolving(homeId),
                        state => Broadcast(homeId, state));
                }
                catch (Exception ex)
                {
                    failure = CleanMessage(ex);
                }

                IDisposable? orphaned = null;
                lock (_gate)
                {
                    if (_feeds.TryGetValue(homeId, out var feed))
                    {
                        feed.SubscribePending = false;
                        feed.Subscription = subscription;
                    }
                    else
                    {
                        orphaned = subscription; // every client left while we were subscribing
                    }
                }
                orphaned?.Dispose();
                if (failure is not null)
                {
                    // The stream stays useful (the page can poll state); it just will not receive
                    // live pushes. Say so honestly instead of dying silent.
                    TryWriteFrame(client, "status", StatusJson("live updates unavailable: " + failure));
                }
            }
            // The connection stays open; broadcasts and keep-alives write from their own threads.
        }

        /// <summary>Latest-wins fan-out: the newest state replaces any pending one, and a single
        /// flusher drains per home — a burst of solves collapses to the freshest frame, and the
        /// UI-thread caller returns immediately (serialization + network happen off it).</summary>
        void Broadcast(string homeId, AppState state)
        {
            string json;
            try { json = JsonSerializer.Serialize(state, JsonOpts); }
            catch { return; }

            lock (_gate)
            {
                if (_disposed || !_feeds.TryGetValue(homeId, out var feed) || feed.Clients.Count == 0) return;
                feed.PendingJson = json;
                if (feed.Flushing) return;
                feed.Flushing = true;
            }
            Task.Run(() => FlushLoop(homeId));
        }

        void FlushLoop(string homeId)
        {
            while (true)
            {
                string json;
                SseClient[] clients;
                lock (_gate)
                {
                    if (!_feeds.TryGetValue(homeId, out var feed)) return;
                    if (feed.PendingJson is null)
                    {
                        feed.Flushing = false;
                        return;
                    }
                    json = feed.PendingJson;
                    feed.PendingJson = null;
                    clients = feed.Clients.ToArray();
                }
                foreach (var client in clients)
                    if (!TryWriteFrame(client, "state", json))
                        RemoveClient(client);
            }
        }

        /// <summary>End a home's whole feed with an honest <c>status</c> frame (the definition
        /// closed). May be called from the UI thread (DocumentRemoved) — everything hands off:
        /// frame writes are network I/O and the unsubscribe re-enters the marshalling gate,
        /// neither of which may run on the UI thread.</summary>
        void CloseFeed(string homeId, string reason)
        {
            Task.Run(() =>
            {
                SseClient[] clients;
                IDisposable? subscription;
                IDisposable? watcher;
                lock (_gate)
                {
                    if (!_feeds.TryGetValue(homeId, out var feed)) return;
                    clients = feed.Clients.ToArray();
                    feed.Clients.Clear();
                    subscription = feed.Subscription;
                    feed.Subscription = null;
                    watcher = feed.Watcher;
                    feed.Watcher = null;
                    _feeds.Remove(homeId);
                }
                // The terminal frame carries the closed-definition code so a page switches on
                // it instead of on the sentence's wording (round-10 S10.3).
                var json = StatusJson(reason, ErrorProtocol.DocNotOpenCode);
                foreach (var client in clients)
                {
                    TryWriteFrame(client, "status", json);
                    try { client.Response.Close(); } catch { /* ignore */ }
                }
                try { subscription?.Dispose(); } catch { /* ignore */ }
                try { watcher?.Dispose(); } catch { /* ignore */ }
            });
        }

        /// <summary>The home's <c>app/manifest.json</c> changed on disk. Push a fresh state
        /// frame so the edit reshapes every open tab immediately — a manifest edit must never
        /// wait for the next recompute (nothing may ever solve) or a page reload. Runs on the
        /// watcher's own thread: session context is bound here (the B3 class — a watcher thread
        /// has no ambient session), and the state read is an ordinary marshalled bridge call.</summary>
        void OnManifestChanged(string homeId, string homeDir)
        {
            lock (_gate)
            {
                if (_disposed || !_feeds.TryGetValue(homeId, out var feed) || feed.Clients.Count == 0)
                    return;
            }
            WireifySessionContext.CurrentHomeId = homeId;
            WireifySessionContext.PathFirst = true;
            var load = LoadManifestReporting(homeId, homeDir);
            if (load.Query is not { } query)
            {
                // Live-page copy splits the same way the API does: a parse failure names its
                // line, an absent file says absent — neither ever reads as "is Rhino open?".
                BroadcastStatus(homeId, load.Error
                    ?? "app/manifest.json is missing — declared controls and views return once it exists");
                return;
            }
            AppState state;
            try
            {
                state = ApplyControlText(_bridge.ReadAppState(query), query);
            }
            catch (Exception ex)
            {
                BroadcastStatusJson(homeId, StatusFrame(ex, homeDir));
                return;
            }
            Broadcast(homeId, state);
        }

        /// <summary>Send a <c>status</c> frame to every client of a home without closing the
        /// stream (CloseFeed is the terminal variant). Never called on the UI thread.</summary>
        void BroadcastStatus(string homeId, string message, string? code = null)
            => BroadcastStatusJson(homeId, StatusJson(message, code));

        void BroadcastStatusJson(string homeId, string json)
        {
            SseClient[] clients;
            lock (_gate)
            {
                if (!_feeds.TryGetValue(homeId, out var feed)) return;
                clients = feed.Clients.ToArray();
            }
            foreach (var client in clients)
                if (!TryWriteFrame(client, "status", json))
                    RemoveClient(client);
        }

        // Marked solving:true so the kit can debounce the stale pill (a fast solve should not
        // flicker it); the error text keeps old status-frame consumers honest unmodified.
        static readonly string SolvingJson =
            JsonSerializer.Serialize(new { error = "solving…", solving = true }, JsonOpts);

        /// <summary>SolutionStart just fired for this home's document: tell every client the
        /// state on screen is going stale. Arrives ON the UI thread at the head of a solve —
        /// the writes hand off; the canvas never waits on a socket.</summary>
        void BroadcastSolving(string homeId)
        {
            Task.Run(() =>
            {
                SseClient[] clients;
                lock (_gate)
                {
                    if (_disposed || !_feeds.TryGetValue(homeId, out var feed)) return;
                    clients = feed.Clients.ToArray();
                }
                foreach (var client in clients)
                    if (!TryWriteFrame(client, "status", SolvingJson))
                        RemoveClient(client);
            });
        }

        void SendKeepAlives()
        {
            SseClient[] clients;
            lock (_gate) clients = _feeds.Values.SelectMany(f => f.Clients).ToArray();
            foreach (var client in clients)
                if (!TryWriteRaw(client, ": ka\n\n"))
                    RemoveClient(client);
        }

        bool TryWriteFrame(SseClient client, string eventName, string dataJson)
            => TryWriteRaw(client, $"event: {eventName}\ndata: {dataJson}\n\n");

        bool TryWriteRaw(SseClient client, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                lock (client.WriteGate)
                {
                    client.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    client.Response.OutputStream.Flush();
                }
                return true;
            }
            catch
            {
                return false; // client gone — caller removes it
            }
        }

        void RemoveClient(SseClient client)
        {
            IDisposable? subscription = null;
            IDisposable? watcher = null;
            lock (_gate)
            {
                if (_feeds.TryGetValue(client.HomeId, out var feed) && feed.Clients.Remove(client)
                    && feed.Clients.Count == 0)
                {
                    subscription = feed.Subscription;
                    feed.Subscription = null;
                    watcher = feed.Watcher;
                    feed.Watcher = null;
                    _feeds.Remove(client.HomeId);
                }
            }
            try { client.Response.Close(); } catch { /* ignore */ }
            // Unsubscribing marshals onto the UI thread — never inside our lock.
            try { subscription?.Dispose(); } catch { /* ignore */ }
            try { watcher?.Dispose(); } catch { /* ignore */ }
        }

        // --- Statics -----------------------------------------------------------------------

        void HandleStatic(HttpListenerContext ctx, string homeId, string homeDir, string relative)
        {
            var response = ctx.Response;
            if (!Authorized(ctx.Request, homeId))
            {
                WriteUnauthorizedPage(response);
                return;
            }
            // A valid ?token= entry grants the session cookie, so the page can drop the token
            // from its URL (history.replaceState) and the stream/API authenticate cookie-first.
            GrantCookie(ctx.Request, response, homeId);

            var file = ResolveStaticPath(Path.Combine(homeDir, "app"), relative);
            if (file is null || !File.Exists(file))
            {
                // A person clicking the Connect-printed link on a not-yet-scaffolded home is a
                // navigation, not an API caller — a raw JSON blob was the one unfriendly answer
                // left on this surface (round-5 S5.0d). Page-shaped targets get the way in;
                // asset fetches keep JSON (a module loader wants a status, not prose).
                var wantsPage = string.IsNullOrEmpty(relative)
                    || relative.EndsWith("/", StringComparison.Ordinal)
                    || relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
                if (wantsPage) WriteNoPageYetPage(response);
                else WriteError(response, 404, NotFoundCode, "not found");
                return;
            }
            if (!ContentTypes.TryGetValue(Path.GetExtension(file), out var contentType))
            {
                WriteError(response, 404, NotFoundCode, "file type not served");
                return;
            }

            var bytes = File.ReadAllBytes(file);
            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        /// <summary>Resolve a requested relative path inside the home's <c>app/</c> dir, or null
        /// when it escapes it. Pure; unit-tested directly — the traversal guard must not depend on
        /// what a client or an HTTP stack normalizes.</summary>
        public static string? ResolveStaticPath(string appDir, string relative)
        {
            if (string.IsNullOrEmpty(relative)) relative = "index.html";
            if (relative.EndsWith("/", StringComparison.Ordinal)) relative += "index.html";
            if (relative.Contains('\\')) return null;

            string appFull;
            string fileFull;
            try
            {
                appFull = Path.GetFullPath(appDir);
                fileFull = Path.GetFullPath(Path.Combine(appFull, relative));
            }
            catch
            {
                return null;
            }

            var root = appFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fileFull.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fileFull : null;
        }

        // --- Request validation --------------------------------------------------------------

        const string CookieName = "wireify_app";

        bool Authorized(HttpListenerRequest request, string homeId)
        {
            var presented = request.Headers["X-Wireify-App-Token"]
                ?? request.QueryString["token"]
                ?? request.Cookies[CookieName]?.Value;
            return presented is not null && FixedTimeEquals(presented, TokenFor(homeId));
        }

        /// <summary>Mint the session cookie when (and only when) this request proved itself with
        /// the explicit <c>?token=</c> entry link. HttpOnly (page code never needs the value),
        /// SameSite=Strict (a cross-site navigation or fetch never carries it), and path-scoped
        /// to this one home — another home, or another local server the user browses on a
        /// different port, never sees it on its own paths.</summary>
        void GrantCookie(HttpListenerRequest request, HttpListenerResponse response, string homeId)
        {
            if (request.QueryString["token"] is null) return;
            response.Headers.Add("Set-Cookie",
                $"{CookieName}={TokenFor(homeId)}; Path=/app/{homeId}/; HttpOnly; SameSite=Strict");
        }

        // Fixed-time comparison (CryptographicOperations.FixedTimeEquals is unavailable on the
        // net48 leg). The length check short-circuits, which leaks only the length — every real
        // token is 32 hex chars.
        static bool FixedTimeEquals(string presented, string expected)
        {
            if (presented.Length != expected.Length) return false;
            var diff = 0;
            for (var i = 0; i < expected.Length; i++) diff |= presented[i] ^ expected[i];
            return diff == 0;
        }

        bool CheckOrigin(HttpListenerRequest request)
            // Same-origin requests may omit Origin entirely; when a browser DOES send one, it
            // must be exactly this server. That refuses cross-site XHR/SSE from any page that is
            // not the served app itself.
            => LoopbackGuard.IsAllowedOrigin(request.Headers["Origin"], Port);

        public static bool IsValidHomeId(string homeId)
        {
            if (string.IsNullOrEmpty(homeId) || homeId.Length > 128) return false;
            foreach (var c in homeId)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-')
                    return false;
            return true;
        }

        /// <summary>One manifest read, with the failure mode kept distinguishable: <c>Query</c>
        /// null + <c>Error</c> null means the file is absent (NO_MANIFEST); <c>Query</c> null +
        /// <c>Error</c> set means it exists but does not parse (BAD_MANIFEST, the parser's line
        /// in the message). A parsed manifest carries its dropped-entry warnings on the query
        /// itself, so every state frame relays them (round-6 S6.11j).</summary>
        internal sealed record ManifestLoad(AppQuery? Query, string? Error);

        /// <summary>Load the home's declared params. The API refuses rather than guessing when
        /// the manifest is missing or unreadable.</summary>
        static ManifestLoad LoadManifest(string homeDir)
        {
            var path = Path.Combine(homeDir, "app", "manifest.json");
            try
            {
                if (!File.Exists(path)) return new ManifestLoad(null, null);
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var warnings = new List<string>();
                var controls = ReadControls(doc.RootElement, warnings, out var text);
                var views = ReadViewRefs(doc.RootElement, warnings);
                return new ManifestLoad(new AppQuery(controls, views, CapWarnings(warnings), text), null);
            }
            catch (JsonException ex)
            {
                // The parser's own message carries a "LineNumber: n | ..." tail — redundant next
                // to the line we print, so it is trimmed, not translated.
                var message = ex.Message;
                var tail = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
                if (tail > 0) message = message.Substring(0, tail).TrimEnd();
                var line = ex.LineNumber is { } n ? $" line {n + 1}" : "";
                return new ManifestLoad(null,
                    $"app/manifest.json{line}: {message} — fix the JSON and the app returns on its own");
            }
            catch (Exception ex)
            {
                return new ManifestLoad(null, "app/manifest.json could not be read: " + ex.Message);
            }
        }

        /// <summary>LoadManifest for the request + watcher paths: dropped-entry warnings are
        /// surfaced on the panel log when their count changes — the per-solve stream re-read
        /// stays silent by design, so a standing mistake logs once, not once per solve.</summary>
        ManifestLoad LoadManifestReporting(string homeId, string homeDir)
        {
            var load = LoadManifest(homeDir);
            var skipped = load.Query?.Warnings?.Count ?? 0;
            bool report;
            lock (_gate)
            {
                _manifestSkipLogged.TryGetValue(homeId, out var previous);
                report = skipped > 0 && skipped != previous;
                _manifestSkipLogged[homeId] = skipped;
            }
            if (report && load.Query?.Warnings is { } warnings)
                _log?.Invoke($"app manifest ({homeId}): {warnings[0]}"
                    + (warnings.Count > 1 ? $" (+{warnings.Count - 1} more)" : ""), false);
            return load;
        }

        /// <summary>Warnings are frame payload on every solve — a manifest full of mistakes must
        /// not flood it, so the list caps at 8 with an honest tail.</summary>
        static IReadOnlyList<string>? CapWarnings(List<string> warnings)
            => warnings.Count == 0 ? null
             : warnings.Count <= 8 ? warnings
             : warnings.Take(8)
                 .Append($"+{warnings.Count - 8} more manifest entr{(warnings.Count - 8 == 1 ? "y" : "ies")} could not be used")
                 .ToList();

        // The keys each section understands. Anything else warns: a declaration that does
        // nothing must never look identical to one that works (round-7 finding 9 — a fresh
        // agent invented "label" on a control and got no feedback that it was ignored).
        static readonly string[] ControlKeys = { "id", "label", "help" };
        static readonly string[] ViewKeys = { "id", "param", "geometry", "samples" };

        static IReadOnlyList<Guid> ReadControls(JsonElement root, List<string> warnings,
            out IReadOnlyDictionary<Guid, AppControlText>? text)
        {
            var ids = new List<Guid>();
            var texts = new Dictionary<Guid, AppControlText>();
            if (root.TryGetProperty("controls", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var entry in array.EnumerateArray())
                    if (TryReadId(entry, "controls", warnings, out var guid))
                    {
                        if (ids.Contains(guid))
                        {
                            warnings.Add($"controls id \"{guid}\" is declared twice — the second entry is ignored");
                            continue;
                        }
                        WarnUnknownKeys(entry, "controls", guid.ToString(), ControlKeys, warnings);
                        ids.Add(guid);
                        var label = ReadText(entry, "label", guid, warnings);
                        var help = ReadText(entry, "help", guid, warnings);
                        if (label is not null || help is not null) texts[guid] = new AppControlText(label, help);
                    }
            text = texts.Count == 0 ? null : texts;
            return ids;
        }

        /// <summary>A control's page text is a non-empty string or nothing; any other shape warns,
        /// because a label that silently does nothing is the mistake round-7 finding 9 was about.</summary>
        static string? ReadText(JsonElement entry, string key, Guid guid, List<string> warnings)
        {
            if (!entry.TryGetProperty(key, out var value)) return null;
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
            warnings.Add($"controls entry \"{guid}\": \"{key}\" must be a non-empty string — it was ignored");
            return null;
        }

        /// <summary>The manifest's label and help ride each control in the state frame — the page
        /// reads one object per control, and the bridge never sees page text.</summary>
        public static AppState ApplyControlText(AppState state, AppQuery query)
        {
            if (query.ControlText is not { Count: > 0 } text) return state;
            var controls = new List<AppControlState>(state.Controls.Count);
            foreach (var c in state.Controls)
                controls.Add(text.TryGetValue(c.Id, out var t) ? c with { Label = t.Label, Help = t.Help } : c);
            return state with { Controls = controls };
        }

        static IReadOnlyList<AppViewRef> ReadViewRefs(JsonElement root, List<string> warnings)
        {
            var views = new List<AppViewRef>();
            if (root.TryGetProperty("views", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var entry in array.EnumerateArray())
                    if (TryReadId(entry, "views", warnings, out var guid))
                    {
                        var param = entry.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String
                            ? p.GetString()
                            : null;
                        var geometry = entry.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.True;
                        var samples = entry.TryGetProperty("samples", out var s) && s.ValueKind == JsonValueKind.Number
                            && s.TryGetInt32(out var n) && n > 0
                            ? (int?)n
                            : null;
                        var key = string.IsNullOrWhiteSpace(param) ? null : param;
                        if (views.Any(v => v.Id == guid && string.Equals(v.Param, key, StringComparison.OrdinalIgnoreCase)))
                        {
                            warnings.Add($"views entry \"{guid}\"{(key is null ? "" : $" / \"{key}\"")} is declared twice — "
                                + "the second entry is ignored (a duplicate view rides every frame twice)");
                            continue;
                        }
                        WarnUnknownKeys(entry, "views", key is null ? guid.ToString() : $"{guid} / {key}", ViewKeys, warnings);
                        views.Add(new AppViewRef(guid, key, geometry, samples));
                    }
            return views;
        }

        static void WarnUnknownKeys(JsonElement entry, string section, string label, string[] known, List<string> warnings)
        {
            foreach (var prop in entry.EnumerateObject())
            {
                if (Array.IndexOf(known, prop.Name) >= 0) continue;
                warnings.Add(section == "controls"
                    ? $"controls entry \"{label}\": unknown key \"{prop.Name}\" was ignored — controls take \"id\", \"label\", \"help\"; "
                      + "to rename the control itself, nickname it on the canvas (rename_component)"
                    : $"views entry \"{label}\": unknown key \"{prop.Name}\" was ignored — views take \"id\", \"param\", "
                      + "\"geometry\", \"samples\"");
            }
        }

        /// <summary>An entry that cannot be used gets a warning naming the exact mistake — a
        /// declaration dropped in silence reads as a plugin bug to the author who just wrote it,
        /// and the likeliest slip (an output NAME in <c>id</c>) gets its remedy spelled out.</summary>
        static bool TryReadId(JsonElement entry, string section, List<string> warnings, out Guid guid)
        {
            guid = Guid.Empty;
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String)
            {
                warnings.Add($"a {section} entry has no \"id\" — declare the component's instance guid there");
                return false;
            }
            if (!Guid.TryParse(id.GetString(), out guid))
            {
                warnings.Add($"{section} id \"{id.GetString()}\" is not a component id (a GUID) — "
                    + "the guid goes in \"id\"; an output name goes in \"param\"");
                return false;
            }
            return true;
        }

        // --- Responses -----------------------------------------------------------------------

        /// <summary>Run a bridge-backed responder, mapping refusals to honest statuses. A routing
        /// refusal (closed definition, background tab) answers 409 with its own code and PAGE
        /// copy naming the app's own file — the agent's sentence ("this session is connected
        /// to …") reached a browser user verbatim and, after a Save As, named the wrong file
        /// (round-10 S10.7/S10.3). Any other WIREIFY_* message reaches the app verbatim with its
        /// code (round-7 finding 11); busy/queue timeouts say try-again, anything else is a
        /// plain 500.</summary>
        void RunBridge(HttpListenerResponse response, string homeDir, Action respond)
        {
            try
            {
                respond();
            }
            catch (TimeoutException ex)
            {
                WriteError(response, 503, BadRequestCode, ex.Message);
            }
            catch (DocRoutingException ex)
            {
                WriteError(response, 409, ex.Code, PageCopy(ex, homeDir));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                var message = CleanMessage(ex);
                WriteError(response, 409, ErrorProtocol.TryExtractCode(message, out var code) ? code : BadRequestCode, message);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"app surface error: {CleanMessage(ex)}", false);
                WriteError(response, 500, BadRequestCode, CleanMessage(ex));
            }
        }

        /// <summary>Innermost message with the ArgumentException parameter tail stripped — the
        /// " (Parameter 'id')" suffix is a .NET formatting artifact, noise on an app-facing
        /// line. Pure string surgery; the message content is otherwise verbatim.</summary>
        static string CleanMessage(Exception ex)
        {
            var message = ExceptionUnwrap.Innermost(ex).Message;
            var tail = message.LastIndexOf(" (Parameter '", StringComparison.Ordinal);
            return tail > 0 && message.EndsWith("')", StringComparison.Ordinal)
                ? message.Substring(0, tail)
                : message;
        }

        /// <summary>A <c>status</c> frame: the sentence plus, when there is one, the protocol
        /// code a page switches on (omitted otherwise — the kit treats a codeless frame as a
        /// transient note).</summary>
        static string StatusJson(string message, string? code = null)
            => code is null
                ? JsonSerializer.Serialize(new { error = message }, JsonOpts)
                : JsonSerializer.Serialize(new { error = message, code }, JsonOpts);

        /// <summary>The status frame for a failed read: routing refusals carry their code and
        /// page copy, other protocol messages their own code, plain failures no code.</summary>
        static string StatusFrame(Exception ex, string homeDir)
        {
            if (ex is DocRoutingException routing)
                return StatusJson(PageCopy(routing, homeDir), routing.Code);
            var message = CleanMessage(ex);
            return StatusJson(message, ErrorProtocol.TryExtractCode(message, out var code) ? code : null);
        }

        /// <summary>Page-shaped copy for a routing refusal, naming the app's own file: the
        /// document the home's path resolves to, or — when nothing is open at that path — the
        /// file the home's record was written for.</summary>
        static string PageCopy(DocRoutingException ex, string homeDir)
        {
            var name = ex.FileName ?? HomeFileName(homeDir);
            return ex.Decision == DocResolution.NotActive
                ? ErrorProtocol.PageDocNotActive(name ?? "the definition this app belongs to")
                : ErrorProtocol.PageDocNotOpen(name);
        }

        static string? HomeFileName(string homeDir)
        {
            try
            {
                var record = WireifyCore.Connect.HomeIdentity.Read(homeDir);
                return record is null ? null : Path.GetFileName(record.GhPath);
            }
            catch { return null; }
        }

        /// <summary>The friendly answer for a page-shaped request on a home with no page yet:
        /// the Connect console hands out this URL before scaffold_app has run, so the person who
        /// clicks it deserves the way in, not a JSON blob. Embeds nothing, mirrors the
        /// expired-link page's register.</summary>
        static void WriteNoPageYetPage(HttpListenerResponse response)
        {
            const string page = "<!doctype html><html><head><meta charset=\"utf-8\">"
                + "<title>Wireify</title></head>"
                + "<body style=\"font-family:system-ui,sans-serif;max-width:26rem;margin:18vh auto;"
                + "line-height:1.5;padding:0 1rem\">"
                + "<h1 style=\"font-size:1.15rem\">No page here yet</h1>"
                + "<p>This definition's companion app has not been scaffolded. Ask Claude to run "
                + "scaffold_app in this definition's session, then reload this page.</p>"
                + "</body></html>";
            var bytes = Encoding.UTF8.GetBytes(page);
            response.StatusCode = 404;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        /// <summary>The friendly no-credentials answer for statics: a person clicked a stale
        /// link, not a program parsing JSON. Plain, self-contained, and it embeds nothing.</summary>
        static void WriteUnauthorizedPage(HttpListenerResponse response)
        {
            const string page = "<!doctype html><html><head><meta charset=\"utf-8\">"
                + "<title>Wireify</title></head>"
                + "<body style=\"font-family:system-ui,sans-serif;max-width:26rem;margin:18vh auto;"
                + "line-height:1.5;padding:0 1rem\">"
                + "<h1 style=\"font-size:1.15rem\">Open this app from Rhino</h1>"
                + "<p>This address needs this Rhino run's key, and the key rides the link only "
                + "once. Open the definition in Rhino and press <b>Open app</b> on its Wireify "
                + "component, or click the address on its plate to copy the working link (or ask "
                + "Claude to run get_app_info). Links from an earlier Rhino run have expired: the "
                + "key rotates every run.</p></body></html>";
            var bytes = Encoding.UTF8.GetBytes(page);
            response.StatusCode = 401;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        /// <summary>The state GET's response wraps the snapshot with the server's build identity
        /// (<c>wireify</c>) — the served page's one way to prove which build answers it (the
        /// client-side 18A: round 4 ran against a stale page for half a session because nothing
        /// gated freshness). SSE frames stay lean; identity is a fetch-time fact.</summary>
        static System.Text.Json.Nodes.JsonNode StateEnvelope(AppState state)
        {
            var node = JsonSerializer.SerializeToNode(state, JsonOpts) ?? new System.Text.Json.Nodes.JsonObject();
            node["wireify"] = WireifyBuild.Describe();
            return node;
        }

        static void WriteJson(HttpListenerResponse response, int status, object payload)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }

        static void WriteError(HttpListenerResponse response, int status, string code, string message)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { code, error = message }, JsonOpts));
                response.StatusCode = status;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
            }
            catch
            {
                try { response.Abort(); } catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            List<IDisposable> subscriptions = new();
            List<SseClient> clients = new();
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var feed in _feeds.Values)
                {
                    if (feed.Subscription is { } s) subscriptions.Add(s);
                    feed.Subscription = null;
                    if (feed.Watcher is { } w) subscriptions.Add(w);
                    feed.Watcher = null;
                    clients.AddRange(feed.Clients);
                }
                _feeds.Clear();
            }
            _keepAlive.Dispose();
            foreach (var client in clients)
            {
                try { client.Response.Close(); } catch { /* ignore */ }
            }
            foreach (var subscription in subscriptions)
            {
                try { subscription.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>get_app_info's answer: how this session's companion app is reached and what the
    /// home currently holds. <c>Url</c> carries the per-run token — the link the agent hands the
    /// user; it rotates with every Rhino run. <c>ManifestError</c> is the parse failure when the
    /// manifest exists but is not valid JSON (with the parser's line); <c>ManifestWarnings</c>
    /// relays declarations the parse dropped — both null when there is nothing to say, and both
    /// defaulted deliberately (a nullable result property without a default is REQUIRED in the
    /// generated schema while the serializer omits nulls — the round-5 S5.6g failure class).</summary>
    public sealed record AppSurfaceInfo(
        bool HomeExists,
        string Url,
        bool AppDirExists,
        bool ManifestPresent,
        int Controls,
        int Views,
        string? ManifestError = null,
        IReadOnlyList<string>? ManifestWarnings = null,
        bool PagePresent = false);

    /// <summary>scaffold_app's receipt: the surface info PLUS what the call actually DID.
    /// <c>Seeded</c> names the files this call created, <c>Skipped</c> the agent/user files that
    /// already existed and were left alone, <c>KitStamped</c> is always true (kit files are
    /// Wireify-owned and refreshed), <c>Template</c> echoes the applied shape. State-only
    /// receipts made the calling agent report the OPPOSITE of the truth — "already existed, not
    /// overwritten" about a page the call had just seeded (round-5 S5.4f).</summary>
    public sealed record ScaffoldAppResult(
        bool HomeExists,
        string Url,
        bool AppDirExists,
        bool ManifestPresent,
        int Controls,
        int Views,
        IReadOnlyList<string> Seeded,
        IReadOnlyList<string> Skipped,
        bool KitStamped,
        string Template,
        string? ManifestError = null,
        IReadOnlyList<string>? ManifestWarnings = null);
}
