# Space Escape browser host

Source-backed port of the original sample. Chrome verification covers rendered
menu/gameplay, mouse/touch Start, keyboard/swipe lane changes and sliding,
advancing distance, game over, Menu, Retry, GPU backbuffer and viewport resize,
focus loss/resume, reload and async shutdown. Both keyboard/mouse and touch lanes
exercise fresh startup and reload, audit the native imports and validate the
exact active WASM module. This does not establish support for every browser or
device-loss recovery.

Run from the checkout root with the pinned .NET SDK/workload installed and required
Space Escape/engine/native-tool LFS inputs hydrated:

```powershell
./build/browser/PrepareEmdawn.ps1 -Project SpaceEscape.Browser
./build/browser/PrepareFreeType.ps1 -Project SpaceEscape.Browser
./build/browser/PrepareBuiltinShaders.ps1 -Naga path/to/naga-29.0.0.exe
./build/browser/PrepareSpaceEscapeContent.ps1 -Naga path/to/naga-29.0.0.exe
dotnet publish build/browser/SpaceEscape.Browser/SpaceEscape.Browser.csproj -m:1 -c Release -p:StridePlatforms=Browser -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0 -p:WasmCachePath=C:/path/to/private-cache
$env:BROWSER_EXECUTABLE = 'C:/Program Files/Google/Chrome/Application/chrome.exe'
node build/browser/validate-space-escape.cjs
node build/browser/validate-space-escape.cjs --touch
```

Use a private cache on the SDK drive with this Windows Emscripten release. Serve
`bin/Release/net11.0/publish/wwwroot` over trusted localhost with the correct WASM
MIME type. C# owns game logic, graphics and input state; JavaScript provides DOM
events and animation scheduling. Existing external FreeType and Emdawn ports
provide the native WASM dependencies. Shader translation runs offline on desktop.

Browser runtime effect compilation is disabled. Missing offline effects must
produce an actionable failure rather than trying to load a native compiler or
submitting SPIR-V to browser WebGPU. Content conversion uses a separate database
and preserves asset namespaces, aliases and transitive bundled objects. It
currently retains original cached effect objects alongside their WGSL replacements.
Only default WGSL resource variants are prepared. `effect-requests.json` contains
the sample's exact resolved material requests captured from browser execution.
Desktop replay verifies their hashes before compiling and packaging them; no
native shader compiler runs in the browser. To refresh captures, set
`BROWSER_EFFECT_REQUESTS_PATH` to a local file when running the validator, then
pass that file as `-EffectRequests` to the content preparer. Review and update
the versioned requests when the sample's permutations change. Built-in sprite/UI/
font effects are compiled separately from current SDSL under ignored `obj/` paths.
DOM text/IME input remains unimplemented; this sample uses labels and buttons.
GPU callbacks use explicit wasm32 indirect string-view arguments with the pinned
Mono runtime. A focused regression verifies full Unicode shader diagnostics,
aborted-map messages and device-loss notification across reloads:
`node build/browser/validate-gpu-errors.cjs` (publish ManagedWebGpuProbe first,
using the instructions in the parent browser README). Notification is covered;
automatic recovery after device loss remains unimplemented.
