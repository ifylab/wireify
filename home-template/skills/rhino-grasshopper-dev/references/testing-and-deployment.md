# Testing And Deployment

## Test Layers

Use the lightest test layer that proves the behaviour:

1. **Pure unit tests** for numerical, geometry-helper, parsing, and validation logic that does not require Rhino.
2. **RhinoCommon tests** for APIs that require Rhino runtime, using Rhino-hosted/headless testing.
3. **Grasshopper integration tests** for component wiring, parameter access, data-tree shape, runtime messages, and solved outputs.
4. **RhinoMCP smoke tests** for live-document operations, viewport inspection, selection, and simple command/script execution.
5. **Packaging tests** for `.gha` generation, Yak build, install, restart, and component discovery.

## Automated Testing

For Rhino 8, prefer McNeel's `Rhino.Testing` direction for NUnit-based tests when the code needs Rhino runtime. Existing xUnit/Rhino.Inside setups can work but forum reports show sensitivity to parallelization and headless document assumptions.

For xUnit-based Grasshopper tests:

- Disable assembly and collection parallelization.
- Run from the command line if Visual Studio test explorer parallelizes between projects.
- Be explicit about test `.gh` files copied to output.
- Avoid assumptions that `RhinoDoc.ActiveDoc` is non-null in headless tests.

Keep pure logic testable without Rhino when possible. Consider `rhino3dm` only for tests that can use OpenNURBS-style geometry without Rhino runtime.

## Local Development Deployment

Use the project's documented deploy path first. Common options:

- Copy `.gha` to the Grasshopper Libraries folder.
- Add the build output folder in `GrasshopperDeveloperSettings`.
- Install a local Yak package.

Restart Rhino/Grasshopper after replacing compiled plugin assemblies. Do not overwrite a user's existing production plugin without a backup or explicit approval.

## Yak Packaging

Use Yak for modern Rhino/Grasshopper distribution. Avoid `.rhi` for modern .NET 8/Rhino 8 workflows unless the project explicitly requires legacy installation.

Useful commands:

```bash
yak spec
yak build
yak build --platform mac
yak build --platform win
yak login --ci
yak push package-name-version-rh8-any.yak
```

On macOS, Rhino's Yak is commonly:

```bash
/Applications/Rhino 8.app/Contents/Resources/bin/yak
```

On Windows:

```powershell
C:\Program Files\Rhino 8\System\Yak.exe
```

Pin Yak versions in CI where possible. Use `YAK_TOKEN` for automated publishing rather than interactive login.

## Smoke Test Checklist

After a build or deployment:

- Confirm `.gha` exists in the expected framework output directory.
- Confirm Rhino starts without plugin load errors.
- Confirm Grasshopper opens and the component appears under the expected category/subcategory.
- Place the component or load a minimal `.gh` test file.
- Solve with normal input, missing input, invalid geometry, and an edge case.
- Check runtime messages and output data-tree topology.
- Capture a viewport or component graph if RhinoMCP tools are available.

## Reporting

Report exactly what was validated. Distinguish:

- Static inspection.
- Build success.
- Rhino/Grasshopper load success.
- Runtime solve success.
- Yak package build/install success.
- Hardware or GUI validation not performed.
