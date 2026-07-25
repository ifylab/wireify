// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.Linq;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class InputDataShaperTests
{
    static ShapedBranch Branch(string path, params (string type, string clr, string val)[] items)
        => new(path, items.Select(i => new ShapedItem(i.type, i.clr, "", i.val)).ToList());

    [Fact]
    public void Tree_stats_reflect_branches_and_items()
    {
        var input = InputDataShaper.Shape("x", "tree", new[]
        {
            Branch("{0;0}", ("Number", "System.Double", "1"), ("Number", "System.Double", "2")),
            Branch("{0;1}", ("Number", "System.Double", "3")),
        }, 5, 50);

        Assert.Equal(2, input.Tree.PathCount);
        Assert.Equal(3, input.Tree.DataCount);
        Assert.False(input.Tree.IsFlat);
    }

    [Fact]
    public void Single_branch_is_flat()
    {
        var input = InputDataShaper.Shape("x", "list", new[] { Branch("{0}", ("Number", "System.Double", "1")) }, 5, 50);
        Assert.True(input.Tree.IsFlat);
    }

    [Fact]
    public void Histogram_groups_by_type_with_counts_and_clr()
    {
        var input = InputDataShaper.Shape("x", "list", new[]
        {
            Branch("{0}",
                ("Number", "System.Double", "1"),
                ("Number", "System.Double", "2"),
                ("Text", "System.String", "hi")),
        }, 10, 50);

        var num = input.Types.Single(t => t.TypeName == "Number");
        var txt = input.Types.Single(t => t.TypeName == "Text");
        Assert.Equal(2, num.Count);
        Assert.Equal("System.Double", num.Clr);
        Assert.Equal(1, txt.Count);
    }

    [Fact]
    public void Samples_cap_per_branch()
    {
        var input = InputDataShaper.Shape("x", "list", new[]
        {
            Branch("{0}", ("N", "C", "1"), ("N", "C", "2"), ("N", "C", "3"), ("N", "C", "4")),
        }, maxPerBranch: 2, maxTotal: 50);

        Assert.Equal(2, input.Samples.Count);
        Assert.Equal("1", input.Samples[0].Value);
        Assert.Equal("2", input.Samples[1].Value);
    }

    [Fact]
    public void Samples_cap_total_but_tree_stats_count_everything()
    {
        var branches = Enumerable.Range(0, 10)
            .Select(i => Branch($"{{{i}}}", ("N", "C", "a"), ("N", "C", "b")))
            .ToList();

        var input = InputDataShaper.Shape("x", "tree", branches, maxPerBranch: 2, maxTotal: 5);

        Assert.Equal(5, input.Samples.Count);   // total cap wins
        Assert.Equal(10, input.Tree.PathCount); // but the histogram + tree see all of it
        Assert.Equal(20, input.Tree.DataCount);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        var input = InputDataShaper.Shape("x", "item", new List<ShapedBranch>(), 5, 50);

        Assert.Equal(0, input.Tree.PathCount);
        Assert.Empty(input.Types);
        Assert.Empty(input.Samples);
        Assert.True(input.Tree.IsFlat);
    }

    [Fact]
    public void Histogram_carries_the_goo_wrapper_name()
    {
        var branch = new ShapedBranch("{0}", new[]
        {
            new ShapedItem("Line", "Rhino.Geometry.Line", "GH_Line", "Line(L:27.00)"),
            new ShapedItem("Text", "System.String", "GH_String", "beam-a"),
        });

        var input = InputDataShaper.Shape("in1", "tree", new[] { branch }, 5, 50);

        Assert.Equal("GH_Line", input.Types.Single(t => t.TypeName == "Line").Goo);
        Assert.Equal("GH_String", input.Types.Single(t => t.TypeName == "Text").Goo);
    }

    [Fact]
    public void Sample_values_are_capped_but_report_the_full_length()
    {
        var big = new string('x', InputDataShaper.MaxSampleValueChars + 5000);
        var input = InputDataShaper.Shape("in1", "item",
            new[] { Branch("{0}", ("Text", "System.String", big)) }, 5, 50);

        var sample = input.Samples.Single();
        Assert.Equal(InputDataShaper.MaxSampleValueChars + 1, sample.Value.Length); // capped text + ellipsis
        Assert.EndsWith("…", sample.Value);
        Assert.Equal(big.Length, sample.ValueLength);
    }

    [Fact]
    public void Small_sample_values_pass_through_uncapped_with_exact_length()
    {
        var input = InputDataShaper.Shape("in1", "item",
            new[] { Branch("{0}", ("Text", "System.String", "hello")) }, 5, 50);

        var sample = input.Samples.Single();
        Assert.Equal("hello", sample.Value);
        Assert.Equal(5, sample.ValueLength);
    }

    [Fact]
    public void Cap_reports_the_truncation_signature_length_exactly()
    {
        // The classic upstream clip: a panel-truncated 32767-char string keeps its telltale length.
        var clipped = new string('j', 32767);
        var (value, fullLength) = InputDataShaper.Cap(clipped, InputDataShaper.MaxSampleValueChars);

        Assert.Equal(32767, fullLength);
        Assert.Equal(InputDataShaper.MaxSampleValueChars + 1, value.Length);
    }

    [Fact]
    public void Cap_handles_null_and_zero_budget()
    {
        Assert.Equal(("", 0), InputDataShaper.Cap(null!, 100));
        var (value, fullLength) = InputDataShaper.Cap("abc", 0);
        Assert.Equal("…", value);
        Assert.Equal(3, fullLength);
    }

    [Fact]
    public void Cap_sanitizes_lone_surrogates_and_stray_controls_for_transport()
    {
        // Canvas data can carry anything; the transported sample must never be able to fail the
        // response's JSON serialization (which runs outside the tool error handling).
        var dirty = "ab" + '\uD800' + "cd" + '\u0001' + "e";
        var (value, fullLength) = InputDataShaper.Cap(dirty, 100);

        Assert.Equal("ab�cd�e", value);
        Assert.Equal(dirty.Length, fullLength); // the reported length is the original, untouched

        var friendly = "a\U0001F600b\nc\td"; // a real astral pair and whitespace controls survive
        Assert.Equal((friendly, friendly.Length), InputDataShaper.Cap(friendly, 100));
    }

    [Fact]
    public void Cap_never_ships_a_surrogate_pair_split_by_the_cut()
    {
        var s = "abc" + char.ConvertFromUtf32(0x1F600); // 5 UTF-16 units; the pair sits at 3..4
        var (value, fullLength) = InputDataShaper.Cap(s, 4); // the cut lands inside the pair

        Assert.Equal(5, fullLength);
        Assert.Equal("abc�…", value);
    }

    [Fact]
    public void A_value_at_the_panel_clip_length_raises_a_warning()
    {
        var clipped = new string('j', WireifyContract.WireifyIds.PanelClipTextLength);
        var input = InputDataShaper.Shape("in1", "item",
            new[] { Branch("{0}", ("Text", "System.String", clipped)) }, 5, 50);

        var warning = Assert.Single(input.Warnings!);
        Assert.Contains("in1", warning);
        Assert.Contains("32767", warning);
        Assert.Contains("file path", warning);
    }

    [Fact]
    public void Ordinary_values_raise_no_warnings()
    {
        var input = InputDataShaper.Shape("in1", "item",
            new[] { Branch("{0}", ("Text", "System.String", "hello")) }, 5, 50);

        Assert.Null(input.Warnings);
    }

    [Fact]
    public void Multiple_clipped_values_report_their_count_in_one_warning()
    {
        var clipped = new string('x', WireifyContract.WireifyIds.PanelClipTextLength);
        var input = InputDataShaper.Shape("in1", "list",
            new[] { Branch("{0}", ("Text", "S", clipped), ("Text", "S", clipped)) }, 5, 50);

        Assert.Contains("2 text values", Assert.Single(input.Warnings!));
    }

    [Fact]
    public void Report_value_cap_override_is_honored()
    {
        var big = new string('x', 4000);

        var sampleCapped = InputDataShaper.Shape("out", "list",
            new[] { Branch("{0}", ("Text", "S", big)) }, 5, 50);
        var reportRoomy = InputDataShaper.Shape("out", "list",
            new[] { Branch("{0}", ("Text", "S", big)) }, 5, 50, InputDataShaper.MaxReportValueChars);

        Assert.True(sampleCapped.Samples[0].Value.Length <= InputDataShaper.MaxSampleValueChars + 1); // + ellipsis
        Assert.Equal(4000, sampleCapped.Samples[0].ValueLength);
        Assert.Equal(big, reportRoomy.Samples[0].Value); // 8k report budget keeps a probe dump whole
    }
}
