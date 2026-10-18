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

Alternatively, set `BROWSER_EXECUTABLE` to an installed Chromium browser's
absolute executable path to avoid downloading another browser. The validator
serves the published app on an ephemeral loopback port, opens a fresh headless
browser, asserts runtime version and interop results across two page loads,
and closes its browser/server. Browser exceptions, console errors, failed network
requests, unexpected results and timeouts fail validation. It uses no existing
browser profile or interactive window.

The result is available in `globalThis.smokeResult` and the page's `#result`
element. The validator prints JSON with browser/runtime versions and both runs.
Keep machine-specific logs outside the checkout; bin/obj and npm dependencies
are ignored. This app is independent of Stride assets, native binaries and LFS
payloads.

Reference: [.NET browser JS interop](https://learn.microsoft.com/en-us/aspnet/core/client-side/dotnet-interop/wasm-browser-app?view=aspnetcore-10.0).
