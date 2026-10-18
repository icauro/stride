param()
$ErrorActionPreference = 'Stop'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tool = Join-Path $PSScriptRoot 'ContentPackageTool'
$output = Join-Path $PSScriptRoot 'ManagedWebGpuProbe/obj/content'
Push-Location $checkout
try {
    & dotnet build $tool -m:1 -c Release --nologo -v:q -p:StridePlatforms=Windows -p:StridePlatform=Windows -p:StrideGraphicsApi=WebGPU -p:StrideGraphicsApiDependentBuildAll=false -p:TargetFramework=net10.0 -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0
    if ($LASTEXITCODE -ne 0) { throw 'Content package tool build failed.' }
    & dotnet "$tool/bin/Release/net10.0/ContentPackageTool.dll" --fixture "$output/native"
    if ($LASTEXITCODE -ne 0) { throw 'Content fixture serialization failed.' }
    & dotnet "$tool/bin/Release/net10.0/ContentPackageTool.dll" "$output/native" "$output/web"
    if ($LASTEXITCODE -ne 0) { throw 'Content packaging failed.' }
} finally { Pop-Location }
