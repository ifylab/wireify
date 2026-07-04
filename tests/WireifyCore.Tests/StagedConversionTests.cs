// SPDX-License-Identifier: Apache-2.0
using System.Linq;
using WireifyContract;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class StagedConversionTests
{
    [Fact]
    public void Matches_staged_to_script_names_case_insensitively()
    {
        var result = StagedConversion.MatchInputs(
            new[] { "areas", "Pts", "min_area" },
            new[] { "pts", "AREAS", "min_area" });

        Assert.Equal(3, result.Matched.Count);
        Assert.Empty(result.Unmatched);
        Assert.Equal("AREAS", result.Matched.Single(m => m.Staged == "areas").Script);
    }

    [Fact]
    public void Reports_unmatched_staged_names_and_claims_each_script_input_once()
    {
        var result = StagedConversion.MatchInputs(
            new[] { "x", "x", "y" },
            new[] { "x", "z" });

        Assert.Single(result.Matched);
        Assert.Equal(new[] { "x", "y" }, result.Unmatched);
    }

    [Fact]
    public void Slugify_normalizes_to_bounded_kebab_case()
    {
        Assert.Equal("cull-panels", StagedConversion.Slugify("Cull  Panels!"));
        Assert.Equal("a-b", StagedConversion.Slugify("_a__b_"));
        Assert.Null(StagedConversion.Slugify("   "));
        Assert.Null(StagedConversion.Slugify(null));
        var slug = StagedConversion.Slugify("a very long task description that keeps going");
        Assert.NotNull(slug);
        Assert.True(slug!.Length <= 24);
    }

    [Fact]
    public void ValidateIo_defaults_inputs_to_staged_names_at_tree_access()
    {
        var io = StagedConversion.ValidateIo(
            new[] { "areas", "pts" }, null, new[] { new IoParamSpec("culled", "list") });

        Assert.Null(io.Error);
        Assert.Equal(new[] { ("areas", "tree"), ("pts", "tree") }, io.Inputs.Select(i => (i.Name, i.Access)));
        Assert.Equal(("culled", "list"), (io.Outputs[0].Name, io.Outputs[0].Access));
    }

    [Fact]
    public void ValidateIo_matches_inputs_case_insensitively_and_keeps_staged_casing()
    {
        var io = StagedConversion.ValidateIo(
            new[] { "Areas" },
            new[] { new IoParamSpec("areas", "list", "float") },
            new[] { new IoParamSpec("n") });

        Assert.Null(io.Error);
        Assert.Equal("Areas", io.Inputs[0].Name);
        Assert.Equal("list", io.Inputs[0].Access);
        Assert.Equal("float", io.Inputs[0].TypeHint);
    }

    [Fact]
    public void ValidateIo_refuses_bad_shapes_without_throwing()
    {
        var staged = new[] { "a", "b" };
        var outN = new[] { new IoParamSpec("n") };

        Assert.Contains("outputs is required", StagedConversion.ValidateIo(staged, null, null).Error);
        Assert.Contains("missing", StagedConversion.ValidateIo(staged, new[] { new IoParamSpec("a") }, outN).Error);
        Assert.Contains("not staged", StagedConversion.ValidateIo(staged,
            new[] { new IoParamSpec("a"), new IoParamSpec("b"), new IoParamSpec("c") }, outN).Error);
        Assert.Contains("invalid access", StagedConversion.ValidateIo(staged,
            new[] { new IoParamSpec("a", "branch"), new IoParamSpec("b") }, outN).Error);
        Assert.Contains("duplicate output", StagedConversion.ValidateIo(staged, null,
            new[] { new IoParamSpec("n"), new IoParamSpec("N") }).Error);
        Assert.Contains("both input and output", StagedConversion.ValidateIo(staged, null,
            new[] { new IoParamSpec("a") }).Error);
    }

    [Fact]
    public void StampHeader_inserts_after_directive_and_never_duplicates()
    {
        var stamped = StagedConversion.StampHeader("#! python 3\nimport json\n", "W1 lines-tags-json");
        Assert.Equal("#! python 3\n# wireify W1 lines-tags-json\nimport json\n", stamped);

        // Re-stamping (a revision, maybe with a new slug) replaces the old header.
        var revised = StagedConversion.StampHeader(stamped, "W1 web-json");
        Assert.Equal("#! python 3\n# wireify W1 web-json\nimport json\n", revised);

        // Plain code (no directive) gets the header first.
        Assert.Equal("# wireify W2\na = 1", StagedConversion.StampHeader("a = 1", "W2"));
    }

    [Fact]
    public void ParseAccess_canonicalizes()
    {
        Assert.Equal("item", StagedConversion.ParseAccess(" Item "));
        Assert.Equal("tree", StagedConversion.ParseAccess("TREE"));
        Assert.Null(StagedConversion.ParseAccess("branch"));
        Assert.Null(StagedConversion.ParseAccess(null));
    }

    [Fact]
    public void Nickname_convention_roundtrips()
    {
        Assert.Equal("W3", WireifyIds.MakeNickname(3));
        Assert.Equal("W3 cull-panels", WireifyIds.MakeNickname(3, "cull-panels"));

        Assert.True(WireifyIds.TryParseNumber("W3", out var bare));
        Assert.Equal(3, bare);
        Assert.True(WireifyIds.TryParseNumber("W12 cull-panels", out var slugged));
        Assert.Equal(12, slugged);

        Assert.False(WireifyIds.TryParseNumber("W", out _));
        Assert.False(WireifyIds.TryParseNumber("W0", out _));
        Assert.False(WireifyIds.TryParseNumber("Wire", out _));
        Assert.False(WireifyIds.TryParseNumber("W3x", out _));
        Assert.False(WireifyIds.TryParseNumber("beam W3", out _));
        Assert.False(WireifyIds.TryParseNumber(null, out _));
    }
}
