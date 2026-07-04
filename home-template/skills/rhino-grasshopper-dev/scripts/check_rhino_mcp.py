#!/usr/bin/env python3
"""Check a RhinoMCP streamable HTTP endpoint and list available tools."""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
from typing import Any


def post_json(url: str, payload: dict[str, Any], timeout_s: float) -> dict[str, Any]:
    """Post a JSON-RPC payload and return the decoded JSON response.

    Args:
        url: RhinoMCP HTTP endpoint.
        payload: JSON-RPC payload.
        timeout_s: Request timeout in seconds.

    Returns:
        Decoded JSON response.

    Raises:
        RuntimeError: If the request fails or the response is not valid JSON.
    """
    data = json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers={
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout_s) as response:
            raw = response.read().decode("utf-8")
    except urllib.error.URLError as exc:
        raise RuntimeError(f"Could not connect to RhinoMCP at {url}: {exc}") from exc

    try:
        decoded = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"RhinoMCP returned non-JSON response: {raw[:200]}") from exc
    return decoded


def list_tools(url: str, timeout_s: float) -> list[str]:
    """Initialize the MCP endpoint and return available tool names."""
    initialize_payload: dict[str, Any] = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "codex-rhino-skill-check", "version": "1.0.0"},
        },
    }
    init_response = post_json(url, initialize_payload, timeout_s)
    if "error" in init_response:
        raise RuntimeError(f"RhinoMCP initialize failed: {init_response['error']}")

    tools_payload: dict[str, Any] = {
        "jsonrpc": "2.0",
        "id": 2,
        "method": "tools/list",
        "params": {},
    }
    tools_response = post_json(url, tools_payload, timeout_s)
    if "error" in tools_response:
        raise RuntimeError(f"RhinoMCP tools/list failed: {tools_response['error']}")

    tools = tools_response.get("result", {}).get("tools", [])
    return sorted(str(tool.get("name", "")) for tool in tools if tool.get("name"))


def build_parser() -> argparse.ArgumentParser:
    """Create the command-line parser."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://localhost:4862/", help="RhinoMCP HTTP endpoint.")
    parser.add_argument("--timeout", type=float, default=5.0, help="Request timeout in seconds.")
    parser.add_argument(
        "--require-tool",
        action="append",
        default=[],
        help="Tool name that must be present. Can be repeated.",
    )
    parser.add_argument("--json", action="store_true", help="Emit JSON instead of human-readable text.")
    return parser


def main() -> int:
    """Run the endpoint check."""
    args = build_parser().parse_args()
    try:
        tools = list_tools(args.url, args.timeout)
    except RuntimeError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    missing = sorted(set(args.require_tool) - set(tools))
    result = {"url": args.url, "tool_count": len(tools), "tools": tools, "missing": missing}
    if args.json:
        print(json.dumps(result, indent=2))
    else:
        print(f"RhinoMCP reachable: {args.url}")
        print(f"Tools: {len(tools)}")
        for tool in tools:
            print(f"- {tool}")
        if missing:
            print(f"Missing required tools: {', '.join(missing)}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
