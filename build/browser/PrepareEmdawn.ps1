param()
$ErrorActionPreference = 'Stop'
$release = 'v20260423.175430'
$expectedHash = '42784f70b67197c614322f9fabb0f1dc64228a0de10e88f99941fa9d29bee9ad6683f4651d4eefd5a7a9fbd1f976eb522b190b683219ed1793e9b531c602ffa6'
$destination = Join-Path $PSScriptRoot 'CWebGpuProbe/obj/emdawnwebgpu'
$archive = Join-Path $destination "emdawnwebgpu_pkg-$release.zip"
New-Item -ItemType Directory -Force -Path $destination | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/google/dawn/releases/download/$release/emdawnwebgpu_pkg-$release.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'Emdawn archive hash mismatch. Inspect/remove the invalid archive before retrying.'
}
# Re-extract verified inputs so an edited cached port cannot silently be reused.
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
Write-Output "Prepared hash-verified Emdawnwebgpu $release in $destination"
