param(
    [Parameter(Mandatory)][string]$NativeLibrary,
    [ValidateSet('Vulkan', 'D3D12')][string[]]$Backend = @('Vulkan', 'D3D12'),
    [switch]$Window,
    [switch]$Game
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Engine validation currently requires Windows x64.'
}
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$nativePath = (Resolve-Path -LiteralPath $NativeLibrary).Path
$project = Join-Path $checkout 'sources/engine/Stride.Graphics.WebGPU.Tests/Stride.Graphics.WebGPU.Tests.csproj'
Push-Location $checkout
$oldNative = $env:STRIDE_WEBGPU_NATIVE_LIBRARY
$oldBackend = $env:STRIDE_WEBGPU_BACKEND
try {
    & dotnet build $project -m:1 -p:StrideGraphicsApi=WebGPU -p:StrideGraphicsApiDependentBuildAll=false -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0 -p:StridePlatform=Windows -p:TargetFramework=net10.0 --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'WebGPU engine validation build failed.' }
    if ($Game) {
        & dotnet build (Join-Path $checkout 'sources/engine/Stride.Games.WebGPU.Tests/Stride.Games.WebGPU.Tests.csproj') -m:1 -p:StrideGraphicsApi=WebGPU -p:StrideGraphicsApiDependentBuildAll=false -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0 -p:StridePlatform=Windows -p:TargetFramework=net10.0 --nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw 'WebGPU game validation build failed.' }
    }
    $env:STRIDE_WEBGPU_NATIVE_LIBRARY = $nativePath
    $arguments = @()
    if ($Window) { $arguments += '--window' }
    foreach ($nativeBackend in $Backend) {
        $env:STRIDE_WEBGPU_BACKEND = $nativeBackend
        Write-Output "Validating Stride WebGPU through $nativeBackend"
        & dotnet (Join-Path $checkout 'sources/engine/Stride.Graphics.WebGPU.Tests/bin/Debug/net10.0/EngineValidation.dll') @arguments
        if ($LASTEXITCODE -ne 0) { throw "Stride WebGPU validation failed through $nativeBackend." }
        & dotnet (Join-Path $checkout 'sources/engine/Stride.Graphics.WebGPU.Tests/bin/Debug/net10.0/EngineValidation.dll') --portable
        if ($LASTEXITCODE -ne 0) { throw "Portable Stride WebGPU startup validation failed through $nativeBackend." }
        if ($Game) {
            $gameDll = Join-Path $checkout 'sources/engine/Stride.Games.WebGPU.Tests/bin/Debug/net10.0/GameValidation.dll'
            & dotnet $gameDll
            if ($LASTEXITCODE -ne 0) { throw "Stride headless game validation failed through $nativeBackend." }
            if ($Window) {
                & dotnet $gameDll --window
                if ($LASTEXITCODE -ne 0) { throw "Stride SDL game validation failed through $nativeBackend." }
            }
            & dotnet $gameDll --scene
            if ($LASTEXITCODE -ne 0) { throw "Stride forward-renderer scene validation failed through $nativeBackend." }
            if ($Window) {
                & dotnet $gameDll --window --scene
                if ($LASTEXITCODE -ne 0) { throw "Stride SDL forward-renderer scene validation failed through $nativeBackend." }
            }
            & dotnet $gameDll --shadows
            if ($LASTEXITCODE -ne 0) { throw "Stride directional-shadow scene validation failed through $nativeBackend." }
            & dotnet $gameDll --lightstreak
            if ($LASTEXITCODE -ne 0) { throw "Stride LightStreak postprocessing validation failed through $nativeBackend." }
            & dotnet $gameDll --posteffects --tonemap-only
            if ($LASTEXITCODE -ne 0) { throw "Stride HDR tone mapping validation failed through $nativeBackend." }
            & dotnet $gameDll --posteffects
            if ($LASTEXITCODE -ne 0) { throw "Stride HDR postprocessing validation failed through $nativeBackend." }
            if ($Window) {
                & dotnet $gameDll --window --lightstreak
                if ($LASTEXITCODE -ne 0) { throw "Stride SDL LightStreak postprocessing validation failed through $nativeBackend." }
                & dotnet $gameDll --window --posteffects
                if ($LASTEXITCODE -ne 0) { throw "Stride SDL HDR postprocessing validation failed through $nativeBackend." }
            }
            if ($Window) {
                & dotnet $gameDll --window --shadows
                if ($LASTEXITCODE -ne 0) { throw "Stride SDL directional-shadow scene validation failed through $nativeBackend." }
            }
        }
    }
} finally {
    $env:STRIDE_WEBGPU_NATIVE_LIBRARY = $oldNative
    $env:STRIDE_WEBGPU_BACKEND = $oldBackend
    Pop-Location
}
