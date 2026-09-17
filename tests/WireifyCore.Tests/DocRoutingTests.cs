// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

/// <summary>The routing table that keeps one session's calls off another definition's canvas —
/// safety-critical, so the whole decision matrix is pinned here without Rhino.</summary>
public class DocRoutingTests
{
    [Fact]
    public void No_session_context_keeps_the_legacy_active_document_behavior()
    {
        Assert.Equal(DocResolution.UseActive,
            DocRouting.Decide(hasContext: false, bindingKnown: false, docOpen: false, docIsActive: false, forMutation: true));
        Assert.Equal(DocResolution.UseActive,
            DocRouting.Decide(hasContext: false, bindingKnown: true, docOpen: true, docIsActive: true, forMutation: false));
    }

    [Fact]
    public void An_unknown_home_refuses_rather_than_guessing_a_document()
    {
        Assert.Equal(DocResolution.UnknownHome,
            DocRouting.Decide(hasContext: true, bindingKnown: false, docOpen: false, docIsActive: false, forMutation: false));
    }

    [Fact]
    public void The_app_surface_picks_the_path_matched_document_only_and_the_mcp_path_the_bound_one()
    {
        // Round-9 S9.47/S9.13: the app belongs to the FILE (an opened definition resolves for its
        // page with no Connect at all); a terminal keeps the instance it was launched for.
        var bound = "instance-the-terminal-was-launched-for";
        var byPath = "document-whose-path-hashes-to-the-home";
        Assert.Equal(byPath, DocRouting.Pick(pathFirst: true, bound, byPath));
        Assert.Equal(bound, DocRouting.Pick(pathFirst: false, bound, byPath));
        // A merely opened file has no binding and still serves its page; a re-opened file
        // serves its terminal through the path match.
        Assert.Equal(byPath, DocRouting.Pick(pathFirst: true, (string?)null, byPath));
        Assert.Equal(byPath, DocRouting.Pick(pathFirst: false, (string?)null, byPath));
        // After a Save As the terminal's instance carries the COPY's path. The app never follows
        // it there — that fallback is how one page rendered two files at once (round-10 S10.7);
        // it reads closed until the original is reopened. The terminal keeps its instance.
        Assert.Null(DocRouting.Pick(pathFirst: true, bound, (string?)null));
        Assert.Equal(bound, DocRouting.Pick(pathFirst: false, bound, (string?)null));
        Assert.Null(DocRouting.Pick<string>(pathFirst: true, null, null));
    }

    [Fact]
    public void A_refusal_names_the_path_matched_file_on_the_app_surface_and_the_bound_file_for_mcp()
    {
        // Round-10 S10.7: the binding had followed a Save As to the copy, and the page was told
        // the copy was "not the active canvas" at the exact moment it was.
        Assert.Equal("HALO.gh", DocRouting.FileNameFor(pathFirst: true, boundName: "HALO-copy.gh", targetName: "HALO.gh"));
        Assert.Null(DocRouting.FileNameFor(pathFirst: true, boundName: "HALO-copy.gh", targetName: null));
        Assert.Equal("HALO-copy.gh", DocRouting.FileNameFor(pathFirst: false, boundName: "HALO-copy.gh", targetName: "HALO.gh"));
        Assert.Equal("HALO.gh", DocRouting.FileNameFor(pathFirst: false, boundName: null, targetName: "HALO.gh"));
    }

    [Fact]
    public void A_live_feed_keeps_switches_or_closes_by_what_the_home_resolves_to()
    {
        // The feed re-resolves its document instead of holding the instance it opened on: the
        // held instance became the renamed copy after a Save As and the stream carried the
        // copy's values while the state GET read the reopened original (round-10 S10.7).
        Assert.Equal(FeedRebind.Keep, DocRouting.DecideFeed(attachedIsResolved: true, resolvedExists: true));
        Assert.Equal(FeedRebind.Switch, DocRouting.DecideFeed(attachedIsResolved: false, resolvedExists: true));
        Assert.Equal(FeedRebind.Close, DocRouting.DecideFeed(attachedIsResolved: false, resolvedExists: false));
    }

    [Fact]
    public void A_bound_document_that_is_not_open_refuses_reads_and_mutations_alike()
    {
        Assert.Equal(DocResolution.NotOpen,
            DocRouting.Decide(hasContext: true, bindingKnown: true, docOpen: false, docIsActive: false, forMutation: false));
        Assert.Equal(DocResolution.NotOpen,
            DocRouting.Decide(hasContext: true, bindingKnown: true, docOpen: false, docIsActive: false, forMutation: true));
    }

    [Fact]
    public void Reads_route_to_the_bound_document_even_in_the_background()
    {
        Assert.Equal(DocResolution.UseBound,
            DocRouting.Decide(hasContext: true, bindingKnown: true, docOpen: true, docIsActive: false, forMutation: false));
    }

    [Fact]
    public void Mutations_require_the_bound_document_to_be_the_active_canvas()
    {
        Assert.Equal(DocResolution.NotActive,
            DocRouting.Decide(hasContext: true, bindingKnown: true, docOpen: true, docIsActive: false, forMutation: true));
        Assert.Equal(DocResolution.UseBound,
            DocRouting.Decide(hasContext: true, bindingKnown: true, docOpen: true, docIsActive: true, forMutation: true));
    }

    [Fact]
    public void App_value_pushes_are_mutations_and_refuse_from_a_background_tab()
    {
        // Round 7 exempted the app's value path for one build and measured the cost: the value
        // landed on the background document but nothing downstream solved until the tab was
        // fronted (Grasshopper solves only the front document), so the page showed a new input
        // beside empty outputs. The table has no exemption: a push is a mutation.
        Assert.Equal(DocResolution.NotActive,
            DocRouting.Decide(hasContext: true, bindingKnown: true, docOpen: true, docIsActive: false, forMutation: true));
        Assert.Equal(DocResolution.NotOpen,
            DocRouting.Decide(hasContext: true, bindingKnown: true, docOpen: false, docIsActive: false, forMutation: true));
        Assert.Equal(DocResolution.UnknownHome,
            DocRouting.Decide(hasContext: true, bindingKnown: false, docOpen: false, docIsActive: false, forMutation: true));
    }
}
