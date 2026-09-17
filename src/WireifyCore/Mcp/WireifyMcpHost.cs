// SPDX-License-Identifier: Apache-2.0
using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;            // McpJsonUtilities
using ModelContextProtocol.Protocol;   // JsonRpcMessage, JsonRpcMessageContext, Implementation
using ModelContextProtocol.Server;     // StreamableHttpServerTransport, McpServer, McpServerOptions, ...

namespace WireifyCore.Mcp
{
    /// <summary>
    /// The in-process MCP host: a loopback <see cref="HttpListener"/> feeding the official SDK's
    /// <c>.Core</c> <see cref="StreamableHttpServerTransport"/> (per request, stateless) into an
    /// <see cref="McpServer"/>, gated by a per-session shared secret. This is the WireifyCore lift
    /// of the validated gate pattern: the Validation Gate proves it loads inside the <c>.gha</c>;
    /// this class is the shipped server. The tool collection is built once and shared across
    /// requests; the transport + server are per request (stateless).
    /// </summary>
    public sealed class WireifyMcpHost : IDisposable
    {
        readonly string _secret;
        readonly McpServerPrimitiveCollection<McpServerTool> _tools;

        HttpListener? _listener;
        CancellationTokenSource? _cts;

        public int Port { get; private set; }
        public string Secret => _secret;
        public bool IsListening => _listener?.IsListening == true;

        /// <summary>Raised on every request that passes the shared-secret check, carrying the
        /// caller's <c>X-Wireify-Home</c> session header (null when absent — a legacy/debug
        /// client). The first one per session is the "Claude connected" signal that flips that
        /// definition's socket/panel state green.</summary>
        public event Action<string?>? AuthenticatedRequest;

        // The companion-app surface (statics + state/values/events), served off the same
        // listener under /app/ + /api/ with its own token — never the MCP secret. Optional:
        // hosts without one (tests, headless) serve /mcp/ only.
        readonly WireifyCore.Hosting.AppSurface? _appSurface;

        public WireifyMcpHost(WireifyTools tools, string secret, WireifyCore.Hosting.AppSurface? appSurface = null)
        {
            if (tools is null) throw new ArgumentNullException(nameof(tools));
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException("secret required", nameof(secret));
            _secret = secret;
            _tools = WireifyToolRegistry.Build(tools);
            _appSurface = appSurface;
        }

        /// <summary>Bind a free loopback port (scanning up from <paramref name="startPort"/>) and start
        /// serving. The default, 9473, is "WIRE" on a phone keypad — distinctive on purpose, far from
        /// the common MCP-server defaults, and the resolved port is written into the config at Connect.</summary>
        public int Start(int startPort = 9473, int range = 200)
        {
            if (_listener != null) throw new InvalidOperationException("Host already started.");
            Port = FindFreePort(startPort, range);
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/mcp/");
            if (_appSurface is not null)
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/app/");
                _appSurface.Port = Port;
            }
            _listener.Start();
            _ = AcceptLoopAsync(_listener, _cts.Token);
            return Port;
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;
            try { _appSurface?.Dispose(); } catch { /* ignore */ }
        }

        public void Dispose() => Stop();

        async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; } // listener stopped
                _ = HandleAsync(ctx);
            }
        }

        async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                // The companion-app surface owns /app/ outright (statics + its api underneath) —
                // its own per-home tokens gate it, and it never fires AuthenticatedRequest (a
                // browser is not the agent).
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                if (_appSurface is not null
                    && path.StartsWith("/app/", StringComparison.OrdinalIgnoreCase))
                {
                    await _appSurface.HandleAsync(ctx).ConfigureAwait(false);
                    return;
                }

                // Transport guards (2026-07-28 streamable-http): the MCP endpoint is POST-only,
                // and Origin/Host must be literally this server. The per-run secret stays the
                // primary control; these are the spec's MUSTs and the DNS-rebinding belt.
                if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 405;
                    ctx.Response.Headers["Allow"] = "POST";
                    ctx.Response.Close();
                    return;
                }
                if (!WireifyCore.Hosting.LoopbackGuard.IsAllowedOrigin(ctx.Request.Headers["Origin"], Port)
                    || !WireifyCore.Hosting.LoopbackGuard.IsAllowedHost(ctx.Request.UserHostName, Port))
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }

                // Shared-secret header, required on every request (loopback-only besides).
                var presented = ctx.Request.Headers["X-Wireify-Secret"];
                if (presented != _secret)
                {
                    // A WRONG secret is almost always a stale session: the secret rotates every
                    // Rhino run, so a terminal that outlives a Rhino restart fails here on every
                    // call. Say so readably — the bare 401 sent clients down their OAuth/DCR
                    // discovery path and surfaced as an HTML 404 that named nothing (round-4
                    // finding: the one unreadable error of the round). 403 deliberately, not
                    // 401: 401 is what triggers the client's auth machinery.
                    if (!string.IsNullOrEmpty(presented))
                    {
                        var stale = JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id = (object?)null,
                            error = new
                            {
                                code = -32000,
                                message = "Wireify: stale session — Rhino restarted since this terminal connected, "
                                    + "and the connection secret rotated with it. Close this terminal and open the "
                                    + "definition's session again (Connect in Grasshopper, or a fresh terminal in "
                                    + "the home directory).",
                            },
                        });
                        var staleBytes = Encoding.UTF8.GetBytes(stale);
                        ctx.Response.StatusCode = 403;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = staleBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(staleBytes, 0, staleBytes.Length).ConfigureAwait(false);
                        ctx.Response.Close();
                        return;
                    }
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    return;
                }

                // The caller's session (which home's .mcp.json this client was configured from) —
                // set on the async context BEFORE the server task starts, so it flows into the
                // tool invocation and the bridge routes to that session's document.
                var home = ctx.Request.Headers["X-Wireify-Home"];
                var session = string.IsNullOrEmpty(home) ? null : home;
                WireifyCore.Bridge.WireifySessionContext.CurrentHomeId = session;

                try { AuthenticatedRequest?.Invoke(session); } catch { /* status listeners never break serving */ }

                using var reqCts = new CancellationTokenSource();

                var message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(
                    ctx.Request.InputStream, McpJsonUtilities.DefaultOptions, reqCts.Token).ConfigureAwait(false);
                if (message is null)
                {
                    ctx.Response.StatusCode = 400;
                    ctx.Response.Close();
                    return;
                }

                var protocolVersion = ctx.Request.Headers["MCP-Protocol-Version"];
                if (!string.IsNullOrEmpty(protocolVersion))
                {
                    message.Context ??= new JsonRpcMessageContext();
                    message.Context.ProtocolVersion = protocolVersion;
                }

                // One transport + one server per request (stateless). Stateless must be set
                // explicitly — the raw .Core transport defaults it to false.
                await using var transport = new StreamableHttpServerTransport { Stateless = true };
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "wireify", Version = WireifyBuild.Version },
                    ToolCollection = _tools,
                    ScopeRequests = false,
                };
                // Arguments are checked against the tool's own schema before the SDK binds them:
                // a wrong parameter name or a number where a uuid belongs used to answer a bare
                // "An error occurred invoking '<tool>'." (round-12 S12.11). The refusal names the
                // field and the signature, and nothing runs.
                options.Filters.Request.CallToolFilters.Add(next => async (ctx, ct) =>
                {
                    // Primitive matching runs before this filter: the matched tool carries the
                    // schema its arguments are checked against.
                    var name = ctx.Params?.Name;
                    if (name is not null && ctx.MatchedPrimitive is McpServerTool tool
                        && ToolArgumentValidator.Validate(name, tool.ProtocolTool.InputSchema, ctx.Params!.Arguments) is { } problem)
                    {
                        return new CallToolResult
                        {
                            IsError = true,
                            Content = new System.Collections.Generic.List<ContentBlock> { new TextContentBlock { Text = problem } },
                        };
                    }
                    return await next(ctx, ct).ConfigureAwait(false);
                });
                await using var server = McpServer.Create(transport, options);

                var runTask = server.RunAsync(reqCts.Token);

                // Streamable-HTTP responses are always SSE-framed, even for a single result.
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Response.StatusCode = 200;
                var wrote = await transport
                    .HandlePostRequestAsync(
                        message, ctx.Response.OutputStream,
                        first => { ctx.Response.StatusCode = StatusFor(first); return default; },
                        reqCts.Token)
                    .ConfigureAwait(false);
                if (!wrote)
                {
                    ctx.Response.ContentType = null;
                    ctx.Response.StatusCode = 202; // notification/response — nothing to return
                }

                try { ctx.Response.OutputStream.Flush(); } catch { /* ignore */ }
                ctx.Response.Close();

                reqCts.Cancel();
                try { await runTask.ConfigureAwait(false); } catch { /* expected on cancel */ }
            }
            catch
            {
                try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignore */ }
            }
        }

        // SEP-2575 maps some JSON-RPC errors onto HTTP statuses so the client's era-detection can
        // see them (400 = header/capability/version errors, 404 = method not found). The transport's
        // onResponseStarting callback fires before any response bytes — the only moment the status
        // line is still ours to choose.
        static int StatusFor(JsonRpcMessage? first)
            => first is JsonRpcError error
                ? error.Error.Code switch
                {
                    -32020 or -32021 or -32022 => 400,
                    -32601 => 404,
                    _ => 200,
                }
                : 200;

        static int FindFreePort(int start, int range)
        {
            for (var p = start; p < start + range; p++)
            {
                var probe = new HttpListener();
                try
                {
                    probe.Prefixes.Add($"http://127.0.0.1:{p}/mcp/");
                    probe.Start();
                    probe.Stop();
                    return p;
                }
                catch { /* taken — try next */ }
                finally { try { probe.Close(); } catch { /* ignore */ } }
            }
            return start;
        }
    }
}
