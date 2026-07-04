// SPDX-License-Identifier: Apache-2.0
using System;
using System.Net;
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
        // One task store for the host's lifetime, shared across all (stateless) requests — a
        // per-request store would make tasks/get come back empty. Enables long-running run/port
        // to execute as MCP Tasks when the client opts in (else they run synchronously).
        readonly InMemoryMcpTaskStore _taskStore = new();

        HttpListener? _listener;
        CancellationTokenSource? _cts;

        public int Port { get; private set; }
        public string Secret => _secret;
        public bool IsListening => _listener?.IsListening == true;

        /// <summary>Raised on every request that passes the shared-secret check — the first one is
        /// the definitive "Claude connected" signal the panel/socket flip green on.</summary>
        public event Action? AuthenticatedRequest;

        public WireifyMcpHost(WireifyTools tools, string secret)
        {
            if (tools is null) throw new ArgumentNullException(nameof(tools));
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException("secret required", nameof(secret));
            _secret = secret;
            _tools = WireifyToolRegistry.Build(tools);
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
                // Shared-secret header, required on every request (loopback-only besides).
                if (ctx.Request.Headers["X-Wireify-Secret"] != _secret)
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    return;
                }

                try { AuthenticatedRequest?.Invoke(); } catch { /* status listeners never break serving */ }

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
                await using var server = McpServer.Create(transport, new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "wireify", Version = "0.1.0" },
                    ToolCollection = _tools,
                    TaskStore = _taskStore,
                    ScopeRequests = false,
                });

                var runTask = server.RunAsync(reqCts.Token);

                // Streamable-HTTP responses are always SSE-framed, even for a single result.
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.StatusCode = 200;
                var wrote = await transport
                    .HandlePostRequestAsync(message, ctx.Response.OutputStream, reqCts.Token)
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
