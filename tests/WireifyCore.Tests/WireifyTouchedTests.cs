// SPDX-License-Identifier: Apache-2.0
using System;
using WireifyContract;
using Xunit;

namespace WireifyCore.Tests;

/// <summary>The touched-set codec behind the "wireify" canvas badge: one comma-joined value
/// in the document's own value table — inert without Wireify, append-only, dedup on write.</summary>
public class WireifyTouchedTests
{
    static readonly Guid A = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    static readonly Guid B = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid, also-not")]
    public void Parse_of_nothing_or_garbage_is_empty(string? raw)
        => Assert.Empty(WireifyTouched.Parse(raw));

    [Fact]
    public void Parse_reads_valid_guids_and_skips_garbage_between_them()
    {
        var set = WireifyTouched.Parse($"{A}, junk ,{B},{Guid.Empty}");
        Assert.Equal(2, set.Count);
        Assert.Contains(A, set);
        Assert.Contains(B, set);
    }

    [Fact]
    public void Append_to_empty_starts_the_set()
    {
        var raw = WireifyTouched.Append(null, A);
        Assert.Equal(A.ToString("D"), raw);
    }

    [Fact]
    public void Append_of_a_new_id_keeps_the_existing_ones()
    {
        var raw = WireifyTouched.Append(A.ToString("D"), B);
        var set = WireifyTouched.Parse(raw);
        Assert.Equal(2, set.Count);
        Assert.Contains(A, set);
        Assert.Contains(B, set);
    }

    [Fact]
    public void Append_of_a_present_id_is_null_meaning_no_write()
    {
        Assert.Null(WireifyTouched.Append($"{A},{B}", A));
    }

    [Fact]
    public void Append_of_the_empty_guid_is_null()
    {
        Assert.Null(WireifyTouched.Append(A.ToString("D"), Guid.Empty));
    }

    [Fact]
    public void Append_output_is_deterministic_whatever_the_input_order()
    {
        var one = WireifyTouched.Append(A.ToString("D"), B);
        var two = WireifyTouched.Append(B.ToString("D"), A);
        Assert.Equal(one, two);
    }

    // Remove is clear_badge's codec half: the deliberate exit from an otherwise
    // append-only set, mirroring Append's null-means-no-write contract.

    [Fact]
    public void Remove_of_a_present_id_keeps_the_rest()
    {
        var raw = WireifyTouched.Remove($"{A},{B}", A);
        var set = WireifyTouched.Parse(raw);
        Assert.Single(set);
        Assert.Contains(B, set);
    }

    [Fact]
    public void Remove_of_an_absent_id_is_null_meaning_no_write()
    {
        Assert.Null(WireifyTouched.Remove(A.ToString("D"), B));
        Assert.Null(WireifyTouched.Remove(null, A));
        Assert.Null(WireifyTouched.Remove("", A));
    }

    [Fact]
    public void Remove_of_the_last_id_empties_the_value_and_parse_reads_it_back_empty()
    {
        var raw = WireifyTouched.Remove(A.ToString("D"), A);
        Assert.Equal("", raw);
        Assert.Empty(WireifyTouched.Parse(raw));
    }

    [Fact]
    public void Remove_then_append_round_trips()
    {
        var without = WireifyTouched.Remove($"{A},{B}", A);
        var restored = WireifyTouched.Append(without, A);
        Assert.Equal(WireifyTouched.Parse($"{A},{B}"), WireifyTouched.Parse(restored));
    }
}
