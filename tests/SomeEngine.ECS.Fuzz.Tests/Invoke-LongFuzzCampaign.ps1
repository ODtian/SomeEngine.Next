[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0[xX][0-9A-Fa-f]{1,16}|[0-9]{1,20})$')]
    [string] $Seed,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1000000)]
    [int] $Steps,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Evidence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $repositoryRoot

$evidencePath = if ([IO.Path]::IsPathRooted($Evidence)) {
    [IO.Path]::GetFullPath($Evidence)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Evidence))
}
$relativeEvidence = [IO.Path]::GetRelativePath($repositoryRoot, $evidencePath)
$evidenceInsideRepository = -not [IO.Path]::IsPathRooted($relativeEvidence) -and
    -not $relativeEvidence.StartsWith('..' + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -and
    $relativeEvidence -ne '..'
if ($evidenceInsideRepository) {
    & git ls-files --error-unmatch -- $relativeEvidence *> $null
    if ($LASTEXITCODE -eq 0) {
        throw 'Long-fuzz evidence must not overwrite a tracked source file.'
    }
}

$evidenceDirectory = [IO.Path]::GetDirectoryName($evidencePath)
if ($evidenceDirectory) {
    [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
}
$candidatePath = Join-Path $evidenceDirectory (
    '.' + [IO.Path]::GetFileName($evidencePath) + '.candidate.' + [Guid]::NewGuid().ToString('N'))

try {
    $savedSeed = $env:SOMEENGINE_ECS_FUZZ_SEED
    $savedSteps = $env:SOMEENGINE_ECS_FUZZ_STEPS
    $savedEvidence = $env:SOMEENGINE_ECS_FUZZ_EVIDENCE
    try {
        $env:SOMEENGINE_ECS_FUZZ_SEED = $Seed
        $env:SOMEENGINE_ECS_FUZZ_STEPS = $Steps.ToString([Globalization.CultureInfo]::InvariantCulture)
        $env:SOMEENGINE_ECS_FUZZ_EVIDENCE = $candidatePath
        & dotnet test tests/SomeEngine.ECS.Fuzz.Tests/SomeEngine.ECS.Fuzz.Tests.csproj `
            -c Release `
            --filter 'FullyQualifiedName=SomeEngine.ECS.Fuzz.Tests.EcsFuzzTests.EnvironmentCampaign_ReplaysRequestedSeedWhenConfigured'
        if ($LASTEXITCODE -ne 0) {
            throw "Long-fuzz campaign failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        $env:SOMEENGINE_ECS_FUZZ_SEED = $savedSeed
        $env:SOMEENGINE_ECS_FUZZ_STEPS = $savedSteps
        $env:SOMEENGINE_ECS_FUZZ_EVIDENCE = $savedEvidence
    }

    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
        throw 'The campaign test succeeded without emitting its required evidence artifact.'
    }
    [IO.File]::Move($candidatePath, $evidencePath, $true)
    Write-Host "Long-fuzz evidence written to $evidencePath"
}
finally {
    if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
        Remove-Item -LiteralPath $candidatePath -Force
    }
}
