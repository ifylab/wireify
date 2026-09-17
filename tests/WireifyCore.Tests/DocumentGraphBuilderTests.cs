// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

/// <summary>The pure half of get_document_graph (round-10 S10.6, round-12 S12.2/S12.3): every
/// wire touching a kept object exactly once, addressed by node index, floating params included;
/// the payload estimate and the priority-ordered cut.</summary>
public class DocumentGraphBuilderTests
{
    static readonly Guid Slider = new("aaaaaaaa-0000-0000-0000-000000000001");
    static readonly Guid Polygon = new("aaaaaaaa-0000-0000-0000-000000000002");
    static readonly Guid Pipe = new("aaaaaaaa-0000-0000-0000-000000000003");
    static readonly Guid Panel = new("aaaaaaaa-0000-0000-0000-000000000004");
    static readonly Guid Outside = new("aaaaaaaa-0000-0000-0000-00000000ffff");

    static WiredParam P(string key, params WireEndInfo[] ends) => new(key, ends);
    static WireEndInfo End(Guid id, string nick, string param) => new(id, nick, param);

    [Fact]
    public void A_wire_between_two_kept_objects_appears_once_by_index_from_the_recipients_side()
    {
        var nodes = new List<GraphWiring>
        {
            new(Slider, new[] { P("radius") }, new[] { P("radius", End(Polygon, "Polygon", "R")) }),   // 0
            new(Polygon, new[] { P("R", End(Slider, "radius", "radius")) }, new[] { P("P", End(Pipe, "Pipe", "C")) }), // 1
            new(Pipe, new[] { P("C", End(Polygon, "Polygon", "P")) }, new[] { P("S") }),                 // 2
        };

        var edges = DocumentGraphBuilder.Edges(nodes);

        Assert.Equal(2, edges.Count);
        Assert.Contains(new GraphEdge(0, "radius", 1, "R"), edges);
        Assert.Contains(new GraphEdge(1, "P", 2, "C"), edges);
        Assert.All(edges, e => { Assert.Null(e.FromId); Assert.Null(e.ToId); });
    }

    [Fact]
    public void The_truncation_note_offers_params_off_only_while_params_are_on()
    {
        // Round-13 S13.3: with includeParams already false the note still suggested passing it.
        var on = DocumentGraphBuilder.TruncationNote(69, 156, includeParams: true);
        var off = DocumentGraphBuilder.TruncationNote(144, 156, includeParams: false);

        Assert.Contains("69 of 156 nodes kept", on);
        Assert.Contains("includeParams: false", on);
        Assert.Contains("144 of 156 nodes kept", off);
        Assert.DoesNotContain("includeParams", off);
        Assert.Contains("ids or nameFilter", off);
    }

    [Fact]
    public void A_wire_into_a_floating_param_is_an_edge()
    {
        // Round-12 S12.3: a panel or a relay param has no inputs in Grasshopper's terms; its
        // incoming wire is its own param's source. The bridge reads it as the param's input.
        var nodes = new List<GraphWiring>
        {
            new(Pipe, Array.Empty<WiredParam>(), new[] { P("message", End(Panel, "Panel", "Panel")) }),           // 0
            new(Panel, new[] { P("Panel", End(Pipe, "IPy2", "message")) }, new[] { P("Panel") }),              // 1
        };

        var edge = Assert.Single(DocumentGraphBuilder.Edges(nodes));
        Assert.Equal(new GraphEdge(0, "message", 1, "Panel"), edge);
    }

    [Fact]
    public void An_endpoint_outside_the_kept_set_carries_its_guid_and_index_minus_one()
    {
        var nodes = new List<GraphWiring>
        {
            new(Polygon, new[] { P("R", End(Outside, "radius", "radius")) }, new[] { P("P", End(Outside, "Custom Preview", "G"), End(Pipe, "Pipe", "C")) }),
            new(Pipe, new[] { P("C", End(Polygon, "Polygon", "P")) }, Array.Empty<WiredParam>()),
        };

        var edges = DocumentGraphBuilder.Edges(nodes);

        Assert.Equal(3, edges.Count);
        Assert.Contains(new GraphEdge(-1, "radius", 0, "R", FromId: Outside), edges);
        Assert.Contains(new GraphEdge(0, "P", -1, "G", ToId: Outside), edges);
        Assert.Contains(new GraphEdge(0, "P", 1, "C"), edges);
    }

    [Fact]
    public void Duplicate_wires_collapse_and_no_wiring_means_no_edges()
    {
        var doubled = new List<GraphWiring>
        {
            new(Pipe, new[] { P("C", End(Outside, "Polygon", "P"), End(Outside, "Polygon", "P")) }, Array.Empty<WiredParam>()),
        };
        Assert.Single(DocumentGraphBuilder.Edges(doubled));
        Assert.Empty(DocumentGraphBuilder.Edges(new List<GraphWiring> { new(Slider, Array.Empty<WiredParam>(), new[] { P("radius") }) }));
    }

    [Fact]
    public void The_token_estimate_charges_guids_heavily_and_prose_lightly()
    {
        // Round-12 S12.2/S12.4: 54,000 and 76,000 characters of guid-heavy JSON were refused
        // by the client; characters alone are the wrong unit.
        var guid = Guid.NewGuid().ToString();
        var prose = new string('a', 330);
        Assert.Equal(100, DocumentGraphBuilder.EstimateTokens(prose));
        Assert.Equal(22, DocumentGraphBuilder.EstimateTokens(guid));
        Assert.Equal(122, DocumentGraphBuilder.EstimateTokens(guid + prose));
        Assert.Equal(0, DocumentGraphBuilder.EstimateTokens(""));
    }

    [Fact]
    public void The_cut_keeps_everything_that_fits_and_finds_the_boundary_otherwise()
    {
        // The estimate grows with k: 100 tokens per node.
        Func<int, int> estimate = k => 100 * k;
        Assert.Equal(10, DocumentGraphBuilder.FitWithinBudget(10, 1500, estimate));
        Assert.Equal(15, DocumentGraphBuilder.FitWithinBudget(40, 1500, estimate));
        Assert.Equal(0, DocumentGraphBuilder.FitWithinBudget(40, 50, estimate));
        Assert.Equal(0, DocumentGraphBuilder.FitWithinBudget(0, 1500, estimate));
    }
}
