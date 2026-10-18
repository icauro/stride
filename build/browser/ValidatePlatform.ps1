param()
$ErrorActionPreference = 'Stop'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $checkout 'sources/core/Stride.Core/Stride.Core.csproj'
$properties = 'StridePlatform,StridePlatformDefines,StrideGraphicsApi,StrideUI,TargetFrameworks,StridePlatformDeps'
$cases = @(
    @{ Name = 'browser-platform'; Args = @('-p:StridePlatforms=Browser', '-p:TargetFramework=net11.0'); Platform = 'Browser'; Frameworks = 'net11.0'; Define = 'STRIDE_PLATFORM_BROWSER'; UI = 'BROWSER' },
    @{ Name = 'browser-rid'; Args = @('-p:RuntimeIdentifier=browser-wasm', '-p:TargetFramework=net11.0'); Platform = 'Browser'; Frameworks = 'net11.0'; Define = 'STRIDE_PLATFORM_BROWSER'; UI = 'BROWSER' },
    @{ Name = 'mixed-browser'; Args = @('-p:StridePlatforms=Windows%3BBrowser', '-p:TargetFramework=net11.0'); Platform = 'Browser'; Frameworks = 'net10.0;net11.0'; Define = 'STRIDE_PLATFORM_BROWSER'; UI = 'BROWSER' },
    @{ Name = 'mixed-desktop'; Args = @('-p:StridePlatforms=Windows%3BBrowser', '-p:TargetFramework=net10.0'); Platform = 'Windows'; Frameworks = 'net10.0;net11.0'; Define = 'STRIDE_PLATFORM_DESKTOP'; UI = 'SDL' },
    @{ Name = 'desktop'; Args = @('-p:StridePlatforms=Windows', '-p:TargetFramework=net10.0'); Platform = 'Windows'; Frameworks = 'net10.0'; Define = 'STRIDE_PLATFORM_DESKTOP'; UI = 'SDL' }
)
foreach ($case in $cases) {
    $evaluationArgs = @('msbuild', $project, '-nologo', "-getProperty:$properties") + $case.Args
    $output = & dotnet @evaluationArgs
    if ($LASTEXITCODE -ne 0) { throw "Evaluation failed: $($case.Name)" }
    $actual = ($output -join "`n" | ConvertFrom-Json).Properties
    if ($actual.StridePlatform -ne $case.Platform -or $actual.TargetFrameworks -ne $case.Frameworks -or $actual.StridePlatformDefines -ne $case.Define -or $actual.StrideUI -ne $case.UI) {
        throw "Unexpected platform selection for $($case.Name): $($actual | ConvertTo-Json -Compress)"
    }
    if ($case.Platform -eq 'Browser' -and ($actual.StrideGraphicsApi -ne 'WebGPU' -or $actual.StridePlatformDeps -ne 'browser-wasm')) {
        throw "Unexpected browser graphics/dependencies: $($actual | ConvertTo-Json -Compress)"
    }
    Write-Output "PASS: $($case.Name) selects $($actual.StridePlatform), $($actual.StridePlatformDefines), $($actual.StrideUI)."
}
