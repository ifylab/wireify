// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WireifyCore.Connect
{
    /// <summary>
    /// Writes the Wireify MCP server entry into a Claude config file without ever clobbering
    /// other content. The contract is read -> parse -> set only our "wireify" key -> write:
    /// every other server, scope, and top-level key is preserved. A file that fails to parse
    /// is left untouched and an exception is thrown rather than overwriting it.
    /// </summary>
    public static class ConfigMerger
    {
        public const string ServerName = "wireify";

        /// <summary>The server entry shape (matches home-template/mcp.json.tmpl).</summary>
        public static JsonObject BuildServerEntry(int port, string secret)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException("secret required", nameof(secret));
            return new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"http://127.0.0.1:{port}/mcp",
                ["headers"] = new JsonObject { ["X-Wireify-Secret"] = secret },
            };
        }

        /// <summary>
        /// Merge the wireify server into a project-local <c>.mcp.json</c> (the preferred scope —
        /// it lives in the per-.gh home dir). Creates the file if missing.
        /// </summary>
        public static void MergeProjectMcpJson(string mcpJsonPath, int port, string secret)
        {
            var root = ReadRootObject(mcpJsonPath);
            var servers = GetOrAddObject(root, "mcpServers");
            servers[ServerName] = BuildServerEntry(port, secret);
            WriteRootObject(mcpJsonPath, root);
        }

        /// <summary>
        /// Fallback scope: <c>~/.claude.json</c>. Project-scoped servers live under
        /// <c>projects["&lt;abs path&gt;"].mcpServers</c>; user-wide servers under the top-level
        /// <c>mcpServers</c>. We target the project scope and leave everything else intact.
        /// </summary>
        public static void MergeClaudeJson(string claudeJsonPath, string projectAbsPath, int port, string secret)
        {
            if (string.IsNullOrEmpty(projectAbsPath))
                throw new ArgumentException("projectAbsPath required", nameof(projectAbsPath));
            var root = ReadRootObject(claudeJsonPath);
            var projects = GetOrAddObject(root, "projects");
            var project = GetOrAddObject(projects, projectAbsPath);
            var servers = GetOrAddObject(project, "mcpServers");
            servers[ServerName] = BuildServerEntry(port, secret);
            WriteRootObject(claudeJsonPath, root);
        }

        /// <summary>
        /// Pre-accept Claude Code's workspace trust for a Wireify-generated home in
        /// <c>~/.claude.json</c>, so the scaffolded <c>.claude/settings.json</c> allowlist applies
        /// from the very first session — an untrusted workspace IGNORES <c>permissions.allow</c>
        /// (live-confirmed on Windows: "Ignoring N permissions.allow entries ... this workspace has
        /// not been trusted", which names this exact key as the remedy). Only ever call this for
        /// homes Wireify itself scaffolds — never for user-owned folders. Merge-only.
        /// </summary>
        public static void EnsureProjectTrust(string claudeJsonPath, string homeDir)
        {
            if (string.IsNullOrEmpty(claudeJsonPath)) throw new ArgumentException("claudeJsonPath required", nameof(claudeJsonPath));
            if (string.IsNullOrEmpty(homeDir)) throw new ArgumentException("homeDir required", nameof(homeDir));

            // Claude Code keys the projects map with forward-slash paths on every OS (its own
            // trust warning prints the key in that form).
            var key = Path.GetFullPath(homeDir).Replace('\\', '/');

            var root = ReadRootObject(claudeJsonPath);
            var projects = GetOrAddObject(root, "projects");
            var project = GetOrAddObject(projects, key);
            project["hasTrustDialogAccepted"] = true;
            WriteRootObject(claudeJsonPath, root);
        }

        static JsonObject ReadRootObject(string path)
        {
            if (!File.Exists(path)) return new JsonObject();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Config at '{path}' is not valid JSON; refusing to overwrite it. ({ex.Message})", ex);
            }

            return node as JsonObject
                ?? throw new InvalidDataException($"Config at '{path}' is not a JSON object; refusing to overwrite it.");
        }

        static JsonObject GetOrAddObject(JsonObject parent, string key)
        {
            if (parent[key] is JsonObject existing) return existing;
            if (parent.ContainsKey(key) && parent[key] is not null)
                throw new InvalidDataException($"Config key '{key}' exists but is not an object; refusing to overwrite it.");
            var created = new JsonObject();
            parent[key] = created;
            return created;
        }

        static void WriteRootObject(string path, JsonObject root)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

            // Write to a sibling temp file first so a crash mid-write can never truncate the original.
            var tmp = path + ".wireify-tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
