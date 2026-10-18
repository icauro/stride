param(
    [Parameter(Mandatory)][string]$NativeLibrary,
    [ValidateSet('Smoke', 'Resources', 'GPU', 'Graphics', 'Engine', 'Physics', 'UI', 'Particles', 'All')][string]$Lane = 'Smoke',
    [ValidateSet('Vulkan', 'D3D12')][string[]]$Backend = @('Vulkan', 'D3D12'),
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [string]$Filter,
    [string[]]$Suite,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Software,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'These WebGPU lanes currently target Windows x64.' }
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$nativePath = (Resolve-Path -LiteralPath $NativeLibrary).Path
$results = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $results -Force | Out-Null
$gpuSolution = Get-Content -LiteralPath (Join-Path $checkout 'build/Stride.Tests.Game.GPU.slnf') -Raw | ConvertFrom-Json
$solutionDirectory = Split-Path ([IO.Path]::GetFullPath((Join-Path "$checkout/build" $gpuSolution.solution.path)))
$gpuProjects = @{}
$gpuSuites = @($gpuSolution.solution.projects | ForEach-Object {
    $path = [IO.Path]::GetFullPath((Join-Path $solutionDirectory $_.Replace('\', '/')))
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    if ($gpuProjects.ContainsKey($name)) { throw "Duplicate GPU suite name: $name" }
    $gpuProjects[$name] = $path
    $name
})
$suites = switch ($Lane) {
    Smoke { 'Stride.Graphics.Tests' }
    Resources { 'Stride.Graphics.Tests' }
    Graphics { 'Stride.Graphics.Tests'; 'Stride.Graphics.Tests.10_0'; 'Stride.Graphics.Tests.11_0' }
    Engine { 'Stride.Engine.Tests' }
    Physics { 'Stride.Physics.Tests' }
    UI { 'Stride.UI.Tests' }
    Particles { 'Stride.Particles.Tests' }
    GPU { $gpuSuites }
    All { $gpuSuites; 'Stride.Particles.Tests' }
}
if ($Suite) {
    foreach ($selected in $Suite) {
        if ($selected -notin $suites) { throw "Suite $selected is not part of lane $Lane." }
    }
    $suites = @($suites | Where-Object { $_ -in $Suite })
}
if (-not $Filter -and $Lane -eq 'Smoke') {
    $Filter = 'FullyQualifiedName=Stride.Graphics.Tests.TestTexture.TestCalculateMipMapCount|FullyQualifiedName=Stride.Graphics.Tests.TestTexture.TestTexture2D|FullyQualifiedName=Stride.Graphics.Tests.TestTexture.TestTexture2DArray'
}
if (-not $Filter -and $Lane -eq 'Resources') {
    $Filter = 'FullyQualifiedName~Stride.Graphics.Tests.TestTexture.TestTexture|FullyQualifiedName~Stride.Graphics.Tests.TestDeviceLoss'
}
$properties = @("-p:Configuration=$Configuration", '-p:StridePlatforms=Windows', '-p:StrideGraphicsApi=WebGPU', '-p:StrideGraphicsApis=WebGPU', '-p:StrideGraphicsApiDependentBuildAll=false', '-p:StrideSkipAutoPack=true', '-p:StrideAssemblyProcessorFramework=net10.0')
$names = @('STRIDE_GRAPHICS_API', 'STRIDE_WEBGPU_NATIVE_LIBRARY', 'STRIDE_WEBGPU_BACKEND', 'STRIDE_TESTS_GPU', 'STRIDE_GRAPHICS_SOFTWARE_RENDERING', 'STRIDE_TESTS_RENDERDOC', 'STRIDE_CRASH_MODE', 'STRIDE_CRASH_DIR')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$rows = [Collections.Generic.List[object]]::new()
$runId = [Guid]::NewGuid().ToString('N')
[pscustomobject]@{
    RunId = $runId; Lane = $Lane; Suites = @($suites); Backends = @($Backend)
    Filter = $Filter; Configuration = $Configuration; Software = [bool]$Software; SkipBuild = [bool]$SkipBuild
    StandardGpuSuites = $gpuSuites; StartedUtc = [DateTime]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $results 'selection.json')
try {
    $env:STRIDE_GRAPHICS_API = 'WebGPU'
    $env:STRIDE_WEBGPU_NATIVE_LIBRARY = $nativePath
    $env:STRIDE_TESTS_GPU = if ($Software) { '0' } else { '1' }
    $env:STRIDE_GRAPHICS_SOFTWARE_RENDERING = if ($Software) { '1' } else { '0' }
    $env:STRIDE_TESTS_RENDERDOC = '0'
    $env:STRIDE_CRASH_MODE = 'save'
    $env:STRIDE_CRASH_DIR = Join-Path $results 'crashes'
    foreach ($suiteName in $suites) {
        $project = if ($gpuProjects.ContainsKey($suiteName)) { $gpuProjects[$suiteName] } else { Join-Path $checkout "sources/engine/$suiteName/$suiteName.csproj" }
        $buildExit = 0
        if (-not $SkipBuild) {
            Write-Output "Building $suiteName ($Configuration, WebGPU)..."
            # Asset compilation can create a device; select a backend before building too.
            $env:STRIDE_WEBGPU_BACKEND = $Backend[0]
            & dotnet build $project -m:1 -nr:false --nologo -v:m @properties *> (Join-Path $results "$suiteName-build.log")
            $buildExit = $LASTEXITCODE
            Write-Output "Build $suiteName exit: $buildExit"
        }
        foreach ($nativeBackend in $Backend) {
            $runDirectory = Join-Path $results "$runId/$Lane-$nativeBackend/$suiteName"
            New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
            if ($buildExit -ne 0) {
                $rows.Add([pscustomobject]@{ Suite=$suiteName; Backend=$nativeBackend; Test='(build)'; Outcome='BuildFailed'; Detail="Exit $buildExit; see $suiteName-build.log" })
                continue
            }
            $env:STRIDE_WEBGPU_BACKEND = $nativeBackend
            Write-Output "Testing $suiteName / $nativeBackend..."
            $filterArguments = if ($Filter) { @('--filter', $Filter) } else { @() }
            & dotnet test $project --no-build --no-restore @properties @filterArguments --logger 'trx;LogFileName=results.trx' --logger 'console;verbosity=normal' --diag (Join-Path $runDirectory 'vstest.log') --results-directory $runDirectory --blame-crash --blame-crash-dump-type mini --blame-hang-timeout 2m --blame-hang-dump-type mini -- RunConfiguration.MaxCpuCount=1 *> (Join-Path $runDirectory 'test.log')
            $testExit = $LASTEXITCODE
            Write-Output "Test $suiteName / $nativeBackend exit: $testExit"
            $trxPath = Join-Path $runDirectory 'results.trx'
            if (-not (Test-Path -LiteralPath $trxPath)) {
                $rows.Add([pscustomobject]@{ Suite=$suiteName; Backend=$nativeBackend; Test='(runner)'; Outcome='RunnerFailed'; Detail="No TRX; exit $testExit; see test.log" })
                continue
            }
            [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
            $tests = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
            if ($tests.Count -eq 0) {
                $rows.Add([pscustomobject]@{ Suite=$suiteName; Backend=$nativeBackend; Test='(discovery)'; Outcome='NoTests'; Detail='Filter matched no executed tests' })
            }
            foreach ($test in $tests) {
                $detail = $test.SelectSingleNode("./*[local-name()='Output']/*[local-name()='ErrorInfo']/*[local-name()='Message']")
                $message = if ($detail) { $detail.InnerText } else { '' }
                $outcome = [string]$test.outcome
                if ($outcome -eq 'Failed' -and $message -match 'NotSupportedException') { $outcome = 'Unsupported' }
                $rows.Add([pscustomobject]@{ Suite=$suiteName; Backend=$nativeBackend; Test=[string]$test.testName; Outcome=$outcome; Detail=$message })
            }
            $aborted = $trx.SelectSingleNode("//*[local-name()='ResultSummary']").outcome -eq 'Aborted'
            if ($aborted -or ($testExit -ne 0 -and -not ($tests | Where-Object outcome -eq 'Failed'))) {
                $diagnostic = if ($aborted) { "Test run aborted; exit $testExit; coverage may be incomplete; see test.log" } else { "Exit $testExit without a reported failed test; see test.log" }
                $rows.Add([pscustomobject]@{ Suite=$suiteName; Backend=$nativeBackend; Test='(runner)'; Outcome='RunnerFailed'; Detail=$diagnostic })
            }
        }
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    $rows | ConvertTo-Json -Depth 5 -AsArray | Set-Content -LiteralPath (Join-Path $results 'results.json')
    $rows | Export-Csv -LiteralPath (Join-Path $results 'results.csv') -NoTypeInformation
    $summary = @("# WebGPU $Lane", '', '| Suite | Native backend | Outcome | Count |', '| --- | --- | --- | ---: |')
    foreach ($group in ($rows | Group-Object Suite,Backend,Outcome)) {
        $row = $group.Group[0]
        $summary += "| $($row.Suite) | $($row.Backend) | $($row.Outcome) | $($group.Count) |"
    }
    $problems = @($rows | Where-Object Outcome -ne 'Passed')
    if ($problems.Count) {
        $summary += @('', '| Backend | Test | Outcome | First diagnostic |', '| --- | --- | --- | --- |')
        foreach ($row in ($problems | Select-Object -First 100)) {
            $diagnostic = (($row.Detail -split '\r?\n' | Where-Object { $_.Trim() }) -join ' ').Replace('|', '\|')
            if ($diagnostic.Length -gt 240) { $diagnostic = $diagnostic.Substring(0, 240) + '…' }
            $testName = $row.Test.Replace('|', '\|')
            $summary += "| $($row.Backend) | $testName | $($row.Outcome) | $diagnostic |"
        }
    }
    $summary += @('', 'Unsupported is a failed test throwing NotSupportedException, not an accepted skip. Native crashes, missing TRX, empty filters and build failures remain failures. See results.json and each test.log/TRX for details. No gold images are updated.')
    $summary | Set-Content -LiteralPath (Join-Path $results 'summary.md')
    $summary | Write-Output
}
if ($rows.Count -eq 0 -or ($rows | Where-Object Outcome -notin 'Passed','NotExecuted')) { throw 'WebGPU lane has failures; see summary.md and results.json.' }
