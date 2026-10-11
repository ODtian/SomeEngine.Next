param(
    [ValidateRange(3, 301)] [int]$Samples = 31,
    [ValidateSet('shader', 'mesh', 'texture', 'audio', 'document', 'network', 'small', 'one-megabyte', 'mixed', 'versions', 'churn')]
    [string[]]$Cases = @('shader', 'mesh', 'texture', 'audio', 'document', 'network', 'small', 'one-megabyte', 'mixed', 'versions', 'churn'),
    [ValidateSet('array', 'segments', 'native', 'native-reuse')]
    [string[]]$Kinds = @('array', 'segments', 'native', 'native-reuse'),
    [ValidateSet('CoreCLR', 'Satori')]
    [string[]]$Collectors = @('CoreCLR', 'Satori'),
    [ValidateSet('interactive', 'low')]
    [string]$Latency = 'low',
    [string]$OutputPath,
    [switch]$KeepBuild
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repository 'tools/SomeEngine.Assets.Benchmarks/SomeEngine.Assets.Benchmarks.csproj'
$workRoot = Join-Path $repository 'obj/AssetStorageMeasurements'
$coreDirectory = Join-Path $workRoot 'coreclr'
$satoriDirectory = Join-Path $workRoot 'satori'

if (![Environment]::Is64BitOperatingSystem -or ![OperatingSystem]::IsWindows()) {
    throw 'This measurement runner publishes Windows x64 applications.'
}

function Reset-MeasurementDirectory([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedRoot = [IO.Path]::GetFullPath($workRoot) + [IO.Path]::DirectorySeparatorChar
    if (!$fullPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a directory outside $allowedRoot"
    }
    if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
    New-Item -ItemType Directory -Path $fullPath | Out-Null
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
foreach ($collector in $Collectors) {
    $directory = if ($collector -eq 'Satori') { $satoriDirectory } else { $coreDirectory }
    $useSatori = if ($collector -eq 'Satori') { 'true' } else { 'false' }
    Reset-MeasurementDirectory $directory
    Write-Host "Publishing an isolated $collector self-contained benchmark..."
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:UseSatoriGC=$useSatori -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $directory -v quiet
    if ($LASTEXITCODE -ne 0) { throw "$collector publish failed with exit code $LASTEXITCODE" }
}

$resultPath = $null
if ($OutputPath) {
    $resultPath = [IO.Path]::GetFullPath((Join-Path $repository $OutputPath))
    $resultDirectory = [IO.Path]::GetDirectoryName($resultPath)
    if ($resultDirectory) { New-Item -ItemType Directory -Force -Path $resultDirectory | Out-Null }
    [IO.File]::WriteAllText($resultPath, '')
}

$checksums = @{}
try {
    foreach ($caseName in $Cases) {
        foreach ($kind in $Kinds) {
            foreach ($collector in $Collectors) {
                $directory = if ($collector -eq 'Satori') { $satoriDirectory } else { $coreDirectory }
                $application = Join-Path $directory 'SomeEngine.Assets.Benchmarks.exe'
                if (!(Test-Path -LiteralPath $application)) { throw "Missing benchmark executable: $application" }
                Write-Host "Measuring $collector/$Latency $caseName $kind..."
                $lines = @(& $application storage --kind $kind --case $caseName --root $repository `
                    --samples $Samples --gc $collector --latency $Latency)
                if ($LASTEXITCODE -ne 0) { throw "$collector/$caseName/$kind failed with exit code $LASTEXITCODE" }
                $json = $lines | Where-Object { $_ -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
                if (!$json) { throw "$collector/$caseName/$kind did not emit JSON." }
                $result = $json | ConvertFrom-Json
                if ($collector -eq 'Satori' -and $result.gcEvidence -eq 'large-object-generation=2') {
                    throw 'The Satori executable appears to be using the default GC.'
                }
                if ($checksums.ContainsKey($caseName) -and $checksums[$caseName] -ne $result.checksum) {
                    throw "Payload checksum mismatch for $caseName."
                }
                $checksums[$caseName] = $result.checksum
                if ($resultPath) { [IO.File]::AppendAllText($resultPath, $json + [Environment]::NewLine) }
                Write-Output $json
            }
        }
    }
} finally {
    if (!$KeepBuild) {
        if (Test-Path -LiteralPath $coreDirectory) { Remove-Item -LiteralPath $coreDirectory -Recurse -Force }
        if (Test-Path -LiteralPath $satoriDirectory) { Remove-Item -LiteralPath $satoriDirectory -Recurse -Force }
    }
}

if ($resultPath) { Write-Host "Results: $resultPath" }
