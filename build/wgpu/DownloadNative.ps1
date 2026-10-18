param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'headers/manifest.json') -Raw | ConvertFrom-Json
$output = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "wgpu-native-$($manifest.release)-win-x64"))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive = Join-Path $output $manifest.archive
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/gfx-rs/wgpu-native/releases/download/$($manifest.release)/$($manifest.archive)" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.archiveSha256) { throw 'Native archive hash does not match the pinned manifest.' }
$extracted = Join-Path $output 'extracted'
Expand-Archive -LiteralPath $archive -DestinationPath $extracted -Force
foreach ($header in $manifest.headers.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Join-Path $extracted "include/webgpu/$($header.Name)") -Algorithm SHA256).Hash.ToLowerInvariant() -ne $header.Value) { throw "Native archive header mismatch: $($header.Name)" }
}
Write-Output (Join-Path $extracted 'lib/wgpu_native.dll')
