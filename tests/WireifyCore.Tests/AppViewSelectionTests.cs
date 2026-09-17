// SPDX-License-Identifier: Apache-2.0
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class AppViewSelectionTests
{
    static readonly string[] ScriptOutputs = { "out", "floor_lines", "floor_breps" };

    [Fact]
    public void Default_skips_the_stdout_param()
    {
        // The PC-round bug: "first output" pinned every script component's card to the empty
        // stdout "out" placeholder while the real data sat in later outputs.
        var (index, error) = AppViewSelection.PickOutput(ScriptOutputs, null);
        Assert.Null(error);
        Assert.Equal(1, index); // floor_lines, not "out"
    }

    [Fact]
    public void A_named_output_is_matched_case_insensitively()
    {
        var (index, error) = AppViewSelection.PickOutput(ScriptOutputs, "Floor_Breps");
        Assert.Null(error);
        Assert.Equal(2, index);
    }

    [Fact]
    public void A_wrong_name_reports_instead_of_guessing()
    {
        var (index, error) = AppViewSelection.PickOutput(ScriptOutputs, "walls");
        Assert.Equal(-1, index);
        Assert.Contains("no output 'walls'", error);
    }

    [Fact]
    public void Stdout_is_reachable_by_naming_it()
    {
        var (index, error) = AppViewSelection.PickOutput(ScriptOutputs, "out");
        Assert.Null(error);
        Assert.Equal(0, index);
    }

    [Fact]
    public void Duplicate_names_resolve_to_the_first_match()
    {
        // Two outputs both keyed "out" (the W1 case): naming picks the first; the second is
        // reachable only by its param guid — the skill's cue to rename.
        var (index, _) = AppViewSelection.PickOutput(new[] { "out", "out" }, "out");
        Assert.Equal(0, index);
    }

    [Fact]
    public void Only_a_stdout_output_still_shows_something()
    {
        var (index, error) = AppViewSelection.PickOutput(new[] { "out" }, null);
        Assert.Null(error);
        Assert.Equal(0, index);
    }

    [Fact]
    public void No_outputs_reports_honestly()
    {
        var (index, error) = AppViewSelection.PickOutput(System.Array.Empty<string>(), null);
        Assert.Equal(-1, index);
        Assert.Contains("no outputs", error);
    }
}
