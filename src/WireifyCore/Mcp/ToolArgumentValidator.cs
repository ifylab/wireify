// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WireifyCore.Mcp
{
    /// <summary>
    /// Checks a tool call's arguments against the tool's own input schema BEFORE the SDK binds
    /// them, and names what is wrong. The SDK's binding failure surfaces as a bare
    /// "An error occurred invoking 'set_source'." — no field, no shape, no remedy — which cost
    /// a round-12 session three failed calls, a leash stop and a wrong diagnosis (S12.11).
    /// Pure: the schema is the JSON the tool advertises, the arguments are what the client sent.
    /// </summary>
    public static class ToolArgumentValidator
    {
        /// <summary>Null when the arguments fit; otherwise one sentence naming every problem,
        /// the tool's signature, and that nothing ran.</summary>
        public static string? Validate(string tool, JsonElement schema, IDictionary<string, JsonElement>? arguments)
        {
            if (schema.ValueKind != JsonValueKind.Object) return null;
            var props = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            var known = props.ValueKind == JsonValueKind.Object ? props.EnumerateObject().Select(o => o.Name).ToList() : new List<string>();
            var given = arguments ?? new Dictionary<string, JsonElement>();

            var problems = new List<string>();
            var missing = required.Where(name => !given.ContainsKey(name)).ToList();
            var unknown = known.Count > 0 ? given.Keys.Where(k => !known.Contains(k)).ToList() : new List<string>();
            foreach (var name in missing) problems.Add($"missing required parameter '{name}'");
            foreach (var kv in given)
            {
                if (unknown.Contains(kv.Key))
                {
                    // One unknown name beside one missing required one is the classic slip
                    // ("code" for "source"); otherwise only a near-miss spelling earns a hint.
                    var hint = unknown.Count == 1 && missing.Count == 1 ? missing[0] : Closest(kv.Key, known);
                    problems.Add($"unknown parameter '{kv.Key}'" + (hint is null ? "" : $" — did you mean '{hint}'?"));
                    continue;
                }
                if (props.ValueKind == JsonValueKind.Object && props.TryGetProperty(kv.Key, out var spec))
                {
                    var issue = Check(kv.Key, spec, kv.Value);
                    if (issue is not null) problems.Add(issue);
                }
            }
            if (problems.Count == 0) return null;
            return $"{tool}: {string.Join("; ", problems)}. This tool takes: {Signature(props, required)}. Nothing was executed.";
        }

        static string? Check(string name, JsonElement spec, JsonElement value)
        {
            var types = TypesOf(spec);
            if (value.ValueKind == JsonValueKind.Null)
                return types.Contains("null") || types.Count == 0 ? null : $"'{name}' must not be null";
            if (types.Count == 0) return null;

            var ok = types.Any(t => t switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => value.ValueKind == JsonValueKind.Array,
                "object" => value.ValueKind == JsonValueKind.Object,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => true,
            });
            if (!ok) return $"'{name}' must be {Describe(spec)} (got {Kind(value)})";

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString() ?? "";
                if (spec.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String
                    && string.Equals(f.GetString(), "uuid", StringComparison.OrdinalIgnoreCase)
                    && !Guid.TryParse(text, out _))
                    return $"'{name}' must be a uuid — the object's InstanceGuid from get_document_summary or introspect_component (got \"{Trim(text)}\")";
                if (spec.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array)
                {
                    var options = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
                    if (options.Count > 0 && !options.Any(o => string.Equals(o, text, StringComparison.OrdinalIgnoreCase)))
                        return $"'{name}' must be one of {string.Join(", ", options)} (got \"{Trim(text)}\")";
                }
            }
            return null;
        }

        static List<string> TypesOf(JsonElement spec)
        {
            if (!spec.TryGetProperty("type", out var t)) return new List<string>();
            if (t.ValueKind == JsonValueKind.String) return new List<string> { t.GetString()! };
            if (t.ValueKind == JsonValueKind.Array)
                return t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            return new List<string>();
        }

        static string Describe(JsonElement spec)
        {
            var types = TypesOf(spec).Where(t => t != "null").ToList();
            var text = types.Count == 0 ? "a value" : string.Join(" or ", types.Select(t => t == "integer" ? "an integer" : t == "array" ? "an array" : t == "object" ? "an object" : "a " + t));
            if (spec.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String) text += $" ({f.GetString()})";
            return text;
        }

        static string Signature(JsonElement props, List<string> required)
        {
            if (props.ValueKind != JsonValueKind.Object) return "no parameters";
            var parts = new List<string>();
            foreach (var prop in props.EnumerateObject())
            {
                var types = TypesOf(prop.Value).Where(t => t != "null").ToList();
                var type = types.Count == 0 ? "value" : string.Join("|", types);
                if (prop.Value.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String) type += ", " + f.GetString();
                if (prop.Value.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array)
                    type += ": " + string.Join("|", e.EnumerateArray().Select(x => x.ToString()));
                parts.Add($"{prop.Name} ({type}{(required.Contains(prop.Name) ? "; required" : "")})");
            }
            return string.Join(", ", parts);
        }

        static string Kind(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => $"a string \"{Trim(value.GetString() ?? "")}\"",
            JsonValueKind.Number => $"the number {value}",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Array => "an array",
            JsonValueKind.Object => "an object",
            _ => "null",
        };

        static string Trim(string text) => text.Length <= 40 ? text : text.Substring(0, 37) + "…";

        /// <summary>The known name closest to a wrong one, when it is close enough to be a slip
        /// or a synonym worth naming (edit distance within a third of the name).</summary>
        internal static string? Closest(string wrong, IReadOnlyList<string> known)
        {
            string? best = null;
            var bestDistance = int.MaxValue;
            foreach (var candidate in known)
            {
                var d = Distance(wrong.ToLowerInvariant(), candidate.ToLowerInvariant());
                if (d < bestDistance) { bestDistance = d; best = candidate; }
            }
            if (best is null) return null;
            var allowance = Math.Max(2, Math.Max(wrong.Length, best.Length) / 3);
            return bestDistance <= allowance ? best : null;
        }

        static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[a.Length, b.Length];
        }
    }
}
