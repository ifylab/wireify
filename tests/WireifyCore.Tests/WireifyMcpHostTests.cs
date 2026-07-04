// SPDX-License-Identifier: Apache-2.0
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using WireifyCore.Mcp;

namespace WireifyCore.Tests;

/// <summary>
/// End-to-end host validation over real loopback HTTP, no Rhino: HttpListener + .Core transport +
/// McpServer + the tool collection + a fake bridge. Covers the whole server pipeline; only in-.gha
/// assembly loading (the Validation Gate's job) is out of reach here.
/// </summary>
public class WireifyMcpHostTests
{
    const string Secret = "test-secret";

    static async Task<(HttpStatusCode Status, string Body)> Post(int port, string secret, string body)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/mcp");
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", secret);
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Initialize_negotiates_and_reports_server_info()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53000);

        var (status, body) = await Post(port, Secret,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("wireify", body);
        Assert.Contains("protocolVersion", body);
    }

    [Fact]
    public async Task Tools_list_advertises_the_loop_tools()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53050);

        var (status, body) = await Post(port, Secret, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("get_document_summary", body);
        Assert.Contains("read_input_data", body);
        Assert.Contains("set_source", body);
    }

    [Fact]
    public async Task Tool_call_runs_through_to_the_bridge()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53100);

        var (status, body) = await Post(port, Secret,
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_runtime_info","arguments":{}}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("cpython3", body); // value the FakeBridge returns
    }

    [Fact]
    public async Task Wrong_secret_is_rejected()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53150);

        var (status, _) = await Post(port, "wrong-secret", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Initialize_advertises_task_capability()
    {
        using var host = new WireifyMcpHost(new WireifyTools(new FakeBridge()), Secret);
        var port = host.Start(53200);

        var (status, body) = await Post(port, Secret,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("tasks", body); // the shared TaskStore makes the server advertise Tasks support
    }
}
