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

        Assert.Equal("tower.gh", registry.HandleExit(towerTerminal));

        Assert.Equal(WireifyConnectionState.ServerListening, registry.StateFor(@"C:\proj\tower.gh"));
        Assert.Equal(WireifyConnectionState.Connected, registry.StateFor(@"C:\proj\facade.gh")); // untouched

        // Re-armed: the next terminal's first authenticated request flips tower back to Connected.
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), new FakeHandle(), launched: true);
        Assert.Equal("tower.gh", registry.MarkAuthenticated("tower-a1b2c3d4"));
    }

    [Fact]
    public void A_superseded_terminal_handle_is_ignored()
    {
        var registry = new SessionRegistry();
        var oldTerminal = new FakeHandle();
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), oldTerminal, launched: true);
        registry.Register("tower-a1b2c3d4", @"C:\proj\tower.gh", Guid.NewGuid(), new FakeHandle(), launched: true);

        Assert.Null(registry.HandleExit(oldTerminal)); // the re-Connect owns the session now
        Assert.Equal(WireifyConnectionState.TerminalLaunched, registry.StateFor(@"C:\proj\tower.gh"));
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
