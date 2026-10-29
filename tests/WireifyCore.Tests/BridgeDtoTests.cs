// SPDX-License-Identifier: Apache-2.0
using System;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class BridgeDtoTests
{
    [Fact]
    public void Enabled_and_wired_flags_serialize_when_set_and_stay_absent_otherwise()
    {
        var info = new WireifyComponentInfo(1, Guid.Empty, "W1", "staged", new[] { "a", "b" },
            Enabled: false, InputWired: new[] { true, false });
        var legacy = new WireifyComponentInfo(1, Guid.Empty, "W1", "staged", new[] { "a" });
        var introspection = new ComponentIntrospection(Guid.Empty, "n", "nn",
            Array.Empty<ParamInfo>(), Array.Empty<ParamInfo>(), Enabled: false);

        var json = System.Text.Json.JsonSerializer.Serialize(info);
        Assert.Contains("\"Enabled\":false", json);
        Assert.Contains("\"InputWired\":[true,false]", json);
        Assert.DoesNotContain("Enabled", System.Text.Json.JsonSerializer.Serialize(legacy));
        Assert.Contains("\"Enabled\":false", System.Text.Json.JsonSerializer.Serialize(introspection));
    }
}
