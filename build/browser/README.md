# Browser toolchain smoke

This minimal .NET WebAssembly app verifies the browser toolchain before Stride
integration. It runs managed startup, calls JavaScript from C#, exports a C#
function to JavaScript, and checks the result after initial load and reload.
It does not create a WebGPU device or render a Stride scene.

Run from the repository root with the SDK pinned in global.json:

```powershell
dotnet workload install wasm-tools
dotnet publish build/browser/Smoke/Smoke.csproj -c Release
```

The initial verified installation used workload set
`11.0.100-rc.2.26513.2`, WASM packs `11.0.0-rc.2.26504.105`, and Emscripten
`6.0.2`. Publish uses the SDK's default Mono browser runtime, with trimming and
native runtime relinking, without managed AOT. Selecting .NET 11 does not itself
select the experimental browser CoreCLR runtime.

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

## C API transport probe

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
