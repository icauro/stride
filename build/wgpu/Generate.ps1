param([switch]$Check)
$ErrorActionPreference = 'Stop'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$generatorArgs = @('run', '--project', (Join-Path $PSScriptRoot 'Generator'), '--', (Join-Path $PSScriptRoot 'headers'), (Join-Path $checkout 'sources/engine/Stride.Graphics.WebGPU.Bindings/Generated'))
if ($Check) { $generatorArgs += '--check' }
& dotnet @generatorArgs
if ($LASTEXITCODE -ne 0) { throw 'WebGPU generation failed.' }
