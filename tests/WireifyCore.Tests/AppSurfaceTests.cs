// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using WireifyCore.Bridge;
using WireifyCore.Hosting;
using WireifyCore.Mcp;

namespace WireifyCore.Tests;

/// <summary>
/// The companion-app surface over real loopback HTTP, no Rhino: statics (traversal-guarded,
/// extension-allowlisted), token + Origin validation, manifest gating, the values push path, and
/// the SSE stream end to end (initial frame, solution pushes via the fake's captured callback,
/// subscription lifecycle). What only Rhino can answer — SolutionEnd behavior, slider set
/// semantics, browser EventSource — is the W-A PC checkpoint.
/// </summary>
public class AppSurfaceTests : IDisposable
{
    const string HomeId = "test-home-aaaa1111";
    static readonly Guid ViewId = new("22222222-2222-2222-2222-222222222222");

    readonly string _root;
    readonly FakeBridge _bridge = new();
    WireifyMcpHost? _host;
    AppSurface? _surface;
    int _port;

    // Tokens are per home and per run now — minted by the surface, read back for requests.
    string Token => _surface!.TokenFor(HomeId);

    public AppSurfaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wireify-app-" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(_root, HomeId, "app");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "index.html"), "<!doctype html><title>spike</title>");
        File.WriteAllText(Path.Combine(appDir, "app.js"), "export {}");
        File.WriteAllText(Path.Combine(appDir, "tool.exe"), "nope");
        WriteManifest();
    }

    void WriteManifest() =>
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\"}]}");

    int Start(
        int startPort,
        Action<string, bool>? log = null,
        Func<string, Action, IDisposable>? watchManifest = null)
    {
        _surface = new AppSurface(
            homeId =>
            {
                var dir = Path.Combine(_root, homeId);
                return Directory.Exists(dir) ? dir : null;
            },
            _bridge,
            log,
            // The real FileSystemWatcher stays out of these tests (it would race manifest
            // rewrites); watcher behavior is exercised through the seam below.
            watchManifest ?? ((_, _) => new WatchHandle()));
        _host = new WireifyMcpHost(
            new WireifyTools(_bridge, appInfo: _surface.Describe), "mcp-secret", _surface);
        _port = _host.Start(startPort);
        return _port;
    }

    sealed class WatchHandle : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    string Url(string pathAndQuery) => $"http://127.0.0.1:{_port}{pathAndQuery}";

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    static HttpClient Client() => new() { Timeout = TimeSpan.FromSeconds(15) };

    // --- Statics -------------------------------------------------------------------------

    [Fact]
    public async Task Static_index_served_at_the_home_root()
    {
        Start(53400);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/?token={Token}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.ToString());
        Assert.Contains("spike", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Static_js_served_with_its_content_type()
    {
        Start(53405);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/app.js?token={Token}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/javascript", resp.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Static_disallowed_extension_is_not_served()
    {
        Start(53410);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/tool.exe?token={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Static_unknown_home_404s()
    {
        Start(53415);
        using var http = Client();
        var resp = await http.GetAsync(Url("/app/no-such-home/"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("a/../../secret.txt")]
    [InlineData("a\\..\\secret.txt")]
    [InlineData("/etc/passwd")]
    public void ResolveStaticPath_refuses_escapes(string relative)
    {
        var appDir = Path.Combine(Path.GetTempPath(), "wireify-guard-test", "app");
        Assert.Null(AppSurface.ResolveStaticPath(appDir, relative));
    }

    [Fact]
    public void ResolveStaticPath_serves_nested_files_inside_the_app_dir()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "wireify-guard-test", "app");
        var resolved = AppSurface.ResolveStaticPath(appDir, "assets/logo.svg");
        Assert.NotNull(resolved);
        Assert.StartsWith(Path.GetFullPath(appDir), resolved);
    }

    [Theory]
    [InlineData("ok-home-1234abcd", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    [InlineData("home id", false)]
    public void Home_id_charset_is_enforced(string homeId, bool valid)
        => Assert.Equal(valid, AppSurface.IsValidHomeId(homeId));

    // --- API validation ------------------------------------------------------------------

    [Fact]
    public async Task Api_without_token_is_401()
    {
        Start(53420);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var wrong = await http.GetAsync(Url($"/app/{HomeId}/api/state?token=wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.DoesNotContain(_bridge.Calls, c => c.StartsWith("ReadAppState"));
    }

    [Fact]
    public async Task Api_cross_origin_is_403()
    {
        Start(53425);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/state?token={Token}"));
        req.Headers.TryAddWithoutValidation("Origin", "http://evil.test");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Api_missing_manifest_is_404_with_the_code()
    {
        Start(53430);
        File.Delete(Path.Combine(_root, HomeId, "app", "manifest.json"));
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains(AppSurface.NoManifestCode, await resp.Content.ReadAsStringAsync());
    }

    // --- State ---------------------------------------------------------------------------

    [Fact]
    public async Task State_returns_the_bridge_snapshot_for_the_declared_params()
    {
        Start(53435);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("height", body);     // control nickname from the canned state
        Assert.Contains("slider", body);
        Assert.Contains("isActiveCanvas", body); // Web-cased JSON
        Assert.Contains(_bridge.Calls, c => c == $"ReadAppState:{FakeBridge.SomeId}:{ViewId}");
    }

    [Fact]
    public async Task State_serializes_the_new_control_kinds()
    {
        // The round-4 kinds ride the same DTO: axes for the MD slider, colour for the swatch,
        // the slider fields for the knob — all Web-cased for the page.
        _bridge.CannedAppState = _bridge.CannedAppState with
        {
            Controls = new List<AppControlState>
            {
                new(FakeBridge.SomeId, "pt", "mdslider", Axes: new List<AppAxisState>
                {
                    new(0.25, 0, 1),
                    new(0.8, 0, 2),
                }),
                new(ViewId, "tint", "colour", Colour: "#3366ff80"),
                new(FakeBridge.SomeId, "dial", "knob", 2.5, 0, 10, 1, Step: 0.1),
            },
        };
        Start(53437);
        using var http = Client();

        var body = await (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}")))
            .Content.ReadAsStringAsync();

        Assert.Contains("\"kind\":\"mdslider\"", body);
        Assert.Contains("\"axes\":[", body);
        Assert.Contains("\"max\":2", body);
        Assert.Contains("\"colour\":\"#3366ff80\"", body);
        Assert.Contains("\"kind\":\"knob\"", body);
    }

    // --- Values push ---------------------------------------------------------------------

    HttpRequestMessage ValuesPost(string url, string json)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("X-Wireify-App-Token", Token);
        return req;
    }

    [Fact]
    public async Task Values_push_reaches_the_bridge_for_a_declared_control()
    {
        Start(53440);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":7.5}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("control", body);  // the receipt is the fresh control state
        Assert.Contains("clamped", body);
        Assert.Contains($"SetAppControlValue:{FakeBridge.SomeId}:7.5", _bridge.Calls);
    }

    [Fact]
    public async Task Values_push_to_an_undeclared_param_is_refused_before_the_bridge()
    {
        Start(53445);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"33333333-3333-3333-3333-333333333333\",\"value\":1}"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains(AppSurface.UndeclaredCode, await resp.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_bridge.Calls, c => c.StartsWith("SetAppControlValue"));
    }

    [Fact]
    public async Task Values_malformed_body_is_400()
    {
        Start(53450);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"), "{\"nope\":true}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.DoesNotContain(_bridge.Calls, c => c.StartsWith("SetAppControlValue"));
    }

    [Fact]
    public async Task Values_bridge_refusal_maps_to_409_with_the_message_verbatim()
    {
        Start(53455);
        _bridge.SetAppControlValueThrows =
            new InvalidOperationException("WIREIFY_DOC_NOT_ACTIVE: bring 'tower.gh' to front");
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":2}"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("WIREIFY_DOC_NOT_ACTIVE: bring", body);
        // The code field carries the message's own protocol code, never the generic one — a
        // client switching on it must not conflate a refused push with a bad token (S7.4f).
        Assert.Contains("\"code\":\"WIREIFY_DOC_NOT_ACTIVE\"", body);
    }

    [Fact]
    public async Task State_refusal_for_a_closed_definition_carries_the_doc_not_open_code()
    {
        Start(53456);
        _bridge.ReadAppStateThrows = new InvalidOperationException(
            ErrorProtocol.DocNotOpen("tower.gh"));
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"WIREIFY_DOC_NOT_OPEN\"", body);
        Assert.Contains("reopen it", body);
    }

    [Fact]
    public async Task Api_without_token_carries_the_unauthorized_code()
    {
        // Round-10 S10.2: a missing credential is neither a shape mistake nor an expired link;
        // a client switching on the code (KIT.md invites it) must not read BAD_REQUEST here.
        Start(53600);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("\"code\":\"WIREIFY_APP_UNAUTHORIZED\"", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_routing_refusal_on_the_app_surface_answers_page_copy_naming_the_apps_file()
    {
        // Round-10 S10.7: the MCP session's sentence reached the browser, and after a Save As
        // it named the copy the session had followed. The surface renders its own copy from
        // the refusal's parts and names the app's own file.
        Start(53601);
        _bridge.SetAppControlValueThrows = new DocRoutingException(
            ErrorProtocol.DocNotActiveCode, DocResolution.NotActive, "HALO.gh",
            ErrorProtocol.DocNotActive("HALO-copy.gh"));
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":2}"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"WIREIFY_DOC_NOT_ACTIVE\"", body);
        // (the JSON writer escapes the quotes around the file name)
        Assert.Contains("HALO.gh", body);
        Assert.Contains("is open but not the front Grasshopper tab", body);
        Assert.DoesNotContain("HALO-copy", body);
        Assert.DoesNotContain("this session", body);
    }

    [Fact]
    public async Task A_closed_definition_on_the_app_surface_names_the_file_from_the_homes_record()
    {
        // No open document names the file, so the home's own record does.
        Start(53602);
        var record = Path.Combine(_root, HomeId, ".wireify");
        Directory.CreateDirectory(record);
        File.WriteAllText(Path.Combine(record, "home.json"), "{\"ghPath\":\"/data/HALO.gh\"}");
        _bridge.ReadAppStateThrows = new DocRoutingException(
            ErrorProtocol.DocNotOpenCode, DocResolution.NotOpen, null, ErrorProtocol.DocNotOpen("HALO-copy.gh"));
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"code\":\"WIREIFY_DOC_NOT_OPEN\"", body);
        Assert.Contains("definition closed", body);
        Assert.Contains("HALO.gh", body);
        Assert.DoesNotContain("HALO-copy", body);
        Assert.DoesNotContain("this session", body);
    }

    [Fact]
    public async Task The_events_initial_frame_failure_carries_the_code_and_page_copy()
    {
        // The browser's EventSource reconnects on its own while the definition is closed, and
        // each reconnect's first frame used to carry the agent's raw sentence into the pill
        // (round-10 S10.3).
        Start(53603);
        _bridge.ReadAppStateThrows = new DocRoutingException(
            ErrorProtocol.DocNotOpenCode, DocResolution.NotOpen, "HALO.gh", ErrorProtocol.DocNotOpen("HALO.gh"));
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var frame = await ReadFrame(reader);
        Assert.Equal("status", frame.Event);
        Assert.Contains("\"code\":\"WIREIFY_DOC_NOT_OPEN\"", frame.Data);
        Assert.Contains("definition closed", frame.Data);
        Assert.DoesNotContain("this session", frame.Data);
    }

    [Fact]
    public async Task A_closed_feed_ends_with_the_closed_definition_code()
    {
        Start(53604);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader);
        for (var i = 0; i < 100 && _bridge.SolutionClosedCallback is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.SolutionClosedCallback);

        _bridge.SolutionClosedCallback!(ErrorProtocol.PageDocNotOpen("HALO.gh"));
        var frame = await ReadFrame(reader);
        Assert.Equal("status", frame.Event);
        Assert.Contains("\"code\":\"WIREIFY_DOC_NOT_OPEN\"", frame.Data);
        Assert.Contains("HALO.gh", frame.Data);
    }

    [Fact]
    public async Task A_refusal_without_a_protocol_code_keeps_the_generic_app_code()
    {
        Start(53457);
        _bridge.SetAppControlValueThrows = new ArgumentException("value must be a number for a slider");
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":\"x\"}"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("\"code\":\"WIREIFY_APP_BAD_REQUEST\"", await resp.Content.ReadAsStringAsync());
    }

    // --- SSE events ----------------------------------------------------------------------

    static async Task<string> ReadLineWithTimeout(StreamReader reader, int ms = 8000)
    {
        var read = reader.ReadLineAsync();
        var done = await Task.WhenAny(read, Task.Delay(ms));
        if (done != read) throw new TimeoutException("SSE read timed out");
        return await read ?? throw new IOException("SSE stream ended");
    }

    static async Task<(string Event, string Data)> ReadFrame(StreamReader reader)
    {
        string? ev = null, data = null;
        while (true)
        {
            var line = await ReadLineWithTimeout(reader);
            if (line.StartsWith(":", StringComparison.Ordinal)) continue; // keep-alive comment
            if (line.StartsWith("event: ", StringComparison.Ordinal)) ev = line.Substring(7);
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line.Substring(6);
            else if (line.Length == 0 && ev is not null) return (ev, data ?? "");
        }
    }

    [Fact]
    public async Task Events_streams_the_initial_state_then_every_solution_push()
    {
        Start(53460);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType?.ToString());

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var initial = await ReadFrame(reader);
        Assert.Equal("state", initial.Event);
        Assert.Contains("height", initial.Data);

        // The subscription registers right after the initial frame; wait for the fake to see it.
        for (var i = 0; i < 100 && _bridge.SolutionCallback is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.SolutionCallback);

        // A canvas solve pushes fresh state — simulate one through the captured callback.
        _bridge.SolutionCallback!(_bridge.CannedAppState with
        {
            Controls = new[] { new WireifyCore.Bridge.AppControlState(FakeBridge.SomeId, "height", "slider", 9, 0, 10, 1) },
        });
        var pushed = await ReadFrame(reader);
        Assert.Equal("state", pushed.Event);
        Assert.Contains("\"value\":9", pushed.Data);
    }

    [Fact]
    public async Task Events_subscription_is_disposed_with_the_surface()
    {
        Start(53465);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader); // initial state — the stream is live
        for (var i = 0; i < 100 && _bridge.SolutionCallback is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.SolutionCallback);

        _host!.Dispose();
        for (var i = 0; i < 100 && _bridge.SolutionUnsubscribed == 0; i++) await Task.Delay(20);
        Assert.Equal(1, _bridge.SolutionUnsubscribed);
    }

    [Fact]
    public async Task Events_subscribe_failure_reports_status_instead_of_dying_silent()
    {
        Start(53470);
        _bridge.SubscribeThrows = new InvalidOperationException("WIREIFY_DOC_NOT_OPEN: 'tower.gh'");
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());

        var initial = await ReadFrame(reader);
        Assert.Equal("state", initial.Event);
        var status = await ReadFrame(reader);
        Assert.Equal("status", status.Event);
        Assert.Contains("live updates unavailable", status.Data);
    }

    // --- Auth v1: cookies, per-home tokens, friendly 401 ---------------------------------

    [Fact]
    public async Task Token_entry_grants_a_cookie_that_authenticates_the_api()
    {
        Start(53350);
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        // Entry with the token: page served, session cookie granted.
        var entry = await http.GetAsync(Url($"/app/{HomeId}/?token={Token}"));
        Assert.Equal(HttpStatusCode.OK, entry.StatusCode);
        Assert.Contains(entry.Headers, h => h.Key == "Set-Cookie"
            && h.Value.Any(v => v.Contains("wireify_app=") && v.Contains("HttpOnly") && v.Contains($"/app/{HomeId}/")));

        // The API now authenticates on the cookie alone — no token in URL or header (the
        // EventSource path: it cannot set headers, and the page drops ?token= via replaceState).
        var state = await http.GetAsync(Url($"/app/{HomeId}/api/state"));
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
    }

    [Fact]
    public async Task Tokenless_entry_grants_no_cookie()
    {
        Start(53355);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/index.html"));
        req.Headers.TryAddWithoutValidation("X-Wireify-App-Token", Token);
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Header auth serves the file but mints nothing — only the explicit ?token= entry does.
        Assert.DoesNotContain(resp.Headers, h => h.Key == "Set-Cookie");
    }

    [Fact]
    public async Task Statics_without_credentials_answer_the_friendly_expired_page()
    {
        Start(53360);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.ToString());
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("expired", body);
        Assert.DoesNotContain(Token, body); // no served page ever embeds a token
    }

    [Fact]
    public async Task Tokens_are_per_home()
    {
        Start(53365);
        const string OtherHome = "other-home-bbbb2222";
        var otherApp = Path.Combine(_root, OtherHome, "app");
        Directory.CreateDirectory(otherApp);
        File.WriteAllText(Path.Combine(otherApp, "manifest.json"), "{\"controls\":[],\"views\":[]}");

        using var http = Client();
        // Home A's token opens home A but never home B.
        var own = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var cross = await http.GetAsync(Url($"/app/{OtherHome}/api/state?token={Token}"));
        Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
        var other = await http.GetAsync(Url($"/app/{OtherHome}/api/state?token={_surface!.TokenFor(OtherHome)}"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // --- Push value shapes ---------------------------------------------------------------

    [Fact]
    public async Task Values_push_carries_string_and_bool_shapes_to_the_bridge()
    {
        Start(53370);
        using var http = Client();

        var text = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":\"hello\"}"));
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        Assert.Equal("hello", _bridge.LastPush?.Text);

        var flag = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":true}"));
        Assert.Equal(HttpStatusCode.OK, flag.StatusCode);
        Assert.True(_bridge.LastPush?.Flag == true);
    }

    [Fact]
    public async Task Values_array_value_is_400()
    {
        Start(53375);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":[1,2]}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.DoesNotContain(_bridge.Calls, c => c.StartsWith("SetAppControlValue"));
    }

    // --- Doc-closed honesty ----------------------------------------------------------------

    [Fact]
    public async Task Events_end_with_a_status_frame_when_the_definition_closes()
    {
        Start(53380);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader); // initial state
        for (var i = 0; i < 100 && _bridge.SolutionClosedCallback is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.SolutionClosedCallback);

        _bridge.SolutionClosedCallback!("definition closed");
        var status = await ReadFrame(reader);
        Assert.Equal("status", status.Event);
        Assert.Contains("definition closed", status.Data);
        for (var i = 0; i < 100 && _bridge.SolutionUnsubscribed == 0; i++) await Task.Delay(20);
        Assert.Equal(1, _bridge.SolutionUnsubscribed);
    }

    // --- get_app_info ----------------------------------------------------------------------

    [Fact]
    public async Task Get_app_info_hands_the_session_its_link()
    {
        Start(53385);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Post, Url("/mcp"));
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", "mcp-secret");
        req.Headers.TryAddWithoutValidation("X-Wireify-Home", HomeId);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"get_app_info","arguments":{}}}""",
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains($"/app/{HomeId}/?token=", body);
        Assert.Contains("manifestPresent", body);
    }

    [Fact]
    public async Task Get_app_info_without_a_session_names_the_gap()
    {
        Start(53390);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Post, Url("/mcp"));
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", "mcp-secret");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"get_app_info","arguments":{}}}""",
            Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("no session home", await resp.Content.ReadAsStringAsync());
    }

    // --- Session routing + live manifest (checkpoint F-1/F-2) ----------------------------

    [Fact]
    public async Task App_requests_bind_the_session_to_the_homes_id()
    {
        // B3 (checkpoint F-1): the app path is a session too — every bridge call on an app
        // request (state read, push, AND the solution subscribe) must carry the home the URL
        // names, never fall to the legacy active-document routing.
        Start(53485);
        using var http = Client();
        Assert.Equal(HttpStatusCode.OK,
            (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"))).StatusCode);
        await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":1}"));

        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader); // initial state read
        for (var i = 0; i < 100 && _bridge.SolutionCallback is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.SolutionCallback);

        // state + push + the stream's initial read + the subscribe — all bound to this home.
        Assert.True(_bridge.AppSessions.Count >= 4, $"expected >=4 bridge calls, saw {_bridge.AppSessions.Count}");
        Assert.All(_bridge.AppSessions, s => Assert.Equal(HomeId, s));
    }

    [Fact]
    public async Task Events_query_provider_rereads_the_manifest_per_solve()
    {
        // B4 (checkpoint F-2): the subscription must not freeze its manifest query at
        // stream-open — an edit is live on the next recompute. The fake captures the provider
        // the surface registered; calling it after an edit must see the new declaration, and an
        // unreadable manifest must yield null (the push is skipped, never a stale list).
        Start(53490);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader); // initial
        for (var i = 0; i < 100 && _bridge.SolutionQueryProvider is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.SolutionQueryProvider);

        var before = _bridge.SolutionQueryProvider!();
        Assert.NotNull(before);
        Assert.Single(before!.Controls);

        var added = new Guid("44444444-4444-4444-4444-444444444444");
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"},{\"id\":\"" + added + "\"}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\"}]}");
        var after = _bridge.SolutionQueryProvider!();
        Assert.NotNull(after);
        Assert.Contains(added, after!.Controls);

        File.Delete(Path.Combine(_root, HomeId, "app", "manifest.json"));
        Assert.Null(_bridge.SolutionQueryProvider!());
    }

    // --- Host guard + error cosmetics ----------------------------------------------------

    [Fact]
    public async Task App_foreign_host_is_refused()
    {
        // The DNS-rebinding shape on the app surface: loopback socket, attacker Host. Refusal
        // may come from the listener's host-matched prefix routing (400/404 — the managed
        // listener answers 404 on macOS, observed) or our guard (403, the belt when a
        // platform's listener is laxer) — either way it must not serve.
        Start(53493);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/?token={Token}"));
        req.Headers.Host = $"evil.test:{_port}";
        var resp = await http.SendAsync(req);
        Assert.True(
            resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"foreign Host must be refused, got {(int)resp.StatusCode}");
    }

    [Fact]
    public async Task Bridge_argument_errors_lose_the_parameter_tail()
    {
        // Checkpoint cosmetic: ArgumentException appends " (Parameter 'id')" — a .NET formatting
        // artifact that read as noise in the app. The message itself stays verbatim.
        Start(53496);
        _bridge.SetAppControlValueThrows =
            new ArgumentException("WIREIFY_NOT_FOUND: no object 3333 on this definition", "id");
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":2}"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("WIREIFY_NOT_FOUND: no object 3333 on this definition", body);
        Assert.DoesNotContain("(Parameter", body);
    }

    // --- Coexistence ---------------------------------------------------------------------

    [Fact]
    public async Task Mcp_endpoint_still_serves_with_the_app_surface_present()
    {
        Start(53475);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Post, Url("/mcp"));
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", "mcp-secret");
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("get_document_summary", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task App_paths_never_require_or_accept_the_mcp_secret()
    {
        Start(53480);
        using var http = Client();
        // The MCP secret is NOT a valid app credential — the surfaces stay separate.
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/state"));
        req.Headers.TryAddWithoutValidation("X-Wireify-Secret", "mcp-secret");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // --- Manifest views: param selectors + honesty on bad entries ------------------------

    [Fact]
    public async Task Manifest_param_selector_reaches_the_bridge_query()
    {
        Start(53485);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\",\"param\":\"floor_lines\"},{\"id\":\"" + ViewId + "\"}]}");

        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.NotNull(_bridge.LastAppQuery);
        Assert.Equal(2, _bridge.LastAppQuery!.Views.Count);
        Assert.Equal("floor_lines", _bridge.LastAppQuery.Views[0].Param);
        Assert.Null(_bridge.LastAppQuery.Views[1].Param); // a bare entry stays selector-less
    }

    [Fact]
    public async Task Malformed_manifest_entries_are_logged_once_and_good_ones_still_flow()
    {
        var logged = new List<string>();
        Start(53490, log: (message, _) => logged.Add(message));
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"},{\"id\":\"not-a-guid\"}]," +
            "\"views\":[{\"name\":\"no id at all\"}]}");

        using var http = Client();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"))).StatusCode);

        // Both bad entries warned, logged once (not once per request), good control intact —
        // and the warnings ride the query itself so state frames can relay them (S6.11j).
        Assert.Single(logged, m => m.Contains("not-a-guid") && m.Contains("+1 more"));
        Assert.Equal(new[] { FakeBridge.SomeId }, _bridge.LastAppQuery!.Controls);
        Assert.Empty(_bridge.LastAppQuery.Views);
        Assert.NotNull(_bridge.LastAppQuery.Warnings);
        Assert.Equal(2, _bridge.LastAppQuery.Warnings!.Count);
        Assert.Contains("an output name goes in \"param\"", _bridge.LastAppQuery.Warnings[0]);
        Assert.Contains("has no \"id\"", _bridge.LastAppQuery.Warnings[1]);
    }

    [Fact]
    public async Task Unknown_manifest_keys_warn_instead_of_vanishing()
    {
        // Round-7 finding 9: a fresh agent wrote {"id", "param", "label"} on a CONTROL and got
        // no feedback that two keys did nothing. A declaration that does nothing must never
        // look identical to one that works. "label" became a manifest key since (the HALO
        // page): it rides the control now instead of warning.
        Start(53491);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\",\"param\":\"Number Slider\",\"label\":\"decimals\"}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\",\"param\":\"lines\",\"samples\":20,\"colour\":\"red\"}]}");

        using var http = Client();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"))).StatusCode);

        var warnings = _bridge.LastAppQuery!.Warnings!;
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("unknown key \"param\"") && w.Contains("controls take \"id\", \"label\", \"help\"") && w.Contains("rename_component"));
        Assert.Contains(warnings, w => w.Contains("unknown key \"colour\"") && w.Contains("views take"));
        Assert.Equal("decimals", _bridge.LastAppQuery.ControlText![FakeBridge.SomeId].Label);
        // The entries themselves still work — warnings never drop a usable declaration.
        Assert.Equal(new[] { FakeBridge.SomeId }, _bridge.LastAppQuery.Controls);
        Assert.Equal(20, _bridge.LastAppQuery.Views.Single().Samples);
    }

    [Fact]
    public async Task Duplicate_manifest_entries_warn_and_the_second_is_dropped()
    {
        Start(53492);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"},{\"id\":\"" + FakeBridge.SomeId + "\"}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\",\"param\":\"lines\"},{\"id\":\"" + ViewId + "\",\"param\":\"LINES\"}," +
            "{\"id\":\"" + ViewId + "\",\"param\":\"count\"}]}");

        using var http = Client();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"))).StatusCode);

        var query = _bridge.LastAppQuery!;
        Assert.Equal(new[] { FakeBridge.SomeId }, query.Controls);
        Assert.Equal(new[] { "lines", "count" }, query.Views.Select(v => v.Param));
        Assert.Equal(2, query.Warnings!.Count);
        Assert.Contains(query.Warnings, w => w.StartsWith("controls id") && w.Contains("declared twice"));
        // The warning names the entry that was dropped (the second, as the author typed it).
        Assert.Contains(query.Warnings, w => w.Contains("\"LINES\"") && w.Contains("declared twice"));
    }

    // --- Manifest watcher: edits go live without a solve ---------------------------------

    [Fact]
    public async Task Manifest_edit_pushes_fresh_state_without_any_solve()
    {
        Action? changed = null;
        Start(53495, watchManifest: (_, callback) => { changed = callback; return new WatchHandle(); });

        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader); // initial state
        for (var i = 0; i < 100 && changed is null; i++) await Task.Delay(20);
        Assert.NotNull(changed); // the watcher was armed with the first client

        _bridge.AppSessions.Clear();
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\",\"param\":\"floor_breps\"}]}");
        await Task.Run(changed!); // the watcher thread fires — no solve anywhere

        var frame = await ReadFrame(reader);
        Assert.Equal("state", frame.Event);
        Assert.Equal("floor_breps", _bridge.LastAppQuery!.Views[0].Param); // the EDITED manifest was read
        Assert.Contains(HomeId, _bridge.AppSessions); // B3: the watcher thread bound the session first
    }

    [Fact]
    public async Task Broken_manifest_edit_reports_status_instead_of_silence()
    {
        Action? changed = null;
        Start(53500, watchManifest: (_, callback) => { changed = callback; return new WatchHandle(); });

        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader);
        for (var i = 0; i < 100 && changed is null; i++) await Task.Delay(20);

        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"), "{ this is not json");
        await Task.Run(changed!);

        var frame = await ReadFrame(reader);
        Assert.Equal("status", frame.Event);
        Assert.Contains("manifest.json", frame.Data);
    }

    [Fact]
    public async Task Watcher_is_disposed_with_the_surface()
    {
        var handle = new WatchHandle();
        Start(53505, watchManifest: (_, _) => handle);

        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader); // stream live — the watcher exists

        _host!.Dispose();
        for (var i = 0; i < 100 && !handle.Disposed; i++) await Task.Delay(20);
        Assert.True(handle.Disposed);
    }

    // --- Gestures (round-4 undo fix) -------------------------------------------------------

    [Fact]
    public async Task Values_gesture_marker_opens_the_bracket_without_a_push()
    {
        Start(53510);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"gesture\":\"start\"}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("start", await resp.Content.ReadAsStringAsync());
        Assert.Contains($"SetAppGesture:{FakeBridge.SomeId}:open", _bridge.Calls);
        Assert.DoesNotContain(_bridge.Calls, c => c.StartsWith("SetAppControlValue"));
    }

    [Fact]
    public async Task Values_final_push_with_gesture_end_applies_then_closes()
    {
        Start(53515);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"value\":4,\"gesture\":\"end\"}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("control", await resp.Content.ReadAsStringAsync()); // still the receipt
        var set = _bridge.Calls.IndexOf($"SetAppControlValue:{FakeBridge.SomeId}:4");
        var close = _bridge.Calls.IndexOf($"SetAppGesture:{FakeBridge.SomeId}:close");
        Assert.True(set >= 0 && close > set, "the push must land before the bracket closes");
    }

    [Fact]
    public async Task Values_gesture_marker_for_an_undeclared_param_is_refused()
    {
        Start(53520);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"33333333-3333-3333-3333-333333333333\",\"gesture\":\"start\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.DoesNotContain(_bridge.Calls, c => c.StartsWith("SetAppGesture"));
    }

    [Fact]
    public async Task Values_unknown_gesture_is_400()
    {
        Start(53525);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"gesture\":\"hold\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Values_gesture_marker_answers_the_same_envelope_as_a_value_push()
    {
        // Round-5 S5.1m: `(await r.json()).control.value` — the obvious client code — threw
        // on markers because they answered a different body shape. One endpoint, one envelope.
        Start(53527);
        using var http = Client();
        var resp = await http.SendAsync(ValuesPost(Url($"/app/{HomeId}/api/values"),
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"gesture\":\"start\"}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"control\":", body);
        Assert.Contains("\"clamped\":false", body);
        Assert.Contains("\"gesture\":\"start\"", body);
    }

    // --- Per-view sample override (round-5 S5.11i) -----------------------------------------

    [Fact]
    public async Task Manifest_samples_override_reaches_the_bridge_query_clamped()
    {
        Start(53528);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[]," +
            "\"views\":[{\"id\":\"" + ViewId + "\",\"samples\":200}," +
            "{\"id\":\"" + FakeBridge.SomeId + "\",\"samples\":-3}]}");
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var views = _bridge.LastAppQuery!.Views;
        Assert.Equal(200, views[0].Samples);
        Assert.Null(views[1].Samples); // non-positive values read as unset, never as zero
    }

    // --- Not-found honesty (round-5 S5.0d) -------------------------------------------------

    [Fact]
    public async Task Pageless_home_navigation_answers_the_friendly_no_page_yet_page()
    {
        // The Connect console hands out this URL before scaffold_app has run — the person
        // clicking it gets the way in, not a raw JSON blob.
        var bareHome = "bare-home-bbbb2222";
        Directory.CreateDirectory(Path.Combine(_root, bareHome));
        Start(53529);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{bareHome}/?token={_surface!.TokenFor(bareHome)}"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.StartsWith("text/html", resp.Content.Headers.ContentType?.ToString());
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("scaffold_app", body);
        Assert.DoesNotContain(_surface!.TokenFor(bareHome), body); // the page embeds no secret
    }

    [Fact]
    public async Task Missing_asset_is_json_with_the_not_found_code()
    {
        // Asset fetches (a module loader) keep JSON, but under the distinct NOT_FOUND code —
        // BAD_REQUEST hid "no such file" behind "malformed request" (S5.0d's code note).
        Start(53531);
        using var http = Client();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/kit/vendor/nope.js?token={Token}"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(AppSurface.NotFoundCode, body);
    }

    // --- State envelope + new state fields -------------------------------------------------

    [Fact]
    public async Task Manifest_label_and_help_ride_the_control_state()
    {
        // A page shows the manifest's human name and hint per control (the HALO page's
        // "ring radius (tensegrity_module_radius)"); the canvas nickname stays on the frame
        // beside them, and a malformed text warns instead of vanishing.
        Start(53499);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\",\"label\":\" ring radius (m) \",\"help\":7}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\"}]}");

        using var http = Client();
        var body = await (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}")))
            .Content.ReadAsStringAsync();

        Assert.Contains("\"label\":\"ring radius (m)\"", body);
        Assert.Contains("\"help\":null", body);
        Assert.Contains("\"nickName\":", body);
        var warnings = _bridge.LastAppQuery!.Warnings!;
        Assert.Single(warnings);
        Assert.Contains("\"help\" must be a non-empty string", warnings[0]);
    }

    [Fact]
    public void Control_text_applies_only_to_declared_controls()
    {
        var text = new Dictionary<Guid, AppControlText> { [FakeBridge.SomeId] = new("sides per ring", "struts per ring") };
        var other = Guid.NewGuid();
        var state = new AppState(true, new List<AppControlState>
        {
            new(FakeBridge.SomeId, "tensegrity_segments", "slider", 6, 3, 10, 0, Step: 1),
            new(other, "mid_scale", "slider", 1, 0.5, 2, 2, Step: 0.01),
        }, new List<InputData>());

        var applied = AppSurface.ApplyControlText(state, new AppQuery(new[] { FakeBridge.SomeId, other }, new List<AppViewRef>(), null, text));

        Assert.Equal("sides per ring", applied.Controls[0].Label);
        Assert.Equal("struts per ring", applied.Controls[0].Help);
        Assert.Equal("tensegrity_segments", applied.Controls[0].NickName);
        Assert.Null(applied.Controls[1].Label);
        Assert.Same(state, AppSurface.ApplyControlText(state, new AppQuery(new[] { other }, new List<AppViewRef>())));
    }

    [Fact]
    public async Task State_envelope_carries_the_build_identity_and_doc_name()
    {
        // The wrapped state GET is the page's one way to prove which build answers it (the
        // client-side 18A); docName feeds headers and report title blocks.
        _bridge.CannedAppState = _bridge.CannedAppState with { DocName = "truss-generator" };
        Start(53530);
        using var http = Client();

        var body = await (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}")))
            .Content.ReadAsStringAsync();

        Assert.Contains("\"wireify\":\"0.3.0 build ", body);
        Assert.Contains("\"docName\":\"truss-generator\"", body);
    }

    [Fact]
    public async Task State_serializes_the_evaluated_expression_value()
    {
        _bridge.CannedAppState = _bridge.CannedAppState with
        {
            Controls = new List<AppControlState>
            {
                new(FakeBridge.SomeId, "len", "slider", 10, 0, 20, 0, Step: 1, Evaluated: 20),
            },
        };
        Start(53535);
        using var http = Client();

        var body = await (await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}")))
            .Content.ReadAsStringAsync();

        Assert.Contains("\"value\":10", body);      // raw — what a push reads back
        Assert.Contains("\"evaluated\":20", body);  // the expression's display truth
    }

    // --- Solving + tab-switch frames (round-4 stale-pill fix) -------------------------------

    [Fact]
    public async Task Solve_start_broadcasts_a_solving_status_frame()
    {
        Start(53540);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader); // initial state

        for (var i = 0; i < 100 && _bridge.SolveStartCallback is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.SolveStartCallback);

        _bridge.SolveStartCallback!();
        var frame = await ReadFrame(reader);
        Assert.Equal("status", frame.Event);
        Assert.Contains("\"solving\":true", frame.Data);
    }

    // --- Geometry endpoint -------------------------------------------------------------------

    void WriteGeometryManifest() =>
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"}]," +
            "\"views\":[{\"id\":\"" + ViewId + "\",\"param\":\"L\",\"geometry\":true}]}");

    [Fact]
    public async Task Geometry_serves_a_declared_geometry_view()
    {
        WriteGeometryManifest();
        Start(53550);
        using var http = Client();

        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/geometry?id={ViewId}&token={Token}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"meshes\":[", body);
        Assert.Contains("\"positions\":[", body);
        Assert.Contains("\"bounds\":[", body);
        Assert.Contains("\"renderedCount\":1", body);
        Assert.Contains($"ReadAppGeometry:{ViewId}/L", _bridge.Calls); // the DECLARED ref, param included
    }

    [Fact]
    public async Task Geometry_refuses_views_that_did_not_opt_in()
    {
        // The default manifest declares the view WITHOUT geometry:true — meshing is real
        // work, and the manifest stays the whole authority.
        Start(53555);
        using var http = Client();

        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/geometry?id={ViewId}&token={Token}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("geometry", await resp.Content.ReadAsStringAsync());
        Assert.DoesNotContain(_bridge.Calls, c => c.StartsWith("ReadAppGeometry"));
    }

    [Fact]
    public async Task Geometry_without_an_id_is_400()
    {
        WriteGeometryManifest();
        Start(53558);
        using var http = Client();

        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/geometry?token={Token}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // Stepwise geometry refusals (S6.3d): each miss names ITSELF — one folded predicate
    // answered a wrong param name with "not declared with geometry:true", sending the caller
    // to edit a manifest that was already correct.

    [Fact]
    public async Task Geometry_unknown_view_id_says_no_view_not_no_flag()
    {
        WriteGeometryManifest();
        Start(53560);
        using var http = Client();

        var other = Guid.NewGuid();
        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/geometry?id={other}&token={Token}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains($"no view with id {other}", body);
        Assert.Contains("the viewport only renders declared views", body);
    }

    [Fact]
    public async Task Geometry_wrong_param_name_lists_the_declared_ones()
    {
        WriteGeometryManifest();
        Start(53562);
        using var http = Client();

        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/geometry?id={ViewId}&param=nope&token={Token}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("no view named", body);
        Assert.Contains("nope", body);
        Assert.Contains("declared: L", body);
    }

    [Fact]
    public async Task Geometry_declared_without_the_flag_names_the_remedy()
    {
        // The default manifest declares the view WITHOUT geometry:true.
        Start(53564);
        using var http = Client();

        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/geometry?id={ViewId}&token={Token}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("is declared, but not with", body);
        Assert.Contains("add that to its entry", body);
    }

    [Fact]
    public async Task Geometry_accepts_the_qualified_label_as_an_alias()
    {
        // The state frame hands a page BOTH names ("L" and "W3 metrics L"); pasting the
        // display label into the geometry URL must open the same door (S6.3d).
        WriteGeometryManifest();
        Start(53566);
        using var http = Client();

        var resp = await http.GetAsync(
            Url($"/app/{HomeId}/api/geometry?id={ViewId}&param=W3%20metrics%20L&token={Token}"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains($"ReadAppGeometry:{ViewId}/L", _bridge.Calls);
    }

    [Fact]
    public async Task Geometry_label_alias_is_space_anchored()
    {
        // "solids" must never be claimed by a label ending in "member_solids".
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[],\"views\":[{\"id\":\"" + ViewId + "\",\"param\":\"solids\",\"geometry\":true}]}");
        Start(53568);
        using var http = Client();

        var resp = await http.GetAsync(
            Url($"/app/{HomeId}/api/geometry?id={ViewId}&param=member_solids&token={Token}"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("no view named", await resp.Content.ReadAsStringAsync());
    }

    // --- Manifest diagnostics (S6.11j / S6.11k API side) ---------------------------------

    [Fact]
    public async Task Broken_manifest_answers_BAD_MANIFEST_with_the_parsers_line()
    {
        Start(53570);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\n  \"controls\": [ { \"id\": \"oops\" ]\n}");
        using var http = Client();

        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(AppSurface.BadManifestCode, body);
        Assert.Contains("app/manifest.json line 2", body);
        Assert.Contains("fix the JSON and the app returns on its own", body);
        Assert.DoesNotContain(AppSurface.NoManifestCode, body); // absent and unparseable split
    }

    [Fact]
    public async Task Manifest_warnings_ride_the_state_envelope()
    {
        Start(53572);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"" + FakeBridge.SomeId + "\"}]," +
            "\"views\":[{\"id\":\"class-names\",\"param\":\"class_names\"}]}");
        using var http = Client();

        var resp = await http.GetAsync(Url($"/app/{HomeId}/api/state?token={Token}"));

        // The declaration that could not be used is NOT silent: the frame carries the warning
        // naming the offending id and the remedy (S6.11j cost a fresh agent a session).
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"warnings\":[", body);
        Assert.Contains("class-names", body);
        Assert.Contains("an output name goes in", body);
    }

    [Fact]
    public void Describe_reports_whether_the_page_exists()
    {
        // The socket's Open app capsule and the Connect line key on the page, not the folder.
        Start(53599);
        Assert.True(_surface!.Describe(HomeId).PagePresent);

        File.Delete(Path.Combine(_root, HomeId, "app", "index.html"));
        var noPage = _surface.Describe(HomeId);
        Assert.False(noPage.PagePresent);
        Assert.True(noPage.AppDirExists);
    }

    [Fact]
    public void Describe_reports_the_parse_error_and_the_warnings()
    {
        Start(53574);
        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"), "{ not json");
        var broken = _surface!.Describe(HomeId);
        Assert.True(broken.ManifestPresent); // the file EXISTS — the error says what is wrong
        Assert.NotNull(broken.ManifestError);
        Assert.Contains("app/manifest.json", broken.ManifestError);

        File.WriteAllText(Path.Combine(_root, HomeId, "app", "manifest.json"),
            "{\"controls\":[{\"id\":\"nope\"}],\"views\":[]}");
        var warned = _surface.Describe(HomeId);
        Assert.Null(warned.ManifestError);
        Assert.NotNull(warned.ManifestWarnings);
        Assert.Contains(warned.ManifestWarnings!, w => w.Contains("nope"));
    }

    [Fact]
    public async Task Tab_switch_broadcasts_a_fresh_state_frame()
    {
        // Without this no frame marks the front-tab change: the pill claimed live while
        // pushes refused with DOC_NOT_ACTIVE (round-4 item 14's named gap).
        Start(53545);
        using var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Get, Url($"/app/{HomeId}/api/events?token={Token}"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        await ReadFrame(reader);

        for (var i = 0; i < 100 && _bridge.ActiveChangedCallback is null; i++) await Task.Delay(20);
        Assert.NotNull(_bridge.ActiveChangedCallback);

        _bridge.ActiveChangedCallback!(_bridge.CannedAppState with { IsActiveCanvas = false });
        var frame = await ReadFrame(reader);
        Assert.Equal("state", frame.Event);
        Assert.Contains("\"isActiveCanvas\":false", frame.Data);
    }
}
