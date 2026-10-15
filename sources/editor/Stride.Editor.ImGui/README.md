# Experimental Dear ImGui editor

Windows editor host using Hexa.NET.ImGui and Stride's actual engine, asset
pipeline and authored document graphs. This remains an experimental port.

See [SOURCE_LAYOUT.md](SOURCE_LAYOUT.md) for source organization and
[architecture.md](../../../docs/editor/imgui/architecture.md) for ownership boundaries.
Agent instructions, parity plans and development evidence are maintained outside
the repository in the feature workspace.

## Build and run

From the repository root with .NET 10 and Stride's native build prerequisites:

```powershell
dotnet build build/Stride.Editor.ImGui.slnx -c Release -p:StrideGraphicsApi=Direct3D11 -p:StrideGraphicsApis=Direct3D11 -p:StrideSkipAutoPack=true
dotnet build sources/assets/Stride.AssetCompiler/Stride.AssetCompiler.csproj -c Release -p:StrideGraphicsApi=Direct3D11 -p:StrideGraphicsApis=Direct3D11 -p:StrideSkipAutoPack=true
& ./sources/editor/Stride.Editor.ImGui/bin/Release/net10.0-windows/win-x64/Stride.Editor.ImGui.exe
```

`StrideSkipAutoPack` prevents development builds from replacing packages in a
shared NuGet feed/cache. Build from the checkout paths even when a local workspace
stores generated output through directory junctions.

Open a project package using File > Open project package. Its paired `.csproj`
must be available. Project compilation and asset compilation run outside the UI;
their diagnostics appear in the editor's output views.

Capability runners and their coverage boundaries are maintained in the local
feature workspace during development. Tests that write projects use disposable
copies and support background synthetic input.
