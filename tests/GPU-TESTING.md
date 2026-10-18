# GPU Regression Testing

Stride uses image comparison tests to validate rendering across graphics APIs. Each test renders a scene, captures a screenshot, and compares it against a reference ("gold") image stored in the `tests/` directory.

## Graphics APIs and Launch Profiles

Test projects have two kinds of launch profiles:

- **GPU profiles** (Direct3D11, Direct3D12, Vulkan) — run on your actual GPU. Results depend on your specific GPU and driver version, so gold images may not match. These are useful for local development and visual inspection, but are not used in CI.
- **Software profiles** (D3D11 WARP, D3D12 WARP, Vulkan Lavapipe) — run on CPU-based software renderers. These produce nearly deterministic output regardless of GPU hardware, making them suitable for CI. Gold images in the repository are generated with these renderers.

To select a profile, use the `STRIDE_GRAPHICS_API` environment variable or the launch profile in your IDE.

### Running Tests in Batch (Test Explorer / CLI)

Tests default to **software rendering** — no configuration needed. Visual Studio Test Explorer and `dotnet test` will use WARP/Lavapipe automatically, matching the gold images in the repository.

To force tests onto your real GPU instead, use the runsettings file:

**Visual Studio:** Test > Configure Run Settings > Select Solution Wide runsettings File > `build/GameTests-GPU.runsettings`

**CLI:** `dotnet test --settings build/GameTests-GPU.runsettings`

This sets `STRIDE_TESTS_GPU=1`, which skips software rendering. Note that GPU results are hardware-dependent and gold images may not match.

## Software Renderers

### Vulkan — Lavapipe

[Lavapipe](https://docs.mesa3d.org/drivers/llvmpipe.html) is Mesa's CPU-based Vulkan driver (the Vulkan front-end of `llvmpipe`). The `Stride.Dependencies.Lavapipe` NuGet package provides the software-Vulkan driver (`vulkan_lvp.dll` and its ICD JSON) for Windows, Linux, and macOS x64, built from Mesa source in `.github/workflows/dep-lavapipe.yml`.

Gold images are stored under `tests/<TestProject>/Windows.Vulkan/Lavapipe/` (and `Linux.Vulkan/Lavapipe/`, plus `Android.Vulkan/Lavapipe-LinuxHost/` and `Lavapipe-WindowsHost/` for the emulator).

Lavapipe is included automatically when building with `StrideGraphicsApi=Vulkan`. When software rendering is active (the default), the test framework invokes the package's module initializer, which sets `VK_DRIVER_FILES` so the Vulkan loader picks Lavapipe — no registry or environment setup needed locally. This path is desktop-only (Windows/Linux/macOS); Android and iOS get Vulkan from the emulator/device.

### macOS / iOS — MoltenVK

macOS defaults to the **real GPU** via [MoltenVK](https://github.com/KhronosGroup/MoltenVK) (Vulkan-on-Metal), since that's the real-world Apple renderer; its gold lives under `macOS.Vulkan/MoltenVK/` and per-chip buckets like `macOS.Vulkan/Apple M4/`. Set `STRIDE_TESTS_GPU=0` to force macOS onto Lavapipe instead. iOS uses MoltenVK on-device (`iOS.Vulkan/MoltenVK/`).

### Direct3D — WARP

[WARP](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/directx-warp) (Windows Advanced Rasterization Platform) is Microsoft's software rasterizer for Direct3D. It is built into Windows and requires no additional packages.

Gold images are stored under `tests/<TestProject>/Windows.Direct3D11/WARP/` and `tests/<TestProject>/Windows.Direct3D12/WARP/`.

## CompareGold Tool

A visual comparison tool for reviewing gold image differences:

```bash
# Launch (opens browser at http://localhost:5505)
tests\compare-gold.cmd

# Or directly:
dotnet run --project build/tools/Stride.CompareGold
```

### Features

- **Treeview** with test suites as collapsible groups
- **Side-by-side comparison** with color-coded diff overlay (blue=noise, green=minor, yellow=noticeable, red=significant)
- **Pixel inspector** on hover — zoomed RGB values for all images
- **Synchronized zoom/pan** across gold, source, and diff
- **CI artifact download** — fetch test output from GitHub Actions runs (requires [gh CLI](https://cli.github.com/))
- **Promote** selected images to gold with one click

### Workflow

1. Run tests locally or on CI — failing tests save output to `tests/local/`
2. Launch CompareGold — it auto-loads local output
3. Review differences — expand rows for side-by-side comparison
4. Promote accepted images to gold
5. Commit the updated gold images

## Generating Gold Images

Gold images should be generated using the **software renderers** to ensure they are reproducible in CI. Run the tests locally with the software renderer profile and commit the resulting screenshots.

Important: gold images must be generated with the **same Lavapipe build** used in CI. The `Stride.Dependencies.Lavapipe` NuGet package is built from Mesa source (see `.github/workflows/dep-lavapipe.yml`) so local and CI binaries are identical. A different Mesa/Lavapipe build may produce subtly different images due to compiler flags and CPU-specific floating-point behavior.

### Generating Gold Across All Platforms (CI)

Locally you can only produce Windows and Linux gold. To generate gold for **every** platform/API at once — including macOS (MoltenVK) and Android (emulator) — dispatch the [`test-gold-gen.yml`](../.github/workflows/test-gold-gen.yml) workflow (Actions tab → "Test Gold Generation" → Run workflow, or via the `gh` CLI). It runs the same reusable per-platform test workflows the PR gate uses, gathers every platform's rendered output into one downloadable `gold-images` artifact, and optionally promotes it.

```bash
# Generate gold for one test across all platforms and commit it to the branch
gh workflow run test-gold-gen.yml --repo <owner>/stride --ref <branch> \
  -f project=sources/engine/Stride.Graphics.Tests/Stride.Graphics.Tests.csproj \
  -f test-filter=FullyQualifiedName~TestStaticSpriteFont \
  -f update-gold=auto
```

(PowerShell: put it on one line, or use backtick `` ` `` continuations — `\` is bash-only.)

Key inputs — all four platforms default to on:

- `project` / `test-filter` — narrow the build+run to one suite / one test; blank runs the whole GPU solution.
- `graphics-api` — blank = all three Windows APIs, or pick one.
- `update-gold` — `none` (artifact only), `auto` (commit to the branch), `amend` (fold into the last commit; force-pushes), `pr` (open a PR). The default branch always lands via PR.
- `dedup-gold` — also drop redundant existing gold.

Generating brand-new gold makes the platform jobs go **red** ("reference image missing") — that's expected. The `gather` job always runs; download its `gold-images` artifact (or point CompareGold at the run) to review. Promotion is tolerance-aware — the same per-pixel compare the tests use — so a render within tolerance of a higher-priority API's gold is covered by fallback instead of committed as near-identical gold.

### Linux Gold Images

```powershell
# Build on Windows, test on Linux via WSL2
.\build\test-linux-gpu.ps1

# Skip rebuild, just re-run tests
.\build\test-linux-gpu.ps1 -SkipBuild

# Review results visually
tests\compare-gold.cmd
```

## Dealing with Flaky Tests

Lavapipe is generally deterministic, but some tests can be flaky due to sub-pixel rasterization differences. Common symptoms:

- A single edge pixel flickers between two values across runs
- Very thin geometry or text renders slightly differently

To fix flaky tests:

1. **Adjust the camera** — nudge the camera position slightly so geometry edges don't land exactly on pixel boundaries.
2. **Avoid sub-pixel edges** — ensure test geometry is large enough that a 1-pixel shift doesn't matter.
3. **Check determinism** — run the test 10+ times locally to confirm it produces identical output every time before committing new gold images.

## CI Workflow

The CI workflow (`.github/workflows/test-windows-game.yml`) does the following for Vulkan tests:

1. Restores NuGet packages with `StrideGraphicsApi=Vulkan` to pull the Lavapipe package
2. Registers the Lavapipe ICD in the Windows registry — but only so the **asset compiler** has a Vulkan device at build time (Skybox compilation). The runtime tests instead rely on the `Stride.Dependencies.Lavapipe` module initializer, which sets `VK_DRIVER_FILES`.
3. Builds the test projects
4. Runs the tests, comparing rendered output against the gold images

## GPU Validation

Tests automatically assert zero GPU validation errors and warnings after each test run. The Vulkan validation layer (Khronos) and D3D12 debug layer are enabled when running in debug mode.

If a test triggers a known false positive from the validation layer, you can suppress it with the `[AllowGpuValidationError]` attribute:

```csharp
[AllowGpuValidationError(GraphicsPlatform.Vulkan,
    "substring of the validation error message",
    Reason = "Explanation of why this is a false positive")]
public class MyTests : GameTestBase { }
```

Prefer fixing the root cause over suppressing errors.

## WebGPU lanes (Windows x64)

WebGPU reuses the existing xUnit regression projects through
`build/wgpu/RunTests.ps1`. Each selected suite builds once and runs separately
through wgpu-native Vulkan and D3D12. These are experimental lanes; failures are
reported normally and do not change the other API lanes.

| Lane | Existing tests selected |
| --- | --- |
| Smoke | Mipmap count, 2D texture upload/readback, 2D array upload/readback |
| Resources | Texture creation/mips/arrays/UAV/3D plus device-loss simulation |
| GPU | Exactly the suites listed in build/Stride.Tests.Game.GPU.slnf, the standard GPU CI selection |
| Graphics | Graphics.Tests, Graphics.Tests.10_0, Graphics.Tests.11_0 |
| Engine | Engine.Tests |
| Physics | Physics.Tests |
| UI | UI.Tests |
| Particles | Particles.Tests |
| All | GPU plus Particles.Tests |

Run from the checkout with an explicit result directory. Keep local reports
outside the checkout when working in an isolated workspace:

```powershell
./build/wgpu/RunTests.ps1 `
  -NativeLibrary C:/deps/wgpu_native.dll `
  -Lane Smoke -ResultsDirectory ../artifacts/webgpu-smoke

./build/wgpu/RunTests.ps1 `
  -NativeLibrary C:/deps/wgpu_native.dll `
  -Lane Resources -Backend Vulkan -SkipBuild `
  -ResultsDirectory ../artifacts/webgpu-resources
```

Use `DownloadNative.ps1 -OutputDirectory <cache>` to obtain the hash-verified,
pinned native release. `-Filter` accepts the usual `dotnet test` expression and
overrides the default Smoke/Resources selection. `-SkipBuild` requires matching
WebGPU binaries already built for the requested configuration. Runs are serial
within a checkout because test assets and output assemblies are shared.
`-Suite Stride.Physics.Tests` narrows a lane to a named member suite for a focused
retry; names outside that lane are rejected. `GPU` is kept in sync with the
standard GPU solution filter rather than maintaining a separate project list.

Hardware is the local default. `-Software` requests a native fallback adapter:
Vulkan uses the existing Lavapipe setup, D3D12 uses native WARP. If a fallback is
unavailable the lane fails; it does not silently pass on hardware. The D3D12
lane preloads the same pinned WARP package used by direct-D3D regression tests.
No cross-machine bitwise determinism is claimed.

Results include `selection.json` (requested coverage/configuration), `summary.md`, per-test `results.json`/`results.csv`, build logs,
TRX files and test logs. Each invocation writes test logs into a unique run
subdirectory so stale TRX files cannot turn an aborted run into a pass. Skipped
tests remain `NotExecuted`. A failed test containing `NotSupportedException` is
labelled `Unsupported` and still fails the lane. Other test errors, native
crashes/missing TRX, no matching tests and build failures remain failures. The
summary lists the first 100 non-passing results; JSON/CSV retain all results.
Hang detection is enabled at two minutes of test inactivity.
VSTest diagnostics, normal-verbosity test events and crash minidumps are captured
too. An aborted TRX remains a runner failure even when it contains failed tests;
it does not establish that the rest of the suite ran.

Image comparisons retain the existing tolerances and gold fallback rules. No
new reference images are generated or promoted. Screenshot paths distinguish
`Windows.WebGPU-Vulkan` and `Windows.WebGPU-D3D12`, including when both use the
same physical GPU. Missing gold is a test failure, not a rendering success.

The manual/reusable **Test Windows (WebGPU)** workflow defaults to the full GPU
lane, accepts Debug/Release configuration, and runs the selected lane on
both native backends with software rendering, fail-fast disabled and separate
uploaded artifacts. It publishes the same Markdown summary in GitHub Actions.
It does not add the experimental backend to the default PR gate. Editor UI
automation and browser WebGPU are outside these lanes.
