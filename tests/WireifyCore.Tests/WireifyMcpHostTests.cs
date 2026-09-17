// SPDX-License-Identifier: Apache-2.0
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using WireifyCore.Hosting;
using WireifyCore.Mcp;

namespace WireifyCore.Tests;

/// <summary>
/// End-to-end host validation over real loopback HTTP, no Rhino: HttpListener + .Core transport +
/// McpServer + the tool collection + a fake bridge. Covers the whole server pipeline; only in-.gha
/// assembly loading (the Validation Gate's job) is out of reach here. Modern-revision requests
/// send what a real 2026-07-28 client sends — the per-request _meta envelope — because the SDK
/// rejects bare modern requests (-32602); the down-level tests deliberately keep the old shapes.
/// </summary>
public class WireifyMcpHostTests
{
    const string Secret = "test-secret";

    // What a real 2026-07-28 client carries in every request's params (SEP-2575: no handshake —
    // protocol version, capabilities, and identity travel per request).
    const string Meta =
        "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"," +
        "\"io.modelcontextprotocol/clientCapabilities\":{}," +
        "\"io.modelcontextprotocol/clientInfo\":{\"name\":\"t\",\"version\":\"0\"}}";

    static string Modern(int id, string method, string? args = null)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{{{Meta}{(args is null ? "" : "," + args)}}}}}";

    static async Task<(HttpStatusCode Status, string Body)> Post(
        int port, string secret, string body, string protocolHeader = "2026-07-28")
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", secret);
        if (protocolHeader.Length > 0)
            req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolHeader);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Initialize_still_negotiates_for_down_level_clients()
    {
        // The 2026-07-28 revision removed the handshake; older clients still open with it. This
        // deliberately covers the down-level path — users on an older Claude Code keep working.
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53000);

        var (status, body) = await Post(port, Secret,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""",
            protocolHeader: "");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("wireify", body);
        Assert.Contains("protocolVersion", body);
    }

    [Fact]
    public async Task A_badly_shaped_tool_call_is_refused_with_the_field_named()
    {
        // Round-12 S12.11: a wrong parameter name answered a bare "An error occurred invoking
        // 'set_source'." three calls in a row. The server checks the arguments against the
        // tool's own schema first and names the field; nothing runs.
        var bridge = new FakeBridge();
        using var host = new WireifyMcpHost(new WireifyTools(bridge), Secret);
        var port = host.Start(53080);

        var (status, body) = await Post(port, Secret, Modern(3, "tools/call",
            "\"name\":\"set_source\",\"arguments\":{\"id\":\"11111111-1111-1111-1111-111111111111\",\"code\":\"a = 1\"}"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"isError\":true", body);
        // (the JSON writer escapes the quotes around the names)
        Assert.Contains("unknown parameter", body);
        Assert.Contains("did you mean", body);
        Assert.Contains("missing required parameter", body);
        Assert.Contains("Nothing was executed", body);
        Assert.DoesNotContain(bridge.Calls, c => c.StartsWith("SetSource"));

        var (status2, body2) = await Post(port, Secret, Modern(4, "tools/call",
            "\"name\":\"convert_staged\",\"arguments\":{\"id\":\"2\",\"code\":\"a = 1\",\"outputs\":[]}"));
        Assert.Equal(HttpStatusCode.OK, status2);
        Assert.Contains("must be a uuid", body2);
        Assert.Contains("get_document_summary", body2);
        Assert.DoesNotContain(bridge.Calls, c => c.StartsWith("ConvertStaged"));
    }

    [Fact]
    public async Task Tools_list_advertises_the_loop_tools()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53050);

        var (status, body) = await Post(port, Secret, Modern(2, "tools/list"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("get_document_summary", body);
        Assert.Contains("read_input_data", body);
        Assert.Contains("set_source", body);
    }

    [Fact]
    public async Task Modern_results_carry_the_required_envelope_fields()
    {
        // THE B1 regression guard. Revision 2026-07-28 requires resultType on every result
        // envelope; the 0.2.0 pin omitted it and the agent loop died on updated Claude Code
        // ("connected · tools fetch failed"). ttlMs/cacheScope ride the same CacheableResult
        // contract on list results. This test is what would have caught it before a user did.
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53500);

        var (listStatus, listBody) = await Post(port, Secret, Modern(2, "tools/list"));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Contains("\"resultType\":\"complete\"", listBody);
        Assert.Contains("\"ttlMs\"", listBody);
        Assert.Contains("\"cacheScope\"", listBody);

        // tools/call has the same envelope duty — the 0.2.0 pin broke it identically, the
        // incident just never got past tools/list.
        var (callStatus, callBody) = await Post(port, Secret,
            Modern(3, "tools/call", "\"name\":\"get_runtime_info\",\"arguments\":{}"));
        Assert.Equal(HttpStatusCode.OK, callStatus);
        Assert.Contains("\"resultType\":\"complete\"", callBody);
    }

    [Fact]
    public async Task Down_level_results_omit_the_modern_fields()
    {
        // The #1721 mirror: stamping 2026-07-28 envelope fields to an earlier-revision client
        // breaks strict old clients on unrecognised keys — the fields must be version-gated.
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53505);

        var (status, body) = await Post(port, Secret,
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", protocolHeader: "2025-11-25");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("get_document_summary", body);
        Assert.DoesNotContain("resultType", body);
        Assert.DoesNotContain("ttlMs", body);
        Assert.DoesNotContain("cacheScope", body);
    }

    [Fact]
    public async Task Tool_call_runs_through_to_the_bridge()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53100);

        var (status, body) = await Post(port, Secret,
            Modern(3, "tools/call", "\"name\":\"get_runtime_info\",\"arguments\":{}"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("cpython3", body); // value the FakeBridge returns
    }

    [Fact]
    public async Task Wrong_secret_answers_the_stale_session_error()
    {
        // A wrong-but-present secret is almost always a terminal that outlived a Rhino
        // restart (the secret rotates per run). 403 deliberately, not 401 — a 401 sends
        // clients down their OAuth/DCR discovery path, which surfaced round-4's one
        // unreadable error (a raw HTML 404 that named nothing).
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53150);

        var (status, body) = await Post(port, "wrong-secret", Modern(1, "tools/list"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("stale session", body);
        Assert.Contains("Rhino restarted", body);
        Assert.Contains("Connect", body);
    }

    [Fact]
    public async Task Missing_secret_stays_a_bare_401()
    {
        // No secret at all is a probe, not a stale session — nothing readable owed.
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53169);

        var (status, _) = await Post(port, "", Modern(1, "tools/list"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Authenticated_request_carries_the_session_header()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var sessions = new List<string?>();
        host.AuthenticatedRequest += s => { lock (sessions) sessions.Add(s); };
        var port = host.Start(53250);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", Secret);
        req.Headers.TryAddWithoutValidation("X-Wireify-Home", "tower-a1b2c3d4");
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(Modern(9, "tools/list"), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(req)).StatusCode);

        var (status, _) = await Post(port, Secret, Modern(10, "tools/list"));
        Assert.Equal(HttpStatusCode.OK, status);

        lock (sessions)
        {
            Assert.Contains("tower-a1b2c3d4", sessions); // the session-tagged request
            Assert.Contains(null, sessions);             // the headerless (legacy/debug) request
        }
    }

    [Fact]
    public async Task Tool_failure_surfaces_the_named_error_not_the_generic_mask()
    {
        // The whole error contract, end to end over real loopback: a bridge exception must reach
        // the client named — "<tool> failed — <Type>: <message>". The SDK prefixes tool errors
        // with "An error occurred invoking 'x': " even for McpException (its sanctioned shape);
        // what must never appear is the BARE mask, period after the tool name and no detail —
        // that is the shape of a non-McpException escaping Guard.
        var bridge = new FakeBridge { GetSourceThrows = new InvalidOperationException("engine wedged") };
        using var host = new WireifyMcpHost(new WireifyTools(bridge), Secret);
        var port = host.Start(53300);

        var (status, body) = await Post(port, Secret,
            Modern(4, "tools/call", "\"name\":\"get_source\",\"arguments\":{\"id\":\"11111111-1111-1111-1111-111111111111\"}"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("get_source failed", body);
        Assert.Contains("engine wedged", body);
        Assert.DoesNotContain("An error occurred invoking 'get_source'.", body);
    }

    [Fact]
    public async Task Discover_advertises_no_tasks_extension()
    {
        // MCP Tasks left the core protocol in the 2026-07-28 extension redesign and Wireify
        // dropped them for 0.3 (request_timeout_ms + the client's auto-backgrounding cover long
        // solves; keeping Tasks would double the .gha assembly closure). server/discover is the
        // revision's mandatory capability surface — it must not advertise the extension.
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53200);

        var (status, body) = await Post(port, Secret, Modern(1, "server/discover"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("2026-07-28", body);
        Assert.DoesNotContain("io.modelcontextprotocol/tasks", body);
    }

    // --- Transport guards (2026-07-28 streamable-http) -----------------------------------

    [Fact]
    public async Task Get_on_the_mcp_endpoint_is_405()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53510);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/mcp");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_is_refused_even_with_the_right_secret()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53515);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", Secret);
        req.Headers.TryAddWithoutValidation("Origin", "http://evil.test");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(Modern(1, "tools/list"), Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Foreign_host_is_refused_even_with_the_right_secret()
    {
        // The DNS-rebinding shape: the socket is loopback but the Host header carries the
        // attacker's domain. Refusal may come from the listener's own host-matched prefix
        // routing (400/404 — the managed listener answers 404 on macOS, observed) or our
        // explicit guard (403, the belt when a platform's listener is laxer) — either way it
        // must not serve. The guard's own logic is pinned by the pure theories below.
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53520);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
        req.Headers.Host = $"evil.test:{port}";
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", Secret);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(Modern(1, "tools/list"), Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);

        Assert.True(
            resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"foreign Host must be refused, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Down_level_notification_returns_202_with_no_body()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53525);

        var (status, body) = await Post(port, Secret,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""", protocolHeader: "2025-11-25");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("", body);
    }

    [Fact]
    public async Task Unsupported_protocol_version_maps_to_400()
    {
        // SEP-2575 requires protocol-level errors on the status line — the client's era-detection
        // fallback inspects the body only "on 400 Bad Request"; a modern error under a 200 is
        // invisible to it.
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53530);

        var future = Meta.Replace("2026-07-28", "2027-01-01");
        var (status, body) = await Post(port, Secret,
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{{{future}}}}}",
            protocolHeader: "2027-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("32022", body);
    }

    // --- LoopbackGuard (pure) ------------------------------------------------------------

    [Theory]
    [InlineData(null, true)]              // same-origin requests may omit Origin
    [InlineData("", true)]
    [InlineData("http://127.0.0.1:9473", true)]
    [InlineData("http://localhost:9473", true)]
    [InlineData("HTTP://LOCALHOST:9473", true)]
    [InlineData("http://127.0.0.1:9999", false)] // wrong port
    [InlineData("http://evil.test", false)]
    [InlineData("https://127.0.0.1:9473", false)] // scheme is part of the origin
    [InlineData("null", false)]                   // sandboxed/opaque origin
    public void Origin_guard_allows_only_this_server(string? origin, bool allowed)
        => Assert.Equal(allowed, LoopbackGuard.IsAllowedOrigin(origin, 9473));

    [Theory]
    [InlineData(null, false)]             // Host is required — nothing legitimate omits it
    [InlineData("", false)]
    [InlineData("127.0.0.1:9473", true)]
    [InlineData("localhost:9473", true)]
    [InlineData("LOCALHOST:9473", true)]
    [InlineData("127.0.0.1:9999", false)] // wrong port
    [InlineData("evil.test:9473", false)] // the rebinding shape
    [InlineData("127.0.0.1", false)]      // portless never matches a non-default port
    public void Host_guard_allows_only_this_server(string? host, bool allowed)
        => Assert.Equal(allowed, LoopbackGuard.IsAllowedHost(host, 9473));
}
