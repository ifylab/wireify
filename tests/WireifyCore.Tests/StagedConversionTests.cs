// SPDX-License-Identifier: Apache-2.0
using System;
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
    public void SelectConversionInputs_drops_unwired_undeclared_and_keeps_the_rest()
    {
        var selection = StagedConversion.SelectConversionInputs(
            new[] { "in1", "in2", "in3" },
            new[] { "in1" },
            new[] { new IoParamSpec("IN3", "item") }); // explicit declaration wins, case-insensitive

        Assert.Equal(new[] { "in1", "in3" }, selection.Effective);
        Assert.Equal(new[] { "in2" }, selection.DroppedUnwired);
    }

    [Fact]
    public void SelectConversionInputs_keeps_all_wired_and_drops_all_unwired_by_default()
    {
        var allWired = StagedConversion.SelectConversionInputs(
            new[] { "a", "b" }, new[] { "a", "b" }, null);
        Assert.Equal(new[] { "a", "b" }, allWired.Effective);
        Assert.Empty(allWired.DroppedUnwired);

        // A bare socket (nothing wired, nothing declared) converts to a no-input component.
        var noneWired = StagedConversion.SelectConversionInputs(
            new[] { "in1", "in2" }, Array.Empty<string>(), null);
        Assert.Empty(noneWired.Effective);
        Assert.Equal(new[] { "in1", "in2" }, noneWired.DroppedUnwired);
    }

    [Fact]
    public void DroppedUnwiredNote_names_the_inputs_and_the_keep_path()
    {
        var note = StagedConversion.DroppedUnwiredNote(new[] { "in2" });

        Assert.Contains("[in2]", note);
        Assert.Contains("nothing was wired", note);
        Assert.Contains("declare a name explicitly in 'inputs'", note);
    }

    [Fact]
    public void StampHeader_inserts_after_directive_and_never_duplicates()
    {
        var stamped = StagedConversion.StampHeader("#! python 3\nimport json\n", "W1 lines-tags-json");
        var lines = stamped.Split('\n');
        Assert.Equal("#! python 3", lines[0]);
        Assert.Matches(@"^# wireify W1 lines-tags-json @[0-9a-f]{8}$", lines[1]);
        Assert.Equal("import json", lines[2]);

        // Re-stamping (a revision, maybe with a new slug) replaces the old header — never two.
        var revised = StagedConversion.StampHeader(stamped, "W1 web-json");
        var revisedLines = revised.Split('\n');
        Assert.Matches(@"^# wireify W1 web-json @[0-9a-f]{8}$", revisedLines[1]);
        Assert.Single(revisedLines.Where(l => l.StartsWith("# wireify", StringComparison.Ordinal)));
        Assert.Equal("import json", revisedLines[2]);

        // Plain code (no directive) gets the header first.
        Assert.Matches(@"^# wireify W2 @[0-9a-f]{8}\na = 1$", StagedConversion.StampHeader("a = 1", "W2"));
    }

    [Fact]
    public void Stamp_fingerprint_is_stable_across_slug_changes_and_editor_round_trips()
    {
        // Same body -> same fingerprint regardless of the slug, directive, or line endings.
        var a = StagedConversion.StampHeader("#! python 3\nimport json\n", "W1 first-slug");
        var b = StagedConversion.StampHeader("import json", "W1 other-slug");
        string HashOf(string s) => s.Split('\n').First(l => l.StartsWith("# wireify")).Split('@')[1];
        Assert.Equal(HashOf(a), HashOf(b));

        var crlf = StagedConversion.StampHeader("import json\r\n", "W1 crlf");
        Assert.Equal(HashOf(a), HashOf(crlf));
    }

    [Fact]
    public void IsExternallyEdited_detects_body_drift_only()
    {
        var written = StagedConversion.StampHeader("#! python 3\nimport json\na = 1\n", "W3 cull-panels");

        // Exactly what Wireify wrote: no drift — including CRLF and final-newline round-trips.
        Assert.False(StagedConversion.IsExternallyEdited(written));
        Assert.False(StagedConversion.IsExternallyEdited(written.Replace("\n", "\r\n")));
        Assert.False(StagedConversion.IsExternallyEdited(written + "\n"));

        // A hand-edit below the stamp is drift.
        Assert.True(StagedConversion.IsExternallyEdited(written.Replace("a = 1", "a = 2")));

        // Legacy stamps (pre-fingerprint) and unstamped sources never claim drift.
        Assert.False(StagedConversion.IsExternallyEdited("#! python 3\n# wireify W3 cull-panels\na = 2\n"));
        Assert.False(StagedConversion.IsExternallyEdited("a = 2\n"));
        Assert.False(StagedConversion.IsExternallyEdited(""));
        Assert.False(StagedConversion.IsExternallyEdited(null));

        // A mangled fingerprint (wrong length / not hex) reads as legacy, not drift.
        Assert.False(StagedConversion.IsExternallyEdited("# wireify W3 x @zzzz\na = 1"));
        Assert.False(StagedConversion.IsExternallyEdited("# wireify W3 x @abc\na = 1"));
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
