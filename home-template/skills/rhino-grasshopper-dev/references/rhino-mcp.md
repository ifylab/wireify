# RhinoMCP Workflow

## Connection Checks

Use `scripts/check_rhino_mcp.py` to verify the HTTP endpoint:

```bash
python3 scripts/check_rhino_mcp.py --url http://localhost:4862 --require-tool run_python
```

If direct MCP tools are available in the session, use them instead of raw HTTP calls. If the tool namespace is not available but the HTTP endpoint responds, use JSON-RPC checks or the script for diagnostics.

## Current Minimal HTTP Tool Surface

Some RhinoMCP installs expose a compact tool set:

- `get_commands`
- `get_selection`
- `get_viewport_image`
- `run_command`
- `run_python`
- `set_selection`
- `zoom_to_layer`
- `zoom_to_object`

With this surface, prefer `run_python` for non-trivial RhinoCommon or Grasshopper API work. Use `run_command` only for simple named Rhino commands.

## Rich Router Tool Surface

The McNeel `cc-plugin` workflow describes a richer router model with tools for Grasshopper graph operations such as placing components, sliders, connecting wires, reading the canvas graph, and solving. If those tools are visible, use graph-level operations for Grasshopper definitions instead of manually scripting the Grasshopper document API.

Do not assume those richer tools exist. Always list tools or inspect the active MCP namespace first.

## Live Rhino Guardrails

- Get oriented before editing: list objects or selection, and capture a viewport when visual confirmation matters.
- Avoid destructive operations such as clearing the document, clearing the Grasshopper canvas, deleting objects, or baking geometry unless the user asks.
- Use named layers and object names for generated geometry when repeated runs are likely.
- Make scripts idempotent where possible; update named objects instead of creating duplicates when that matches the user request.
- Wrap multi-step document edits so partial failures do not leave unintended geometry.
- After edits, select or zoom to the changed objects and report object IDs or component names.

## Grasshopper Through Rhino Python

When graph tools are unavailable, Grasshopper definitions can still be created through Rhino Python if Grasshopper is loaded:

1. Run `_Grasshopper`.
2. Import `Grasshopper` and `Grasshopper.Instances`.
3. Create or inspect a `GH_Document`.
4. Use `Instances.ComponentServer` to search or emit component proxies.
5. Place components with attributes and pivots.
6. Connect sources to input parameters.
7. Run `doc.NewSolution(True)`.

Use this route carefully. Component APIs vary, so inspect component inputs/outputs before wiring.
