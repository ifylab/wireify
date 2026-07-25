// SPDX-License-Identifier: Apache-2.0
using System;
using System.Linq;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class SummaryBoundingTests
{
    static SummaryCandidate C(string name, string nick = "", bool selected = false, bool wireify = false)
        => new(new ComponentRef(Guid.NewGuid(), name, nick), selected, wireify);

    [Fact]
    public void Under_cap_and_unfiltered_passes_everything_through()
    {
        var candidates = new[] { C("A"), C("B"), C("C") };

        var (components, truncated) = SummaryBounding.Apply(candidates, 300, null);

        Assert.Equal(3, components.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void Non_positive_cap_means_no_cap()
    {
        var candidates = Enumerable.Range(0, 500).Select(i => C($"c{i}")).ToList();

        var (components, truncated) = SummaryBounding.Apply(candidates, 0, null);

        Assert.Equal(500, components.Count);
        Assert.False(truncated);
    }

    [Fact]
    public void Filter_matches_name_and_nickname_case_insensitive()
    {
        var candidates = new[] { C("GH_Panel"), C("Addition", "myPANEL"), C("Curve") };

        var (components, truncated) = SummaryBounding.Apply(candidates, 300, "panel");

        Assert.Equal(2, components.Count);
        Assert.False(truncated);
        Assert.Contains(components, r => r.Name == "GH_Panel");
        Assert.Contains(components, r => r.NickName == "myPANEL");
    }

    [Fact]
    public void Cap_keeps_selected_and_wireify_first_but_presents_document_order()
    {
        var candidates = new[]
        {
            C("plain0"),
            C("socket", "W1", wireify: true),
            C("plain2"),
            C("picked", selected: true),
            C("plain4"),
        };

        var (components, truncated) = SummaryBounding.Apply(candidates, 2, null);

        Assert.True(truncated);
        Assert.Equal(2, components.Count);
        Assert.Equal("socket", components[0].Name); // document order in the output …
        Assert.Equal("picked", components[1].Name); // … but priority decided who survived
    }

    [Fact]
    public void Filter_and_cap_compose()
    {
        var candidates = new[] { C("panel a"), C("panel b"), C("panel c"), C("curve"), C("panel d") };

        var (components, truncated) = SummaryBounding.Apply(candidates, 2, "panel");

        Assert.True(truncated);
        Assert.Equal(2, components.Count);
        Assert.All(components, r => Assert.Contains("panel", r.Name));
    }
}
