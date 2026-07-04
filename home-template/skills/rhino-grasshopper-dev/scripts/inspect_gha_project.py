#!/usr/bin/env python3
"""Statically inspect a C# Grasshopper GHA project for common risks."""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path


_GUID_VALUE = r"([0-9a-fA-F-]{36}|x{8}-x{4}-x{4}-x{4}-x{12})"
# Match: new Guid("..."), new System.Guid("..."), Guid.Parse("..."), target-typed new("...").
GUID_RE = re.compile(
    rf"(?:new\s+(?:System\.)?Guid\s*\(|Guid\.Parse\s*\(|new\s*\()\s*\"{_GUID_VALUE}\"\s*\)"
)


@dataclass(frozen=True)
class Finding:
    """A static inspection finding."""

    level: str
    path: Path
    message: str


def local_name(tag: str) -> str:
    """Return the local XML element name without namespace."""
    return tag.rsplit("}", 1)[-1]


def child_texts(root: ET.Element, name: str) -> list[str]:
    """Return text from all XML children with the given local name."""
    values: list[str] = []
    for element in root.iter():
        if local_name(element.tag) == name and element.text:
            values.append(element.text.strip())
    return values


def inspect_csproj(path: Path) -> list[Finding]:
    """Inspect one C# project file."""
    findings: list[Finding] = []
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as exc:
        return [Finding("error", path, f"Invalid XML: {exc}")]

    target_values = child_texts(root, "TargetFramework") + child_texts(root, "TargetFrameworks")
    if not target_values:
        findings.append(Finding("warning", path, "No TargetFramework or TargetFrameworks found."))
    elif not any("net48" in value or "net7.0" in value or "net8.0" in value for value in target_values):
        findings.append(
            Finding("warning", path, f"Targets may not match Rhino 8 plugin practice: {', '.join(target_values)}")
        )

    package_refs: list[str] = []
    direct_refs: list[str] = []
    copied_refs: list[str] = []
    for element in root.iter():
        name = local_name(element.tag)
        include = element.attrib.get("Include", "")
        if name == "PackageReference":
            package_refs.append(include)
        if name == "Reference":
            direct_refs.append(include)
            private_values = [
                child.text.strip().lower()
                for child in element
                if local_name(child.tag) == "Private" and child.text
            ]
            if include in {"RhinoCommon", "Grasshopper", "GH_IO", "Eto", "Rhino.UI"} and private_values != ["false"]:
                copied_refs.append(include)

    refs = set(package_refs + direct_refs)
    if "Grasshopper" not in refs:
        findings.append(Finding("warning", path, "No Grasshopper PackageReference or Reference found."))
    if "RhinoCommon" not in refs and "Grasshopper" not in package_refs:
        findings.append(Finding("warning", path, "No RhinoCommon reference found."))
    for ref in copied_refs:
        findings.append(Finding("warning", path, f"{ref} reference should normally have Private=false."))

    if package_refs and not any("Grasshopper" == ref for ref in package_refs):
        findings.append(Finding("info", path, "Project uses NuGet but not the Grasshopper package."))
    return findings


def inspect_cs_files(root: Path) -> list[Finding]:
    """Inspect C# source files for component-level risks."""
    findings: list[Finding] = []
    guid_locations: dict[str, list[Path]] = {}
    component_files = 0

    for path in sorted(root.rglob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        if "GH_Component" in text:
            component_files += 1
            if "AddRuntimeMessage" not in text:
                findings.append(
                    Finding("info", path, "GH_Component file does not call AddRuntimeMessage; verify invalid inputs are surfaced.")
                )
            if "RegisterInputParams" not in text or "RegisterOutputParams" not in text:
                findings.append(Finding("warning", path, "GH_Component file is missing parameter registration methods."))
            if "SolveInstance" not in text:
                findings.append(Finding("warning", path, "GH_Component file is missing SolveInstance."))

        for match in GUID_RE.finditer(text):
            guid = match.group(1).lower()
            guid_locations.setdefault(guid, []).append(path)
            if guid.startswith("xxxxxxxx"):
                findings.append(Finding("error", path, "Placeholder ComponentGuid found. Generate a real GUID."))

    if component_files == 0:
        findings.append(Finding("warning", root, "No C# files containing GH_Component were found."))

    for guid, paths in sorted(guid_locations.items()):
        unique_paths = sorted(set(paths))
        if len(unique_paths) > 1:
            joined = ", ".join(str(path) for path in unique_paths)
            findings.append(Finding("error", root, f"Duplicate GUID {guid} appears in: {joined}"))

    return findings


def inspect_project(path: Path) -> list[Finding]:
    """Inspect a C# Grasshopper project directory."""
    root = path.resolve()
    findings: list[Finding] = []
    csproj_paths = sorted(root.rglob("*.csproj"))
    if not csproj_paths:
        findings.append(Finding("error", root, "No .csproj files found."))
    for csproj in csproj_paths:
        findings.extend(inspect_csproj(csproj))
    findings.extend(inspect_cs_files(root))
    return findings


def build_parser() -> argparse.ArgumentParser:
    """Create the command-line parser."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path, help="Project directory to inspect.")
    parser.add_argument("--fail-on-warning", action="store_true", help="Exit non-zero for warnings as well as errors.")
    return parser


def main() -> int:
    """Run the static inspection."""
    args = build_parser().parse_args()
    findings = inspect_project(args.path)
    if not findings:
        print("No obvious GHA project risks found.")
        return 0

    for finding in findings:
        print(f"{finding.level.upper()}: {finding.path}: {finding.message}")

    has_error = any(finding.level == "error" for finding in findings)
    has_warning = any(finding.level == "warning" for finding in findings)
    if has_error or (args.fail_on_warning and has_warning):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
