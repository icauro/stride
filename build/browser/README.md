# Browser toolchain smoke

This .NET WebAssembly app verifies managed startup, JavaScript interop and the
actual Stride Core, Foundation and Games assemblies across initial load and
reload. It checks browser platform selection, the virtual filesystem, exact PNG
decoding and canvas window creation, resize, host state and disposal. It does not
initialize a graphics device, advance a game or render a Stride scene.

Run from the repository root with the SDK pinned in global.json:

```powershell
dotnet workload install wasm-tools
pwsh -NoProfile -File build/browser/ValidatePlatform.ps1
dotnet publish build/browser/Smoke/Smoke.csproj -c Release -p:StridePlatforms=Browser -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0
```

The initial verified installation used workload set
`11.0.100-rc.2.26513.2`, WASM packs `11.0.0-rc.2.26504.105`, and Emscripten
`6.0.2`. Publish uses the SDK's default Mono browser runtime, with trimming and
native runtime relinking, without managed AOT. Selecting .NET 11 does not itself
select the experimental browser CoreCLR runtime.

Pass `StridePlatforms=Browser` globally when building or publishing Stride runtime
projects: NuGet restore must select the same framework as compilation. Browser
runtime libraries target net11.0; existing desktop targets retain net10.0. Host
asset tools remain separate desktop builds. The assembly processor uses its
existing net10.0 host binary and the platform-neutral Shared processing mode.

Install the pinned browser-test dependency and run validation:

```powershell
Push-Location build/browser
npm ci
npx playwright install chromium
npm run smoke
Pop-Location
```

## WebGPU transport baseline

```powershell
dotnet publish build/browser/WebGpuProbe/WebGpuProbe.csproj -c Release
Push-Location build/browser
npm run webgpu
Pop-Location
```

WebGpuProbe uses direct .NET JavaScript interop to create a browser device, compile
WGSL, draw a red triangle on blue, present it to a canvas and asynchronously read
the texture back. C# checks every pixel's color, analytic interior/exterior
regions and foreground/background counts. Width65 deliberately requires padding:
GPU rows use512bytes while packed rows use260bytes. Three frames are checked per
load; the validator repeats after reload. An invalid map request is checked with
a validation error scope, a rejected JS promise must reach C#, and an injected
unavailable adapter must fail startup without producing verified frames.

This is a transport baseline, not a Stride backend or a final decision to replace
the C bindings. Initialization/mapping use promises and C# tasks without blocking
the browser thread. The proof awaits a JSObject then copies its typed array into
a managed int array because this SDK does not support Task<int[]> imports. This
copy expands pixel storage on the managed side; measure a byte/memory-view path
before production use. Reported frame timings include GPU/readback, marshalling
and managed verification; they are not rendering benchmarks.

WebGPU must actually be available to the selected browser. No software fallback
or GPU-enabling flags are silently added. Results record adapter information,
advertised features, readback layout, pixel counts, cleanup and elapsed times.
Only default device capabilities are requested; advertised adapter features do
not imply they were enabled on that device. This test does not establish Stride
shader-variant, asset-loading or lifecycle compatibility.

## Generated C# bindings probe (integration path)

```powershell
pwsh -NoProfile -File build/browser/PrepareEmdawn.ps1 -Project ManagedWebGpuProbe
cargo install naga-cli --version 29.0.0 --locked --root build/wgpu/ShaderPackageTool/obj/naga
pwsh -NoProfile -File build/browser/PrepareEngineShaders.ps1 -Naga build/wgpu/ShaderPackageTool/obj/naga/bin/naga.exe
pwsh -NoProfile -File build/browser/PrepareContent.ps1
$wasmProbeCache = Join-Path $env:LOCALAPPDATA 'Stride/WasmCache/net11-rc2-emscripten-6.0.2'
dotnet publish build/browser/ManagedWebGpuProbe/ManagedWebGpuProbe.csproj -c Release -p:StridePlatforms=Browser -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0 "-p:WasmCachePath=$wasmProbeCache"
Push-Location build/browser
npm run webgpu:managed
Pop-Location
```

This is the intended language boundary: C# rendering/resource code calls the
existing generated C# bindings, which call statically linked Emdawn. There is no
handwritten C source or NativeFileReference rendering helper in this project.
Emdawn's own C++/JavaScript implementation remains a dependency. Only the canvas
extension struct is declared locally, from the pinned browser header; common
types, defaults, function signatures and callbacks use the shared bindings.

The same lane compiles the production `WebGpuDeviceContext` sources used by native
validation. It checks portable async adapter/device creation, negotiated limits,
exact buffer readback and reuse, queue completion, cancellation followed by retry,
and disposing with a read pending. It also references the actual Graphics assembly,
prepares/creates a GraphicsDevice, uploads a buffer, and checks three17x9 red/green
clears through Stride CommandList, Texture.Copy and nonblocking GetData. Every
pixel must match before async device disposal. It verifies factory disposal of an
unused prepared device. It requires `sharedContextVerified` and
`engineDeviceVerified` across load and reload. The published import table now
contains85 audited core functions;
native polling, timestamp-period queries and surfacePresent must remain absent.
This verifies context/GraphicsDevice startup and clears, not a running game.
Space Escape still needs its complete effect/variant set, input, runtime
subsystems and its own packaged game assets.

Desktop preparation compiles real SDSL texture/compute effects and converts them
with pinned Naga29.0.0 using the shared package tool. Generated assets stay in the
probe's ignored obj/effects folder and publish under effects/. At runtime C#
HttpClient fetches them and Stride deserializes their actual EffectBytecode,
reflection and parameter keys. The renderer verifies four exact textured pixels
including orientation, then dispatches compute and verifies all64buffer values.
The harness requires `wgslEffectsVerified` on load/reload. No converter executable
or runtime SDSL compilation is needed in browser startup. This is shader asset
delivery evidence, separate from the database tests below.

PrepareContent.ps1 uses desktop engine serializers to build actual GameSettings,
a namespaced Scene containing an entity/transform, and a GraphicsCompositor into
a compressed Stride bundle. ContentPackageTool packages a database's index,
bundles and aliases with a version1 manifest containing sizes and SHA256 hashes.
Its normal command is `ContentPackageTool <data/db directory> <web output> [default bundle]`.
The web output must be outside the input database. Generated fixture files stay
in ignored obj/content and publish under content/; they are not Space Escape's
assets and do not require a GPU or a shader compiler to deserialize.

Before starting a browser game, await
`BrowserContent.PreloadAsync(httpClient, absoluteManifestUri)` and assign the
returned default bundle name to `Game.AssetBundleName`. The helper installs a
complete verified database in the existing /data/db filesystem mount. Game's
existing synchronous content loader then reads local files; it never waits for
HTTP. Installation refuses an already populated database, stages downloads until
all checks pass, and cleans failed/cancelled staging data. It rejects invalid
paths, ambiguous casing, unsupported manifest versions, missing index/default
bundle, and content exceeding the declared byte budget (default1GiB).

BrowserAssetCheck reads the compressed bundle through ObjectDatabase and
ContentManager, checks GameSettings, the namespaced scene/entity/transform and
compositor, and confirms a bare alias resolves to the same scene instance.
Negative cases cover corrupt hashes, an actual HTTP404, path traversal, duplicate
case variants, incorrectly cased index, version mismatch, size budget,
cancellation and refusal to replace an existing database. Native file locks
explicitly report Unsupported in the browser's private WASM filesystem; Store
uses its existing unsupported-filesystem fallback. No OS lock is reported as
acquired. This is content delivery/deserialization evidence; asset-backed scene
rendering and full Game initialization remain to be integrated.

The actual SwapChainGraphicsPresenter now accepts a Browser WindowHandle whose
NativeWindow is the CSS selector of an existing canvas. The host supplies canvas
pixel dimensions and advances browser animation frames; it never calls the
unsupported surfacePresent import. The shared renderer performs the final blit.
Linear rendering uses an sRGB view of the UNorm canvas surface for correct color
encoding. Resize occurs between submitted frames, with resource retirement handled
by WebGPU rather than a blocking CPU wait.

BrowserPresenterCheck verifies submitted pixels survive texture replacement,
zero-size acquisition is skipped, resize restores rendering, and final offscreen
pixels remain correct. The headless harness keeps both presenters alive and checks
all65520pixels in each260x252composited canvas screenshot, for gamma and linear
output, across reload (color tolerance1/255). Integer CSS geometry excludes page
background edges without reducing the checked canvas area. PNGJS7.0.0 is a pinned
test-only screenshot decoder. This is real canvas evidence, not a playable game.

The project registers the existing logical import module `wgpu_native` with the
pinned .NET11 RC2 Mono WASM import generator. Its final linker supplies Emdawn;
the module name does not request the Windows DLL. This uses an SDK-private target
hook intentionally scoped to the pinned proof. Re-evaluate it when upgrading the
SDK or targeting browser CoreCLR. Shared bindings bypass the DLL resolver on
browser hosts, and LoadNativeLibrary explicitly rejects browser invocation.

ManagedFrame owns resource handles and callback GCHandles, requests adapter/device
and pipeline asynchronously, encodes draws/copies and awaits map completion.
Mapped bytes are copied into a byte array while valid. Managed callbacks copy
native error text and complete Tasks rather than unwinding an exception through
native code. Frame/callback counts return to zero after each fixture lifetime.
Invalid-map validation/retry, DLL-loader rejection and unavailable-adapter startup
are checked alongside the shared triangle pixel assertions and reload.

The validator also checks the generated pinvoke-table.h against core-imports.json:
exactly85 imports whose signatures match both audited headers. Any change requires
review against the pinned headers. Native-only calls, incompatible multi-draw
signatures and the browser-aborting surfacePresent call are excluded. This is a
fixture allowlist, not a compile-time ban on those methods for every future host.

BrowserGameLoopCheck also runs GameBase with the real graphics manager, canvas
presenter and command list. Two startup/shutdown cycles per page verify presented
frame counts, all65x63 back-buffer pixels, inactive-frame yielding, exit between
frames, active asynchronous disposal, reentrant disposal and exactly-once system
and lifecycle teardown. Its test assembly uses the same friend-assembly pattern
as the existing no-assets engine tests. This covers the game foundation without
claiming that an asset-backed scene or Space Escape already runs.

Browser hosts must stop scheduling frames and await `game.DisposeAsync()` on the
browser thread. It ends the run, deactivates the window, releases game systems
and the presenter while the device is alive, then awaits real GPU completion
before device teardown. Closed windows ignore pending host resize/focus updates.
Native window close and synchronous disposal behavior remain unchanged.

The integrated tests cover async context cancellation, GraphicsDevice, basic
offline shader assets, canvas presentation and GameBase lifecycle. The original
ManagedFrame diagnostic remains a single-in-flight reference with no device-loss
recovery. Full asset-backed Game initialization, the complete shader permutation
set, input and sample content preparation remain open. Native Vulkan/D3D12 behavior
and DLL loading use the same binding assembly.

## C API transport probe (diagnostic reference)

Prepare the pinned Emdawn package (SHA512 verified; sources remain in ignored
obj/), then publish using a private writable Emscripten cache:

```powershell
pwsh -NoProfile -File build/browser/PrepareEmdawn.ps1
$wasmProbeCache = Join-Path $env:LOCALAPPDATA 'Stride/WasmCache/net11-rc2-emscripten-6.0.2'
dotnet publish build/browser/CWebGpuProbe/CWebGpuProbe.csproj -c Release "-p:WasmCachePath=$wasmProbeCache"
Push-Location build/browser
npm run webgpu:c
Pop-Location
```

The cache must be on the same Windows volume as the Emscripten toolchain with
this version: Emscripten6.0.2 cannot build ports with source/cache on different
drives. Do not change the installed frozen cache or copy the full installed
cache. A private cache builds only the required dependencies. Keep SDK/cache
versions aligned.

CWebGpuProbe performs instance/surface creation, adapter/device and pipeline
requests, WGSL shader creation, draw/submit, texture copies and asynchronous
mapping through the C API. Its C helper is statically linked using
NativeFileReference; managed imports use the triangle source basename. All GPU
calls use the pinned provider header. JavaScript only boots .NET, schedules a
browser frame and reports results; it does not issue GPU commands for this probe.
Native map callbacks copy padded bytes directly into a managed byte array before
unmapping, without expanding pixels to JS/managed int arrays.

Both GPU probes use Shared/TrianglePixels.cs for the same color/geometry checks.
The C probe also captures a real invalid-map validation failure, retries a valid
map, checks that owned native frame contexts were released, and injects an
unavailable-adapter failure. Each frame creates and releases its own device and
surface to exercise cleanup; timings therefore include initialization and cannot
be compared with the JS probe as a transport benchmark. Native adapter type is
recorded without forcing hardware/software mode.

The C probe submits a copy to the canvas, but pixel assertions read an offscreen
texture. It does not verify a browser-composited screenshot. Emdawn explicitly
aborts on wgpuSurfacePresent: use browser frame scheduling, not the native
presentation call. A production presenter still needs persistent resources,
resize/device-loss handling and independently verified visible frames.

This proves C API transport, not reuse of Stride's generated import loader or
engine renderer. The native DLL loader, native extensions and blocking waits
still need separation before engine integration. Downloaded dependency licenses
are retained in the package; THIRD-PARTY-NOTICES.txt accompanies the published app.

## Browser selection and validation

Set `BROWSER_EXECUTABLE` to an installed Chromium browser's
absolute executable path to avoid downloading another browser. The validator
serves the published app on an ephemeral loopback port, opens a fresh headless
browser, asserts runtime version and interop results across two page loads,
and closes its browser/server. Browser exceptions, console errors, failed network
requests, unexpected results and timeouts fail validation. It uses no existing
browser profile or interactive window. `BROWSER_PUBLISH_ROOT` may override the
served publish wwwroot directory for isolated toolchain experiments.

The result is available in `globalThis.smokeResult` and the page's `#result`
element. The validator prints JSON with browser/runtime versions and both runs.
Keep machine-specific logs outside the checkout; bin/obj and npm dependencies
are ignored. This app is independent of Stride assets, native binaries and LFS
payloads.

Reference: [.NET browser JS interop](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0).
