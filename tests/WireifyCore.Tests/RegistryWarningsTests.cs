// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

/// <summary>The registry names a duplicated number where the agent looks first (round-12
/// S12.13: two `W2`s after a paste, nothing flagging it, 'do #2' resolved client-side).</summary>
public class RegistryWarningsTests
{
    static WireifyComponentInfo Entry(int n, string state, string id)
        => new(n, new Guid(id), $"W{n}", state, new List<string>());

    [Fact]
    public void Two_objects_on_one_number_are_named_with_their_ids_and_the_way_out()
    {
        var registry = new List<WireifyComponentInfo>
        {
            Entry(1, "converted", "11111111-1111-1111-1111-111111111111"),
            Entry(2, "staged", "22222222-2222-2222-2222-222222222222"),
            Entry(2, "staged", "33333333-3333-3333-3333-333333333333"),
            Entry(0, "authored", "44444444-4444-4444-4444-444444444444"),
            Entry(0, "authored", "55555555-5555-5555-5555-555555555555"),
        };

        var warnings = RegistryWarnings.DuplicateNumbers(registry);

        var warning = Assert.Single(warnings!);
        Assert.StartsWith("number 2 is carried by 2 objects", warning);
        Assert.Contains("22222222-2222-2222-2222-222222222222 (staged)", warning);
        Assert.Contains("33333333-3333-3333-3333-333333333333 (staged)", warning);
        Assert.Contains("'do #2' is ambiguous", warning);
        Assert.Contains("rename_component", warning);
    }

    [Fact]
    public void A_clean_registry_has_no_warnings()
    {
        Assert.Null(RegistryWarnings.DuplicateNumbers(new List<WireifyComponentInfo>
        {
            Entry(1, "converted", "11111111-1111-1111-1111-111111111111"),
            Entry(2, "staged", "22222222-2222-2222-2222-222222222222"),
        }));
        Assert.Null(RegistryWarnings.DuplicateNumbers(new List<WireifyComponentInfo>()));
    }
}
