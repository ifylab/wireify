// SPDX-License-Identifier: Apache-2.0
using System;
using WireifyContract;
using WireifyCore.Connect;
using WireifyCore.Hosting;

namespace WireifyCore.Tests;

public class SessionRegistryTests
{
    sealed class FakeHandle : ITerminalHandle
    {
        public bool HasExited => false;
        public event Action? Exited { add { } remove { } }
    }

    [Fact]
    public void Two_definitions_hold_independent_sessions()
    {
        var registry = new SessionRegistry();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), new FakeHandle(), launched: true);
        registry.Register("facade-99887766", @"C:\proj\facade.gh", Guid.NewGuid(), new FakeHandle(), launched: true);

        Assert.Equal("tower.gh", registry.MarkAuthenticated("tower-a1b2c3d4"));

        // Tower is Connected; facade is still only launched — its socket must not go green.
        Assert.Equal(WireifyConnectionState.Connected, registry.StateFor(@"C:\proj\tower.gh"));
        Assert.Equal(WireifyConnectionState.TerminalLaunched, registry.StateFor(@"C:\proj\facade.gh"));
        Assert.Equal(WireifyConnectionState.ServerStopped, registry.StateFor(@"C:\proj\unrelated.gh"));
        Assert.Equal(WireifyConnectionState.Connected, registry.MaxState);
    }

    [Fact]
    public void MarkAuthenticated_reports_the_transition_once_and_ignores_unknown_sessions()
    {
        var registry = new SessionRegistry();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), new FakeHandle(), launched: true);

        Assert.Equal("tower.gh", registry.MarkAuthenticated("tower-a1b2c3d4"));
        Assert.Null(registry.MarkAuthenticated("tower-a1b2c3d4")); // already connected — no re-log
        Assert.Null(registry.MarkAuthenticated("never-registered"));
    }

    [Fact]
    public void Terminal_exit_demotes_only_its_own_session()
    {
        var registry = new SessionRegistry();
        var towerTerminal = new FakeHandle();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), towerTerminal, launched: true);
        registry.Register("facade-99887766", @"C:\proj\facade.gh", Guid.NewGuid(), new FakeHandle(), launched: true);
        registry.MarkAuthenticated("tower-a1b2c3d4");
        registry.MarkAuthenticated("facade-99887766");

        Assert.Equal(new SessionRegistry.TerminalExit("tower.gh", Remaining: 0), registry.HandleExit(towerTerminal));

        Assert.Equal(WireifyConnectionState.ServerListening, registry.StateFor(@"C:\proj\tower.gh"));
        Assert.Equal(WireifyConnectionState.Connected, registry.StateFor(@"C:\proj\facade.gh")); // untouched

        // Re-armed: the next terminal's first authenticated request flips tower back to Connected.
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), new FakeHandle(), launched: true);
        Assert.Equal("tower.gh", registry.MarkAuthenticated("tower-a1b2c3d4"));
    }

    [Fact]
    public void A_session_stays_live_until_its_last_terminal_closes()
    {
        // Round-9 S9.7: with two terminals open, closing the NEWER one read Build while the older
        // still answered MCP calls — and the click spawned a third. Any live terminal = live.
        var registry = new SessionRegistry();
        var first = new FakeHandle();
        var second = new FakeHandle();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), first, launched: true);
        registry.MarkAuthenticated("tower-a1b2c3d4");
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), second, launched: true);

        // The second launch does not un-connect a session whose first terminal authenticated.
        Assert.Equal(WireifyConnectionState.Connected, registry.StateFor(@"C:\proj\tower.gh"));

        Assert.Equal(new SessionRegistry.TerminalExit("tower.gh", Remaining: 1), registry.HandleExit(second));
        Assert.Equal(WireifyConnectionState.Connected, registry.StateFor(@"C:\proj\tower.gh")); // first still open

        Assert.Equal(new SessionRegistry.TerminalExit("tower.gh", Remaining: 0), registry.HandleExit(first));
        Assert.Equal(WireifyConnectionState.ServerListening, registry.StateFor(@"C:\proj\tower.gh"));

        Assert.Null(registry.HandleExit(first)); // already forgotten — nothing to demote twice
        Assert.Null(registry.HandleExit(new FakeHandle())); // never tracked
    }

    [Fact]
    public void A_fresh_terminal_after_all_closed_re_arms_the_connected_transition()
    {
        var registry = new SessionRegistry();
        var first = new FakeHandle();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), first, launched: true);
        registry.MarkAuthenticated("tower-a1b2c3d4");
        registry.HandleExit(first);

        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), new FakeHandle(), launched: true);
        Assert.Equal(WireifyConnectionState.TerminalLaunched, registry.StateFor(@"C:\proj\tower.gh"));
        Assert.Equal("tower.gh", registry.MarkAuthenticated("tower-a1b2c3d4"));
    }

    [Fact]
    public void Repath_moves_a_live_session_to_the_saved_as_path()
    {
        // Round-9 S9.13: after a Save As the session is keyed to the document instance while
        // the home is keyed to the path. The session follows the file the user is looking at;
        // the old path honestly reads Build.
        var registry = new SessionRegistry();
        var docId = Guid.NewGuid();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", docId, new FakeHandle(), launched: true);

        Assert.Equal("tower-v2.ghx", registry.Repath(@"C:\proj\tower.gh", @"C:\proj\tower-v2.ghx"));

        Assert.Equal(WireifyConnectionState.TerminalLaunched, registry.StateFor(@"C:\proj\tower-v2.ghx"));
        Assert.Equal(WireifyConnectionState.ServerStopped, registry.StateFor(@"C:\proj\tower.gh"));
        // The binding keeps the instance id (the terminal was launched for it) under the home.
        Assert.Equal(docId, registry.Binding("tower-a1b2c3d4")!.DocumentId);
        Assert.Equal(@"C:\proj\tower-v2.ghx", registry.Binding("tower-a1b2c3d4")!.GhPath);

        // Idempotent and quiet: a second call for the same rename, a first save (no old path),
        // or a path nobody registered all return null.
        Assert.Null(registry.Repath(@"C:\proj\tower.gh", @"C:\proj\tower-v2.ghx"));
        Assert.Null(registry.Repath(null, @"C:\proj\new.gh"));
        Assert.Null(registry.Repath(@"C:\proj\other.gh", @"C:\proj\other-2.gh"));
        Assert.Null(registry.Repath(@"C:\proj\tower-v2.ghx", @"C:\proj\tower-v2.ghx"));
    }

    [Fact]
    public void Binding_carries_the_latest_connect_and_null_for_unknown_ids()
    {
        var registry = new SessionRegistry();
        var docId = Guid.NewGuid();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), null, launched: false);
        registry.Register("tower-a1b2c3d4", @"C:\moved\tower-v2.gh", docId, null, launched: true); // re-Connect after a move

        var binding = registry.Binding("tower-a1b2c3d4");
        Assert.NotNull(binding);
        Assert.Equal(docId, binding!.DocumentId);
        Assert.Equal(@"C:\moved\tower-v2.gh", binding.GhPath);
        Assert.Equal("tower-v2.gh", binding.FileName);
        Assert.Null(registry.Binding("never-registered"));
    }

    [Fact]
    public void StateFor_normalizes_paths_and_handles_null()
    {
        var registry = new SessionRegistry();
        var path = Path.Combine(Path.GetTempPath(), "wf-reg", "tower.gh");
        registry.Register("tower-a1b2c3d4", path, Guid.NewGuid(), null, launched: true);

        var unnormalized = Path.Combine(Path.GetTempPath(), "wf-reg", ".", "tower.gh");
        Assert.Equal(WireifyConnectionState.TerminalLaunched, registry.StateFor(unnormalized));
        Assert.Equal(WireifyConnectionState.ServerStopped, registry.StateFor(null));
        Assert.Equal(WireifyConnectionState.ServerStopped, registry.StateFor(""));
    }
}
