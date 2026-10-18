# Space Escape browser host

Experimental source-backed port of the original sample. The first browser run
currently reaches the splash render path; the end-to-end validator is expected
to expose remaining renderer, asset and effect-cache gaps. A successful publish
is not proof of playable gameplay.

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
Only default WGSL resource variants are prepared; runtime requirements still need
to be verified against the actual sample. DOM text/IME input remains unimplemented.
