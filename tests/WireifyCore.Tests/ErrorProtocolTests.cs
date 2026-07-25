// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using WireifyCore.Bridge;

namespace WireifyCore.Tests;

public class ErrorProtocolTests
{
    static WireifyComponentInfo Entry(int n, string state = "converted")
        => new(n, Guid.NewGuid(), $"W{n} slug", state, new List<string>());

    [Fact]
    public void NotFound_carries_code_registry_and_recovery()
    {
        var id = Guid.NewGuid();
        var msg = ErrorProtocol.NotFound(id, new List<WireifyComponentInfo> { Entry(3, "staged"), Entry(1) });

        Assert.StartsWith(ErrorProtocol.NotFoundCode, msg);
        Assert.Contains(id.ToString(), msg);
        Assert.Contains("W1 'W1 slug' converted", msg);
        Assert.Contains("W3 'W3 slug' staged", msg);
        Assert.Contains("get_document_summary", msg);
        // Sorted by number regardless of input order.
        Assert.True(msg.IndexOf("W1 '", StringComparison.Ordinal) < msg.IndexOf("W3 '", StringComparison.Ordinal));
    }

    [Fact]
    public void NotFound_with_empty_registry_says_so()
    {
        var msg = ErrorProtocol.NotFound(Guid.NewGuid(), new List<WireifyComponentInfo>());
        Assert.Contains("(no Wireify components on the canvas)", msg);
    }

    [Fact]
    public void NotFound_registry_caps_at_twenty_entries()
    {
        var registry = Enumerable.Range(1, 25).Select(n => Entry(n)).ToList();
        var msg = ErrorProtocol.NotFound(Guid.NewGuid(), registry);

        Assert.Contains("W20 '", msg);
        Assert.DoesNotContain("W21 '", msg);
        Assert.Contains("(+5 more)", msg);
    }

    [Fact]
    public void Timeout_messages_carry_codes_and_the_load_bearing_phrases()
    {
        var busy = ErrorProtocol.Busy(15);
        Assert.StartsWith(ErrorProtocol.BusyCode, busy);
        Assert.Contains("NOT executed", busy);

        var queue = ErrorProtocol.QueueTimeout("wire", 20);
        Assert.StartsWith(ErrorProtocol.QueueTimeoutCode, queue);
        Assert.Contains("wire", queue);
        Assert.Contains("NOT started", queue);
    }

    [Fact]
    public void InputWired_names_the_occupants_and_both_explicit_modes()
    {
        var toId = Guid.NewGuid();
        var sources = new List<WireEndInfo>
        {
            new(Guid.NewGuid(), "Entwine", "Result"),
            new(Guid.NewGuid(), "Panel", "Panel"),
        };

        var msg = ErrorProtocol.InputWired("in1", toId, sources);

        Assert.StartsWith(ErrorProtocol.InputWiredCode, msg);
        Assert.Contains("input 'in1'", msg);
        Assert.Contains(toId.ToString(), msg);
        Assert.Contains("2 source(s)", msg);
        Assert.Contains("Entwine.Result", msg);
        Assert.Contains("Panel.Panel", msg);
        Assert.Contains("mode 'replace'", msg);
        Assert.Contains("'add'", msg);
        Assert.Contains("never touches an occupied input", msg);
    }

    [Fact]
    public void InputWired_caps_the_listed_sources()
    {
        var sources = Enumerable.Range(1, 12)
            .Select(n => new WireEndInfo(Guid.NewGuid(), $"Src{n}", "out"))
            .ToList();

        var msg = ErrorProtocol.InputWired("in1", Guid.NewGuid(), sources);

        Assert.Contains("12 source(s)", msg);
        Assert.Contains("Src10.out", msg);
        Assert.DoesNotContain("Src11.out", msg);
        Assert.Contains("(+2 more)", msg);
    }

    [Fact]
    public void ExternalEdit_embeds_the_current_source_and_both_recovery_paths()
    {
        var id = Guid.NewGuid();
        var msg = ErrorProtocol.ExternalEdit("W3 cull-panels", id, "# wireify W3 cull-panels @ab12cd34\na = 1");

        Assert.StartsWith(ErrorProtocol.ExternalEditCode, msg);
        Assert.Contains("W3 cull-panels", msg);
        Assert.Contains(id.ToString(), msg);
        Assert.Contains("merge your change", msg);
        Assert.Contains("overwriteExternalEdits", msg);
        Assert.Contains("--- current source ---", msg);
        Assert.Contains("a = 1", msg);
        Assert.DoesNotContain("truncated", msg);
    }

    [Fact]
    public void ExternalEdit_caps_a_huge_source_and_points_at_get_source()
    {
        var huge = new string('x', 20000);
        var msg = ErrorProtocol.ExternalEdit("W1", Guid.NewGuid(), huge);

        Assert.Contains("truncated", msg);
        Assert.Contains("get_source", msg);
        Assert.True(msg.Length < 18000);
    }

    [Fact]
    public void NoDoc_and_NotASocket_carry_codes_and_recovery()
    {
        Assert.StartsWith(ErrorProtocol.NoDocCode, ErrorProtocol.NoDoc());
        Assert.Contains("open the", ErrorProtocol.NoDoc());

        var notSocket = ErrorProtocol.NotASocket(Guid.NewGuid(), "Addition");
        Assert.StartsWith(ErrorProtocol.NotASocketCode, notSocket);
        Assert.Contains("Addition", notSocket);
        Assert.Contains("set_source", notSocket);
    }

    [Fact]
    public void DocNotOpen_names_the_file_and_never_retargets()
    {
        var msg = ErrorProtocol.DocNotOpen("tower.gh");

        Assert.StartsWith(ErrorProtocol.DocNotOpenCode, msg);
        Assert.Contains("'tower.gh'", msg);
        Assert.Contains("only ever touch that definition", msg);
        Assert.Contains("Connect", msg);
    }

    [Fact]
    public void DocNotActive_says_untouched_reads_still_work_and_asks_for_the_front_tab()
    {
        var msg = ErrorProtocol.DocNotActive("tower.gh");

        Assert.StartsWith(ErrorProtocol.DocNotActiveCode, msg);
        Assert.Contains("'tower.gh'", msg);
        Assert.Contains("untouched", msg);
        // The agent must keep working: reads route to the bound doc from the background, and the
        // user is only needed at the moment of the next mutation.
        Assert.Contains("Reads still work", msg);
        Assert.Contains("only when you are ready to mutate", msg);
        Assert.Contains("bring 'tower.gh' to front", msg);
        Assert.Contains("Never dodge", msg);
    }

    [Fact]
    public void NoSession_points_at_Connect()
    {
        var msg = ErrorProtocol.NoSession("tower-a1b2c3d4");

        Assert.Contains("tower-a1b2c3d4", msg);
        Assert.Contains("Connect from Rhino", msg);
    }
}
