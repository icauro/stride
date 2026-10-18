# WebGPU backend and bindings

Raw, standalone .NET 10 bindings for pinned wgpu-native v29.0.1.1, plus an
experimental opt-in Stride backend. The default graphics backend list is unchanged.

Generated output lives in
sources/engine/Stride.Graphics.WebGPU.Bindings/Generated/:

- Enums.g.cs — native enums.
- Flags.g.cs — 64-bit flag enums.
- Handles.g.cs — opaque native handles.
- Structs.g.cs — native structs, callback fields and descriptor defaults.
- Constants.g.cs — version, library name and sentinel constants.
- Functions.g.cs — native function imports.

Check mode verifies every output file and rejects stale or unexpected generated
files.

From the repository root with PowerShell 7 and .NET 10:

```powershell
./build/wgpu/Generate.ps1
./build/wgpu/Generate.ps1 -Check
./build/wgpu/Validate.ps1
```

The validation runner currently requires Windows x64, Visual Studio C++ tools,
and a Windows SDK. It compares managed layouts, enum values and descriptor
defaults against a separately compiled C11 probe. Generated probe files and build
outputs stay in their owning obj/bin directories. Other operating systems and
architectures have not yet been validated.

For a native GPU smoke test, acquire the pinned release into a local cache:

```powershell
$native = ./build/wgpu/DownloadNative.ps1 -OutputDirectory ../artifacts/native-cache
./build/wgpu/Validate.ps1 -NativeLibrary $native
```

The download script verifies the archive and header hashes. Runtime loading
requires an explicit absolute library path, verifies its four-part native version
and checks every imported symbol. Native binaries are not stored in this checkout.
The loader keeps the library loaded for the process lifetime.

Native validation also exercises the developing backend context on both Vulkan
and D3D12: device/limits, queue completion, offscreen triangle pixel readback,
validation-error delivery and disposal. These tests require both native backends
on the test machine. The context source is linked into the validation executable
with STRIDE_GRAPHICS_API_WEBGPU. The separate engine validation below exercises
Stride's public graphics API. These checks do not establish full renderer support.

## Source organization

The Stride implementation lives in `sources/engine/Stride.Graphics/WebGPU/`,
following the existing Vulkan and Direct3D12 backends. Public graphics types use
`Type.WebGPU.cs`, with separate adapter factory and device feature files.
Partial files such as `CommandList.TextureMap.WebGPU.cs` group related operations.
Internal `WebGpu*` helpers handle native device ownership, format conversion and
shader adaptation. Raw generated C API bindings remain in the separate
`Stride.Graphics.WebGPU.Bindings` project.

WebGPU-specific validation projects follow the existing engine test folder
convention under `sources/engine/`:

- `Stride.Graphics.WebGPU.Bindings.Tests/`: C ABI, native loading and low-level validation.
- `Stride.Graphics.WebGPU.Tests/`: graphics resources, shaders, commands and presentation.
- `Stride.Games.WebGPU.Tests/`: game lifecycle, scenes and rendering integration.

These supplementary validation projects currently run as console executables.
Normal regression cases remain in the existing Stride test projects and use their
existing test SDK and runner. Top-level `tests/` retains platform assets and image
baselines; each native backend has separate result images, not duplicated tests.

Vulkan and D3D12 are runtime choices inside wgpu-native, sharing this WebGPU
implementation. They do not need separate `wgpu_on_vk` or `wgpu_on_dx12` source
trees or build variants. Keep native-specific workarounds localized and guarded
by the actual native backend where needed; use device capabilities for optional
features. Both native backends retain independent test lanes and results.

## Opt-in Stride backend

Select the engine variant with `StrideGraphicsApi=WebGPU`. At runtime set
`STRIDE_WEBGPU_NATIVE_LIBRARY` to the pinned DLL's absolute path. Optional
`STRIDE_WEBGPU_BACKEND=Vulkan` or `D3D12` selects the underlying native backend;
otherwise wgpu selects its default adapter backend.

```powershell
./build/wgpu/ValidateEngine.ps1 -NativeLibrary $native -Window
# Include the normal Stride.Engine.Game and GraphicsDeviceManager lifecycle:
./build/wgpu/ValidateEngine.ps1 -NativeLibrary $native -Window -Game
```

This builds the real Stride graphics assembly with its .NET 10 assembly processor
and disables automatic development-package output. It then validates both Vulkan
and D3D12. Supply `-Backend Vulkan` or `-Backend D3D12` for one native backend.
Checks include buffer upload/copy/map, dynamic buffer writes, array/mip/3D texture
round trips and region updates, padded-row pixel readback, depth/stencil clearing, and actual
`GraphicsContext.DrawTexture` with SpriteEffect. Patterned pixel comparisons check
texture orientation and sampling, and two recorded draws check constant-buffer
update ordering. Newly compiled and serialized SDSL validates the WebGPU target,
resource binding and pixel output. Compute checks direct/indirect dispatch and
structured-buffer results, including padded float3 storage and rejection of an
incompatible CPU stride. `-Window` additionally uses a hidden real HWND to exercise native
presentation, repeated frames, resizing, existing view recreation, present mode
changes, zero-size restoration and real OS minimize/restore under a hidden host.
It does not provide a visual desktop capture. Indexed/instanced/indirect rendering,
depth rejection, source-alpha blending and scissor clipping have pixel assertions;
resource and command-list lifetime checks include release before submission and
rejected cross-device work.
`-Game` runs eight verified frames through the normal Stride Game and
GraphicsDeviceManager initialization, frame submission and shutdown; with
`-Window`, it tests both headless and hidden SDL HWND hosts. It also renders a
procedural mesh with an emissive material through the normal forward renderer,
SceneSystem and shader compiler, checking mesh/background pixels on eight frames.
It also runs a lit cube/ground scene with directional shadows. Disabling only the
cube's casting must brighten a localized footprint while the scene stays visible.
Scene validation reports shader compilation failures instead of silently skipping
meshes. Optional failure/image captures use an absolute external directory set by
`STRIDE_WEBGPU_VALIDATION_ARTIFACTS`.

The backend currently targets native Windows x64. It reuses Stride's existing
built-in SPIR-V, removing debug line and Google semantic annotations before
wgpu's ordinary shader validation. Native module preparation also adapts image
types and supported resource operations while retaining numeric bindings. Depth bindings select cached pipeline variants with matching depth
image types and aspect-specific shader views; color bindings keep their types.
New SDSL targets WebGPU's uniform/storage layouts, lowers typed Buffer/RWBuffer
resources to storage buffers and uses existing SPIRV-Tools control-flow/opaque
inlining and dead-code cleanup. Shader cache revisioning is specific to WebGPU.
Native module preparation passes read-only scalar/vector arguments by value when
every use is a plain load, avoiding lost arguments on the pinned native Vulkan
path. Writable/reference, indexed-vector and other aggregate arguments retain
their original representation. Validation includes nested function values,
constant-gray luminance reduction/readback and actual template-style HDR tone
mapping/postprocessing, checking distinct scene and background colors.
Geometry
and tessellation stages are rejected. Browser deployment remains separate work.

Offscreen four-sample rendering and color resolve are implemented. Format
capabilities report the baseline WebGPU guarantees for the implemented formats,
plus R32/RG32/RGBA32 float sampling when the adapter provides Float32Filterable.
This optional feature is requested for the skybox SH reduction; adapters without
it cannot yet sample these formats through the automatic bind-group layouts.
Float32 blending remains disabled. DepthClipControl and the native
clamp-to-border feature are requested only when the adapter provides them. Border
colors support transparent black, opaque black and opaque white; arbitrary colors
are rejected. Validation allocates all advertised
formats with reported usages, checks exact MSAA resolve pixels, and exercises
deferred/immediate SpriteBatch with ordered partial dynamic-buffer uploads.
Bounded buffer/color-texture region copies and texture-to-staging regions are
implemented, including destination offsets into padded staging rows.

Matching single-sample D16/D24S8/D32 GPU textures support full copies and complete
XY-subresource region copies across mip/array layers. D24S8 preserves both depth
and stencil; the pinned Vulkan path explicitly transfers the stencil aspect
through a GPU-only buffer after the combined copy. D32 depth-only staging
readback is supported. Opaque D16/D24 depth/stencil CPU transfers and partial
depth/stencil rectangles remain unsupported.

Dynamic color textures support WriteDiscard maps and public Texture.SetData.
Maps expose padded rows/slices in a private native upload buffer; the texture copy
is recorded at unmap to preserve earlier/later command ordering. Texture
WriteNoOverwrite, byte-range maps and staging write maps remain unsupported.

Read-write buffer clears use native zero clears or an internal cached compute
pipeline for nonzero values. Raw/structured integer views repeat X; typed
8/16/32-bit channel views preserve their per-channel values and packing. Nonzero
clears currently require the buffer to fit one native storage binding. Texture
UAV clears reuse a bounded GPU fill plane or row band across selected mip levels,
array layers and depth slices. Integer clears copy low channel bits directly;
float clears convert float/half/normalized formats. Partial-depth 3D views and
other packed channel layouts remain unsupported.

Timestamp queries negotiate the optional native encoder-timestamp feature, report
the queue's tick frequency and read results asynchronously. A pool may span
command lists; results remain unavailable until every slot has been written and
submitted. Readback copies use a separate command buffer in the same submission
to avoid stale same-encoder Vulkan resolve results.

BC1 through BC7 textures support sampled 2D/cube resources when the adapter
provides TextureCompressionBC, including block-aware mip uploads and readback.
Compressed render/depth/storage attachments and multisampling are not supported.
Base dimensions must be block-aligned; tiny mip levels retain complete blocks.

This is an incomplete native backend: geometry/tessellation stages, wireframe,
some resource copies/maps and broader platform coverage remain unsupported. Unsupported operations throw. Windows presentation keeps a stable
Stride back buffer and uses one fullscreen draw into the acquired surface image;
this avoids requiring optional surface copy-destination support. Render pipelines
use wgpu's inferred layouts and Stride's existing pipeline cache. Bind groups are
cached by native layout, resource handles and buffer ranges for each recording,
with a limit of 512 retained groups. Explicit layout construction remains pending.
Null read-only storage buffers use a private zero fallback for empty light groups.
Unbound ordinary sampled float 2D textures use a private transparent-zero texture,
including optional inputs retained by ColorCombiner in LightStreak postprocessing.
Unset mutable samplers use LinearClamp; explicit and immutable samplers retain
their configured state. Other null texture types and writable resources remain
unsupported.
GetDimensions observes the fallback's physical size (one texel for the texture).

Reference sources/engine/Stride.Graphics.WebGPU.Bindings from consumers, then:

```csharp
using Stride.Graphics.WebGPU;
using Api = Stride.Graphics.WebGPU.WebGPU;

Api.LoadNativeLibrary(absoluteNativeLibraryPath);
var descriptor = WGPUBufferDescriptor.CreateDefault();
descriptor.size = 1024;
descriptor.usage = WGPUBufferUsage.CopyDst | WGPUBufferUsage.Storage;
```

Use CreateDefault() for descriptors with native INIT macros: zero initialization
does not preserve native sentinel/default values. WGPUBool is a 32-bit uint;
WGPUFlags typedefs become 64-bit enums. Callback fields are unmanaged Cdecl
function pointers. The caller owns handle release, UTF-8 allocation/pinning,
pointer lifetimes, callback userdata and callback completion/cancellation.

To upgrade, deliberately select a release; replace both headers from that same
release, update their manifest hashes and provenance/licenses, regenerate, and run
ABI/native validation against that release. Unsupported C constructs or macro
forms fail generation. Keep generated changes reviewable and do not edit them
manually. See NOTICE.md for sources and licenses.

### One-dimensional textures and optional R8 storage

Stride 1D textures use height-one native 2D storage so mip chains and render
attachments remain usable. WebGPU-only SPIR-V preparation expands scalar sample
coordinates with y=0.5 and integer texel/offset/gradient coordinates with y=0;
width queries extract the physical size.x. This preserves the public 1D resource
and shader API and caps the reported maximum width at the smaller native 1D/2D
limit. Mip clear isolation, SampleLevel, Load, width queries, mixed 1D/2D shader
bindings and rendering into a 1D target have pixel checks on Vulkan and D3D12.
Projected sampling, gather, arrays and multisampling for 1D images are rejected.
Other uncommon image operations remain outside the validated coverage.

R8_UNorm storage requires optional support. Request texture-formats-tier1 when
available; on the pinned native runtime, also negotiate adapter-specific format
features and probe R8 storage allocation under a validation error scope. Advertise
R8 storage only after one of those paths establishes support. A device lacking
it rejects R8 UAV creation explicitly before the invalid native call. This does
not advertise other adapter-specific formats. The standard constraints and tier
features are described in the [WebGPU specification](https://www.w3.org/TR/webgpu/).

Color clears of a complete 3D mip use a render pass per depth slice with an
explicit native depthSlice. The regression checks every texel and verifies that
clearing another mip does not disturb earlier values. This does not establish
geometry-shader/layered 3D drawing or partial-depth view support.

### Portable asynchronous context

The shared context also offers `CreatePortableAsync`, `WaitForIdleAsync` and
`ReadBufferAsync`. Portable creation requests only core feature names; native
clamp-to-border, encoder timestamp and adapter-specific format extensions stay
on the existing native creation path. Load the native provider before portable
creation on desktop; a statically linked browser provider needs no DLL loader.

Browser requests yield to callbacks without polling or blocking sleeps. Desktop
async requests poll with asynchronous delays. Cancellation/timeout abandons
unconsumed results, and late adapter/device callbacks release their handles.
Readback holds a buffer reference through completion and unmaps before returning
copied bytes. Await it before copying into, mapping or otherwise reusing that
buffer; overlapping reads/reuse reject explicitly. Disposing the context cancels
outstanding async queue/readback waits. Synchronous queue waits and maps reject
in a browser. Full browser game startup and async shutdown require host integration.

`Validate.ps1 -NativeLibrary ...` includes portable async device, queue, exact
readback/reuse and callback ownership checks on Vulkan and D3D12, alongside the
existing synchronous rendering checks. Device-loss recovery and browser canvas
presentation require separate integration evidence.

`await GraphicsAdapterFactory.InitializeWebGPUAsync()` prepares the first portable
device and adapter metadata before the synchronous game startup path runs.
`GraphicsDevice.New` consumes that prepared context once; the adapter factory owns
it until consumption and releases an unused context on disposal. The created
GraphicsDevice owns it afterwards. Existing native lazy initialization continues
to use the native capabilities path. Dispose/reinitialize the factory to prepare
another browser device. Factory disposal during initialization invalidates that
pending initialization. `GraphicsDevice.WaitForWebGPUIdleAsync` exposes yielding
queue completion to hosts. This does not make synchronous game shutdown browser
compatible; the host must integrate asynchronous completion before disposal.

WebGPU GraphicsDevice implements `IAsyncDisposable`: stop producing frames, then
`await device.DisposeAsync()` on the rendering thread. It completes a real async
queue wait immediately before the common resource teardown. The teardown then
uses that completed wait, without blocking the browser. Ordinary synchronous
disposal still rejects an undrained browser device. Game/window closing needs its
own async host sequence; this device API does not implement a game shutdown loop.

### Offline WGSL shaders

WebGPU shader packages retain original SPIR-V for native specialization and
reflection, plus explicitly converted WGSL variants. Package version1 and the
converter identity participate in the shader data hash; converted ShaderBytecode
IDs are recomputed without changing the common EffectBytecode serialization
layout. Native raw SPIR-V remains supported. Packaged shaders use WGSL on both
native and browser paths so native validation exercises the browser shader form.
Browsers reject raw SPIR-V and missing depth/storage variants explicitly.

The C# ShaderPackageTool runs Naga only during asset preparation. Pin the external
converter (MIT/Apache-2.0, [Naga](https://github.com/gfx-rs/wgpu/tree/v29.0.0/naga)):

```powershell
cargo install naga-cli --version 29.0.0 --locked --root build/wgpu/ShaderPackageTool/obj/naga
pwsh -NoProfile -File build/wgpu/ValidateWgsl.ps1 -NativeLibrary <absolute-wgpu-native.dll> -Naga build/wgpu/ShaderPackageTool/obj/naga/bin/naga.exe
```

This lane compiles real SDSL effects, converts/validates WGSL and checks texture
pixels/orientation and compute values on Vulkan and D3D12. The tool verifies Naga's
version and records the executable SHA256 and preparation revision in each package.
It uses `--keep-coordinate-space` to match the native SPIR-V import convention;
the CLI default flips Y and changes winding. No converter runs in browser startup.

For a serialized effect, run ShaderPackageTool with `<naga> <input-effect>
<output-effect> [variant-requests.json]`. It always includes the default variant.
Optional requests use this shape (numeric binding indices come from reflection):

```json
[{ "DepthBindings": [3], "StorageFormats": { "7": "R8G8B8A8_UNorm" } }]
```

The canonical key filters resources to the shader module and removes already
inferred comparison-depth bindings. Missing variants never fall back to an
incompatible layout. Variant enumeration and asset-pipeline integration for the
complex sample remain separate work; this tool does not establish sample coverage.

### Editor line rasterization

Like Stride's Vulkan backend, WebGPU accepts the legacy
`RasterizerStateDescription.MultisampleAntiAliasLine` hint without selecting a
separate line smoothing mode. Actual sample count follows the output attachments.
This allows editor line overlays to render; it does not promise smooth lines on
single-sample targets. Wireframe fill remains explicitly unsupported. The engine
validation compares line pixels with the hint enabled and disabled on both
single-sample and four-sample attachments.
