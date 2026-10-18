param(
    [Parameter(Mandatory)][string]$NativeLibrary,
    [Parameter(Mandatory)][string]$Naga,
    [ValidateSet('Vulkan', 'D3D12')][string[]]$Backend = @('Vulkan', 'D3D12')
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'This native WGSL validation lane currently requires Windows x64.'
}
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$nativePath = (Resolve-Path -LiteralPath $NativeLibrary).Path
$nagaPath = (Resolve-Path -LiteralPath $Naga).Path
$engine = Join-Path $checkout 'sources/engine/Stride.Graphics.WebGPU.Tests'
$tool = Join-Path $PSScriptRoot 'ShaderPackageTool'
$output = Join-Path $tool 'obj/validation'
Push-Location $checkout
$oldNative = $env:STRIDE_WEBGPU_NATIVE_LIBRARY
$oldBackend = $env:STRIDE_WEBGPU_BACKEND
try {
    & dotnet build "$engine/Stride.Graphics.WebGPU.Tests.csproj" -m:1 -p:StrideGraphicsApi=WebGPU -p:StrideGraphicsApiDependentBuildAll=false -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0 -p:StridePlatform=Windows -p:TargetFramework=net10.0 --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'WGSL engine validation build failed.' }
    & dotnet build $tool -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0 --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Offline WGSL tool build failed.' }
    & dotnet "$engine/bin/Debug/net10.0/EngineValidation.dll" --export-wgsl "$output/native"
    if ($LASTEXITCODE -ne 0) { throw 'SDSL effect export failed.' }
    New-Item -ItemType Directory -Path "$output/portable" -Force | Out-Null
    foreach ($effect in @('WebGpuValidation', 'WebGpuCompute')) {
        & dotnet "$tool/bin/Debug/net10.0/ShaderPackageTool.dll" $nagaPath "$output/native/$effect.effect" "$output/portable/$effect.effect"
        if ($LASTEXITCODE -ne 0) { throw "WGSL conversion failed for $effect." }
    }
    $env:STRIDE_WEBGPU_NATIVE_LIBRARY = $nativePath
    foreach ($gpuBackend in $Backend) {
        $env:STRIDE_WEBGPU_BACKEND = $gpuBackend
        Write-Output "Validating packaged WGSL on $gpuBackend"
        & dotnet "$engine/bin/Debug/net10.0/EngineValidation.dll" --wgsl "$output/portable"
        if ($LASTEXITCODE -ne 0) { throw "WGSL shader validation failed on $gpuBackend." }
    }
} finally {
    $env:STRIDE_WEBGPU_NATIVE_LIBRARY = $oldNative
    $env:STRIDE_WEBGPU_BACKEND = $oldBackend
    Pop-Location
}
