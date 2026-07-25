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
    public void An_unknown_session_refuses_rather_than_guessing_a_document()
    {
        Assert.Equal(DocResolution.NoSession,
            DocRouting.Decide(hasContext: true, bindingKnown: false, docOpen: false, docIsActive: false, forMutation: false));
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
}
