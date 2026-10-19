// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.Linq;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class OutputComparerTests
{
    static ComparableItem Num(double v) => new(ComparableKind.Number, "Number", new[] { v });
    static ComparableItem Int(int v) => new(ComparableKind.Integer, "Integer", new[] { (double)v });
    static ComparableItem Txt(string s) => new(ComparableKind.Text, "Text", Text: s);
    static ComparableItem Pt(double x, double y, double z) => new(ComparableKind.Point, "Point", new[] { x, y, z });
    static ComparableItem Curve() => new(ComparableKind.Unsupported, "Curve");
    static ComparableBranch Br(string path, params ComparableItem[] items) => new(path, items);
    static ComparableOutput Out(string name, params ComparableBranch[] branches) => new(name, branches);
    static ComparableOutput Out(string name, bool truncated, params ComparableBranch[] branches) => new(name, branches, truncated);
    static List<ComparableOutput> L(params ComparableOutput[] outputs) => outputs.ToList();

    [Fact]
    public void Identical_outputs_are_equal_and_the_summary_says_equivalent()
    {
        var a = L(Out("a", Br("{0}", Num(1), Num(2.5)), Br("{1}", Txt("x"))), Out("b", Br("{0}", Pt(0, 0, 0))));
        var b = L(Out("a", Br("{0}", Num(1), Num(2.5)), Br("{1}", Txt("x"))), Out("b", Br("{0}", Pt(0, 0, 0))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.True(r.AllEqual);
        Assert.All(r.Outputs, v => Assert.Equal("equal", v.Verdict));
        Assert.Equal(3, r.Outputs[0].Compared);
        Assert.Empty(r.Mismatches);
        Assert.Empty(r.ManualCheck);
        Assert.StartsWith("equivalent", r.Summary);
    }

    [Fact]
    public void Numbers_within_tolerance_pass_and_the_edge_is_inclusive()
    {
        var a = L(Out("n", Br("{0}", Num(1.0), Num(10.0))));
        var b = L(Out("n", Br("{0}", Num(1.001), Num(10.0011))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.False(r.AllEqual);
        var only = Assert.Single(r.Mismatches);
        Assert.Equal(1, only.Index);
        Assert.Equal("10", only.A);
        Assert.Contains("tolerance 0.001", only.Reason);
        Assert.Equal(1, r.Outputs[0].Mismatches);
        Assert.Equal(2, r.Outputs[0].Compared);
    }

    [Fact]
    public void Points_compare_per_coordinate_and_name_the_coordinate()
    {
        var a = L(Out("p", Br("{0}", Pt(1, 2, 3))));
        var b = L(Out("p", Br("{0}", Pt(1, 2.5, 3))));

        var r = OutputComparer.Compare(a, b, 0.01);

        var m = Assert.Single(r.Mismatches);
        Assert.StartsWith("coordinate 1 differs", m.Reason);
        Assert.Equal("(1, 2, 3)", m.A);
    }

    [Fact]
    public void Integers_are_exact_and_an_integer_against_a_number_compares_numerically_with_a_note()
    {
        var a = L(Out("i", Br("{0}", Int(3))));
        var b = L(Out("i", Br("{0}", Num(3.0))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.True(r.AllEqual);
        Assert.Contains("types differ: Integer on A, Number on B", r.Outputs[0].Note);

        var c = L(Out("i", Br("{0}", Int(3), Int(4))));
        var d = L(Out("i", Br("{0}", Int(3), Int(5))));
        Assert.Equal(1, OutputComparer.Compare(c, d, 10).Outputs[0].Mismatches);
    }

    [Fact]
    public void A_different_path_set_is_one_mismatch_and_shared_paths_still_compare()
    {
        var a = L(Out("t", Br("{0}", Num(1)), Br("{1}", Num(2))));
        var b = L(Out("t", Br("{0}", Num(1)), Br("{2}", Num(2))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.Equal("differs", r.Outputs[0].Verdict);
        var m = Assert.Single(r.Mismatches);
        Assert.Equal("paths differ", m.Reason);
        Assert.Equal("2: {0} {1}", m.A);
        Assert.Equal(1, r.Outputs[0].Compared);
    }

    [Fact]
    public void A_branch_count_difference_is_reported_and_the_overlap_is_compared()
    {
        var a = L(Out("t", Br("{0}", Num(1), Num(2), Num(3))));
        var b = L(Out("t", Br("{0}", Num(1), Num(9))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.Equal(2, r.Outputs[0].Mismatches);
        Assert.Contains(r.Mismatches, m => m.Reason == "item count differs" && m.A == "3" && m.B == "2");
        Assert.Contains(r.Mismatches, m => m.Index == 1);
        Assert.Equal(3, r.Outputs[0].ItemsA);
        Assert.Equal(2, r.Outputs[0].ItemsB);
    }

    [Fact]
    public void Text_is_exact_and_a_kind_change_is_a_reason()
    {
        var a = L(Out("s", Br("{0}", Txt("a"), Txt("b"))));
        var b = L(Out("s", Br("{0}", Txt("a"), Num(1))));

        var r = OutputComparer.Compare(a, b, 0.001);

        var m = Assert.Single(r.Mismatches);
        Assert.Equal("kind differs: Text vs Number", m.Reason);
    }

    [Fact]
    public void Unsupported_items_make_the_output_a_manual_check_not_a_pass()
    {
        var a = L(Out("c", Br("{0}", Curve(), Num(1))));
        var b = L(Out("c", Br("{0}", Curve(), Num(1))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.False(r.AllEqual);
        Assert.Equal("unsupported", r.Outputs[0].Verdict);
        Assert.Equal(1, r.Outputs[0].Compared);
        Assert.Equal(new[] { "c" }, r.ManualCheck);
        Assert.Contains("Curve", r.Outputs[0].Note);
        Assert.Contains("manual check", r.Summary);
    }

    [Fact]
    public void An_unsupported_type_name_that_changed_is_a_mismatch()
    {
        var a = L(Out("g", Br("{0}", Curve())));
        var b = L(Out("g", Br("{0}", new ComparableItem(ComparableKind.Unsupported, "Surface"))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.Equal("differs", r.Outputs[0].Verdict);
        Assert.Equal("type differs: Curve vs Surface", Assert.Single(r.Mismatches).Reason);
    }

    [Fact]
    public void Outputs_are_matched_by_name_case_insensitively_and_the_rest_are_missing()
    {
        var a = L(Out("Out", Br("{0}", Num(1))), Out("extra", Br("{0}", Num(1))));
        var b = L(Out("out", Br("{0}", Num(1))), Out("other", Br("{0}", Num(1))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.False(r.AllEqual);
        Assert.Equal(new[] { "equal", "missing-on-b", "missing-on-a" }, r.Outputs.Select(v => v.Verdict));
        Assert.Equal("other", r.Outputs[2].Output);
        Assert.Contains("exist on one side only", r.Summary);
    }

    [Fact]
    public void The_mismatch_list_is_capped_while_the_counts_stay_complete()
    {
        var a = L(Out("n", Br("{0}", Enumerable.Range(0, 20).Select(i => Num(i)).ToArray())));
        var b = L(Out("n", Br("{0}", Enumerable.Range(0, 20).Select(i => Num(i + 1)).ToArray())));

        var r = OutputComparer.Compare(a, b, 0.001, maxMismatches: 3);

        Assert.Equal(3, r.Mismatches.Count);
        Assert.True(r.MismatchesTruncated);
        Assert.Equal(20, r.Outputs[0].Mismatches);
    }

    [Fact]
    public void A_truncated_read_never_claims_equivalence()
    {
        var a = L(Out("n", true, Br("{0}", Num(1))));
        var b = L(Out("n", Br("{0}", Num(1))));

        var r = OutputComparer.Compare(a, b, 0.001);

        Assert.False(r.AllEqual);
        Assert.Equal("equal", r.Outputs[0].Verdict);
        Assert.StartsWith("partial", r.Outputs[0].Note);
        Assert.Contains("item cap", r.Summary);
    }

    [Fact]
    public void No_outputs_on_either_side_is_not_equivalence()
    {
        var r = OutputComparer.Compare(new List<ComparableOutput>(), new List<ComparableOutput>(), 0.001);

        Assert.False(r.AllEqual);
        Assert.Equal("no outputs on either component", r.Summary);
    }
}
