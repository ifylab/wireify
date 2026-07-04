#!/usr/bin/env python3
"""Create a minimal C# Grasshopper GHA project scaffold."""

from __future__ import annotations

import argparse
import re
import uuid
from pathlib import Path


def to_identifier(name: str) -> str:
    """Convert a project or component name to a C# identifier."""
    parts = re.findall(r"[A-Za-z0-9]+", name)
    if not parts:
        raise ValueError("Name must contain at least one letter or digit.")
    identifier = "".join(part[:1].upper() + part[1:] for part in parts)
    if identifier[0].isdigit():
        identifier = f"Gh{identifier}"
    return identifier


def to_package_name(name: str) -> str:
    """Convert a project name to a Yak-safe package name."""
    package = re.sub(r"[^a-zA-Z0-9_-]+", "-", name.strip()).strip("-").lower()
    if not package:
        raise ValueError("Project name must produce a non-empty package name.")
    return package


def write_text(path: Path, content: str, force: bool) -> None:
    """Write a file, refusing to overwrite unless force is true."""
    if path.exists() and not force:
        raise FileExistsError(f"{path} already exists. Use --force to overwrite.")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def csproj_text(project_id: str, target_frameworks: str, package_version: str) -> str:
    """Return a minimal SDK-style Grasshopper project file."""
    target_tag = "TargetFrameworks" if ";" in target_frameworks else "TargetFramework"
    return f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <{target_tag}>{target_frameworks}</{target_tag}>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>{project_id}</AssemblyName>
    <RootNamespace>{project_id}</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Grasshopper" Version="{package_version}" PrivateAssets="all" ExcludeAssets="runtime" />
  </ItemGroup>

  <Target Name="CopyDllToGha" AfterTargets="Build">
    <Copy SourceFiles="$(TargetPath)" DestinationFiles="$(TargetDir)$(TargetName).gha" />
  </Target>
</Project>
"""


def component_text(project_id: str, component_id: str, category: str, subcategory: str, component_guid: uuid.UUID) -> str:
    """Return a minimal GH_Component implementation."""
    return f"""using System;
using Grasshopper.Kernel;

namespace {project_id}.Components;

public sealed class {component_id}Component : GH_Component
{{
    public {component_id}Component()
        : base("{component_id}", "{component_id[:4]}", "Double a numeric value.", "{category}", "{subcategory}")
    {{
    }}

    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {{
        pManager.AddNumberParameter("Value", "V", "Input value.", GH_ParamAccess.item);
    }}

    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {{
        pManager.AddNumberParameter("Result", "R", "Doubled value.", GH_ParamAccess.item);
    }}

    protected override void SolveInstance(IGH_DataAccess DA)
    {{
        double value = 0.0;
        if (!DA.GetData(0, ref value))
        {{
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Value input is required.");
            return;
        }}

        if (double.IsNaN(value) || double.IsInfinity(value))
        {{
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Value must be finite.");
            return;
        }}

        DA.SetData(0, value * 2.0);
    }}

    public override Guid ComponentGuid => new("{component_guid}");
}}
"""


def assembly_info_text() -> str:
    """Return assembly-level Grasshopper loading metadata."""
    return """using Grasshopper.Kernel;

[assembly: GH_Loading(GH_LoadingDemand.ForceDirect)]
"""


def plugin_info_text(project_id: str, assembly_guid: uuid.UUID) -> str:
    """Return GH_AssemblyInfo metadata."""
    return f"""using System;
using Grasshopper.Kernel;

namespace {project_id};

public sealed class {project_id}Info : GH_AssemblyInfo
{{
    public override string Name => "{project_id}";
    public override string AuthorName => "";
    public override string AuthorContact => "";
    public override Guid Id => new("{assembly_guid}");
}}
"""


def manifest_text(package_name: str, project_id: str) -> str:
    """Return a minimal Yak manifest."""
    return f"""name: {package_name}
version: 0.1.0
authors:
  - Your Name
description: >
  Grasshopper components for {project_id}.
url: https://example.com
keywords:
  - grasshopper
  - rhino8
  - rhinocommon
"""


def scaffold(args: argparse.Namespace) -> Path:
    """Create the scaffold and return the project directory."""
    project_id = to_identifier(args.name)
    component_id = to_identifier(args.component)
    package_name = to_package_name(args.name)
    project_dir = (args.path / project_id).resolve()
    if project_dir.exists() and any(project_dir.iterdir()) and not args.force:
        raise FileExistsError(f"{project_dir} is not empty. Use --force to write into it.")
    project_dir.mkdir(parents=True, exist_ok=True)

    write_text(project_dir / f"{project_id}.csproj", csproj_text(project_id, args.target_frameworks, args.package_version), args.force)
    write_text(
        project_dir / "Components" / f"{component_id}Component.cs",
        component_text(project_id, component_id, args.category, args.subcategory, uuid.uuid4()),
        args.force,
    )
    write_text(project_dir / "Properties" / "AssemblyInfo.cs", assembly_info_text(), args.force)
    write_text(project_dir / f"{project_id}Info.cs", plugin_info_text(project_id, uuid.uuid4()), args.force)
    write_text(project_dir / "manifest.yml", manifest_text(package_name, project_id), args.force)
    write_text(project_dir / ".gitignore", "bin/\nobj/\n*.user\n.vs/\n", args.force)
    return project_dir


def build_parser() -> argparse.ArgumentParser:
    """Create the command-line parser."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("name", help="Project name.")
    parser.add_argument("--path", type=Path, default=Path.cwd(), help="Parent output directory.")
    parser.add_argument("--component", default="DoubleNumber", help="Initial component name.")
    parser.add_argument("--category", default="Research", help="Grasshopper tab/category.")
    parser.add_argument("--subcategory", default="Utilities", help="Grasshopper panel/subcategory.")
    parser.add_argument("--target-frameworks", default="net7.0", help="TargetFramework or TargetFrameworks value.")
    parser.add_argument("--package-version", default="8.*", help="Grasshopper NuGet package version.")
    parser.add_argument("--force", action="store_true", help="Allow overwriting generated files.")
    return parser


def main() -> int:
    """Run the scaffolder."""
    args = build_parser().parse_args()
    project_dir = scaffold(args)
    print(f"Created C# Grasshopper scaffold: {project_dir}")
    print("Next checks:")
    print(f"  dotnet restore {project_dir}")
    print(f"  dotnet build -c Release {project_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
