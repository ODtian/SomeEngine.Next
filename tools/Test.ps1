[CmdletBinding()]
param(
    [ValidateSet('Software', 'Hardware', 'LongFuzz', 'Performance')]
    [string] $Category = 'Software',
    [string] $Project = 'SomeEngine.slnx',
    [string] $Configuration = 'Release',
    [string] $ResultsDirectory = 'artifacts/test-results',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $repository
try {
    $results = [IO.Path]::GetFullPath($ResultsDirectory)
    [IO.Directory]::CreateDirectory($results) | Out-Null
    if (!$NoBuild -and $Project -eq 'SomeEngine.slnx') {
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -ResultsDirectory $results
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        $NoBuild = $true
    }
    $arguments = @('test', $Project, '-c', $Configuration, '-maxcpucount:2',
        '--logger', 'trx', '--results-directory', $results,
        '--blame-crash', '--blame-hang', '--blame-hang-timeout', '5m')
    if ($NoBuild) { $arguments += '--no-build' }
    # Software uses Directory.Build.targets, the same default as plain dotnet test.
    if ($Category -ne 'Software') { $arguments += @('--filter', "Category=$Category") }
    $previousArtifacts = $env:SOMEENGINE_TEST_ARTIFACTS
    try {
        $env:SOMEENGINE_TEST_ARTIFACTS = $results
        & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $results "$Category.log")
        $testExitCode = $LASTEXITCODE
    }
    finally { $env:SOMEENGINE_TEST_ARTIFACTS = $previousArtifacts }
    exit $testExitCode
}
finally { Pop-Location }
