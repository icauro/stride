# Nala blendshape baseline port

This branch ports NalaTheThird's implementation onto the current engine for later
correctness and performance comparisons. It is not a measured performance baseline yet.

## Provenance

- Engine base: `dd9ebaf962eb1fc3e3387b11cfb8c3ad730a699f`.
- Source: [nalathethird/stride, feature-BlendshapeShapekey](https://github.com/nalathethird/stride/tree/feature-BlendshapeShapekey).
- Source tip: `edcaf7b78a39345f08c9ab3dd282472a65652afa`.
- Eight commits were cherry-picked in their original order with authorship preserved.
- The independent `feature/our-morph` branch starts at the same engine base and does
  not contain this port.

## Port changes

- Move the editor view model into the renamed `Stride.Assets.Presentation.Wpf` project.
- Use the current SDSL-generated parameter keys instead of duplicate hand-written keys.
- Copy the pose-space rotation-axis property to a local before passing it by reference.
- Build the demo against the local engine project rather than published 4.3 packages,
  and update its logging call to the current API.

## Functional repairs

The original import path threw when constructing morph vertex semantics with both
an embedded target index and an explicit semantic index. Pass the index separately.

It also populated only vertex streams, leaving the serialized base attributes,
vertex layout, vertex count, and target delta arrays empty. The CPU/compute paths
require those arrays. Populate them from the same transformed data used to create
the vertex streams. Increment the model import command's cache version to rebuild
existing assets with this data.

These repairs are kept separate from the original donor commits and build fixes;
performance comparisons must identify this repaired port rather than claiming to
measure the donor branch unchanged.

## Verification

Run from the repository root in PowerShell:

```powershell
dotnet build sources/engine/Stride.Assets.Models/Stride.Assets.Models.csproj -c Release
dotnet build sources/editor/Stride.Assets.Presentation.Wpf/Stride.Assets.Presentation.Wpf.csproj -c Release
dotnet build samples/Graphics/BlendShapeDemo/BlendShapeDemo.Windows/BlendShapeDemo.Windows.csproj -c Release
dotnet test sources/engine/Stride.Assets.Tests/Stride.Assets.Tests.csproj -c Release --filter FullyQualifiedName~TestBlendShapes
```

The focused tests cover:

- Import of a self-contained glTF triangle with a position morph target, sparse cooking,
  and CPU deformation using the imported data.
- Simultaneous weighted targets on small and parallel CPU workloads, preservation of
  other vertex attributes, and return to the base pose when all weights become zero.
- Independent component weights for entities sharing a model.

## Remaining validation before benchmarking

- Execute dense GPU, sparse GPU, and fused skinning paths and compare their output
  against the CPU result, including transitions to zero weights and between modes.
- Verify real FBX facial rigs, imported morph animation curves, normal/tangent deltas,
  bounds/culling, model replacement, and resource cleanup.
- Supply a model and scene for the donor demo; it currently contains project/script
  scaffolding rather than a complete bundled visual test.
- Record hardware, backend, fixed workloads, CPU/GPU timings, and memory before
  using this implementation as a performance reference.

Successful C# builds and CPU tests do not establish GPU rendering correctness or speed.
