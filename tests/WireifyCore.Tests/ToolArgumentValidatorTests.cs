// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.Text.Json;
using WireifyCore.Mcp;

namespace WireifyCore.Tests;

/// <summary>Arguments checked against the tool's own schema before binding, so a wrong call is
/// refused with the field named instead of "An error occurred invoking" (round-12 S12.11).</summary>
public class ToolArgumentValidatorTests
{
    const string SetSourceSchema = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "format": "uuid" },
            "source": { "type": "string" },
            "runtime": { "type": "string", "enum": ["cpython3", "ironpython2"] },
            "solve": { "type": "boolean" },
            "overwriteExternalEdits": { "type": "boolean" },
            "note": { "type": ["string", "null"] }
          },
          "required": ["id", "source"]
        }
        """;

    static JsonElement Schema => JsonDocument.Parse(SetSourceSchema).RootElement;

    static Dictionary<string, JsonElement> Args(string json)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in JsonDocument.Parse(json).RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }

    [Fact]
    public void A_well_formed_call_passes()
    {
        Assert.Null(ToolArgumentValidator.Validate("set_source", Schema,
            Args("""{"id":"11111111-1111-1111-1111-111111111111","source":"a = 1","runtime":"cpython3","solve":true,"note":null}""")));
    }

    [Fact]
    public void A_wrong_parameter_name_is_named_with_the_closest_real_one_and_the_missing_one()
    {
        var problem = ToolArgumentValidator.Validate("set_source", Schema,
            Args("""{"id":"11111111-1111-1111-1111-111111111111","code":"a = 1"}"""));

        Assert.NotNull(problem);
        Assert.StartsWith("set_source:", problem);
        Assert.Contains("missing required parameter 'source'", problem);
        Assert.Contains("unknown parameter 'code'", problem);
        Assert.Contains("did you mean 'source'", problem);
        Assert.Contains("This tool takes: id (string, uuid; required), source (string; required)", problem);
        Assert.EndsWith("Nothing was executed.", problem);
    }

    [Fact]
    public void A_number_where_a_uuid_belongs_is_named_with_where_to_get_one()
    {
        var problem = ToolArgumentValidator.Validate("set_source", Schema,
            Args("""{"id":"2","source":"a = 1"}"""));

        Assert.NotNull(problem);
        Assert.Contains("'id' must be a uuid", problem);
        Assert.Contains("get_document_summary", problem);
        Assert.Contains("(got \"2\")", problem);
    }

    [Fact]
    public void Type_and_enum_mismatches_are_named()
    {
        var problem = ToolArgumentValidator.Validate("set_source", Schema,
            Args("""{"id":"11111111-1111-1111-1111-111111111111","source":"a","solve":"yes","runtime":"python4"}"""));

        Assert.NotNull(problem);
        Assert.Contains("'solve' must be a boolean (got a string \"yes\")", problem);
        Assert.Contains("'runtime' must be one of cpython3, ironpython2", problem);
    }

    [Fact]
    public void Null_is_refused_where_the_schema_does_not_allow_it_and_accepted_where_it_does()
    {
        var refused = ToolArgumentValidator.Validate("set_source", Schema,
            Args("""{"id":"11111111-1111-1111-1111-111111111111","source":null}"""));
        Assert.Contains("'source' must not be null", refused);

        Assert.Null(ToolArgumentValidator.Validate("set_source", Schema,
            Args("""{"id":"11111111-1111-1111-1111-111111111111","source":"a","note":null}""")));
    }

    [Fact]
    public void A_far_fetched_name_gets_no_suggestion_and_a_schemaless_tool_passes_anything()
    {
        var problem = ToolArgumentValidator.Validate("set_source", Schema,
            Args("""{"id":"11111111-1111-1111-1111-111111111111","source":"a","thing":1}"""));
        Assert.Contains("unknown parameter 'thing'", problem);
        Assert.DoesNotContain("did you mean", problem);

        Assert.Null(ToolArgumentValidator.Validate("get_runtime_info", JsonDocument.Parse("true").RootElement, Args("""{"x":1}""")));
    }
}
