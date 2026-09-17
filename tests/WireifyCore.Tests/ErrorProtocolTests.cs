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
    public void TryExtractCode_reads_the_leading_protocol_code_and_refuses_prose()
    {
        Assert.True(ErrorProtocol.TryExtractCode(ErrorProtocol.DocNotOpen("t.gh"), out var open));
        Assert.Equal(ErrorProtocol.DocNotOpenCode, open);
        Assert.True(ErrorProtocol.TryExtractCode(ErrorProtocol.DocNotActive("t.gh"), out var active));
        Assert.Equal(ErrorProtocol.DocNotActiveCode, active);
        Assert.False(ErrorProtocol.TryExtractCode("value must be a number for a slider", out _));
        Assert.False(ErrorProtocol.TryExtractCode("WIREIFY_ without a colon", out _));
        Assert.False(ErrorProtocol.TryExtractCode(null, out _));
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
        Assert.Contains("Build", msg);
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
    public void UnknownHome_carries_the_closed_definition_code_and_says_open_the_file()
    {
        // Round-9 S9.47: the sessionless refusal had no code, so the app API answered the generic
        // WIREIFY_APP_BAD_REQUEST and the page classified an OPEN definition as closed. A home
        // nobody can resolve IS a definition that is not open at that path — say so, with the
        // code a client switches on, and without the dead routes (_Wireify, "client config").
        var msg = ErrorProtocol.UnknownHome("tower-a1b2c3d4");

        Assert.True(ErrorProtocol.TryExtractCode(msg, out var code));
        Assert.Equal(ErrorProtocol.DocNotOpenCode, code);
        Assert.Contains("tower-a1b2c3d4", msg);
        Assert.Contains("open that .gh", msg);
        Assert.Contains("reconnects by itself", msg);
        Assert.DoesNotContain("_Wireify", msg);
        Assert.DoesNotContain("client config", msg);
    }

    [Fact]
    public void Page_copy_names_the_apps_own_file_and_never_the_session()
    {
        // Round-10 S10.7/S10.3: the MCP sentence ("this session is connected to …") reached a
        // browser user verbatim, and after a Save As it named the copy the session followed.
        var closed = ErrorProtocol.PageDocNotOpen("HALO.gh");
        Assert.StartsWith("definition closed", closed);
        Assert.Contains("'HALO.gh'", closed);
        Assert.Contains("reconnects on its own", closed);
        Assert.DoesNotContain("session", closed);
        Assert.DoesNotContain("Build", closed);

        var unnamed = ErrorProtocol.PageDocNotOpen(null);
        Assert.StartsWith("definition closed", unnamed);
        Assert.Contains("the definition this app belongs to", unnamed);

        var background = ErrorProtocol.PageDocNotActive("HALO.gh");
        Assert.Contains("'HALO.gh' is open but not the front Grasshopper tab", background);
        Assert.Contains("bring it to front", background);
        Assert.DoesNotContain("session", background);
        Assert.DoesNotContain("mutat", background);
    }

    [Fact]
    public void DocNotOpen_names_Build_on_the_component_not_a_command()
    {
        var msg = ErrorProtocol.DocNotOpen("tower.gh");
        Assert.Contains("press Build on the Wireify component", msg);
        Assert.DoesNotContain("_Wireify", msg);
    }
}
