param([string]$NativeLibrary)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'The current ABI validation runner requires Windows x64 and Visual Studio C++ tools.' }
& (Join-Path $PSScriptRoot 'Generate.ps1') -Check
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tests = Join-Path $checkout 'sources/engine/Stride.Graphics.WebGPU.Bindings.Tests'
& dotnet build $tests --nologo -p:StrideSkipAutoPack=true -p:StrideAssemblyProcessorFramework=net10.0
if ($LASTEXITCODE -ne 0) { throw 'Bindings validation build failed.' }
$probe = Join-Path $tests 'obj/abi-probe.c'
$exe = Join-Path $tests 'obj/abi-probe.exe'
$object = Join-Path $tests 'obj/abi-probe.obj'
$results = Join-Path $tests 'obj/abi-probe.txt'
& dotnet run --no-build --project $tests -- --write-probe $probe
if ($LASTEXITCODE -ne 0) { throw 'C ABI probe generation failed.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $visualStudio) { throw 'Visual Studio C++ tools not found.' }
$tools = Get-ChildItem -LiteralPath (Join-Path $visualStudio 'VC/Tools/MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdkVersion = (Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name
$oldInclude = $env:INCLUDE; $oldLib = $env:LIB
try {
    $env:INCLUDE = "$($tools.FullName)/include;$sdk/Include/$sdkVersion/ucrt;$sdk/Include/$sdkVersion/shared;$sdk/Include/$sdkVersion/um"
    $env:LIB = "$($tools.FullName)/lib/x64;$sdk/Lib/$sdkVersion/ucrt/x64;$sdk/Lib/$sdkVersion/um/x64"
    & (Join-Path $tools.FullName 'bin/Hostx64/x64/cl.exe') /nologo /TC /std:c11 "/I$(Join-Path $PSScriptRoot 'headers')" $probe "/Fe$exe" "/Fo$object"
    if ($LASTEXITCODE -ne 0) { throw 'C ABI probe compilation failed.' }
} finally { $env:INCLUDE = $oldInclude; $env:LIB = $oldLib }
& $exe | Set-Content -LiteralPath $results -Encoding ascii
if ($LASTEXITCODE -ne 0) { throw 'C ABI probe failed.' }
& dotnet run --no-build --project $tests -- --abi $results
if ($LASTEXITCODE -ne 0) { throw 'ABI validation failed.' }
if ($NativeLibrary) {
    & dotnet run --no-build --project $tests -- --native ([IO.Path]::GetFullPath($NativeLibrary))
    if ($LASTEXITCODE -ne 0) { throw 'Native smoke validation failed.' }
    & dotnet run --no-build --project $tests -- --context ([IO.Path]::GetFullPath($NativeLibrary))
    if ($LASTEXITCODE -ne 0) { throw 'WebGPU context/render validation failed.' }
    & dotnet run --no-build --project $tests -- --async-context ([IO.Path]::GetFullPath($NativeLibrary))
    if ($LASTEXITCODE -ne 0) { throw 'Portable async WebGPU context/readback validation failed.' }
} else { Write-Output 'Native GPU validation not run; supply -NativeLibrary to enable it.' }
