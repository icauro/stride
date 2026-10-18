param([Parameter(Mandatory)][string]$Naga)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'This fixture currently prepares its shader assets with Windows x64 desktop tools.'
}
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$nagaPath = (Resolve-Path -LiteralPath $Naga).Path
$engine = Join-Path $checkout 'sources/engine/Stride.Graphics.WebGPU.Tests'
$tool = Join-Path $checkout 'build/wgpu/ShaderPackageTool'
$output = Join-Path $PSScriptRoot 'ManagedWebGpuProbe/obj/effects'
Push-Location $checkout
try {
    # These are desktop asset-preparation processes, separate from the WASM graph.
    $hostProperties = @('-p:StridePlatforms=Windows', '-p:StridePlatform=Windows', '-p:TargetFramework=net10.0', '-p:StrideSkipAutoPack=true', '-p:StrideAssemblyProcessorFramework=net10.0')
    & dotnet build "$engine/Stride.Graphics.WebGPU.Tests.csproj" -m:1 -p:StrideGraphicsApi=WebGPU -p:StrideGraphicsApiDependentBuildAll=false @hostProperties --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Desktop shader export build failed.' }
    & dotnet build $tool @hostProperties --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Offline WGSL tool build failed.' }
    & dotnet "$engine/bin/Debug/net10.0/EngineValidation.dll" --export-wgsl "$output/native"
    if ($LASTEXITCODE -ne 0) { throw 'SDSL effect export failed.' }
    New-Item -ItemType Directory -Path "$output/portable" -Force | Out-Null
    foreach ($effect in @('WebGpuValidation', 'WebGpuCompute')) {
        & dotnet "$tool/bin/Debug/net10.0/ShaderPackageTool.dll" $nagaPath "$output/native/$effect.effect" "$output/portable/$effect.effect"
        if ($LASTEXITCODE -ne 0) { throw "Offline WGSL conversion failed: $effect" }
    }
} finally { Pop-Location }
