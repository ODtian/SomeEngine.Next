[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0[xX][0-9A-Fa-f]{1,16}|[0-9]{1,20})$')]
    [string] $Seed,

    [Parameter(Mandatory = $true)]
    [ValidateRange(10000, 1000000)]
    [int] $Steps,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Evidence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $repositoryRoot

$initialStatus = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the Git worktree.'
}
if ($initialStatus.Count -ne 0) {
    throw "Long-fuzz evidence requires a clean tracked and untracked worktree.`n$($initialStatus -join [Environment]::NewLine)"
}

$commitSha = (& git rev-parse --verify HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $commitSha -notmatch '^[0-9a-f]{40,64}$') {
    throw 'Unable to capture a full Git commit SHA.'
}

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
    & git check-ignore -q -- $relativeEvidence
    if ($LASTEXITCODE -ne 0) {
        throw 'Long-fuzz evidence inside the repository must be covered by .gitignore.'
    }
}

$evidenceDirectory = [IO.Path]::GetDirectoryName($evidencePath)
if ($evidenceDirectory) {
    [IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
}
$candidatePath = Join-Path $evidenceDirectory (
    '.' + [IO.Path]::GetFileName($evidencePath) + '.candidate.' + [Guid]::NewGuid().ToString('N'))
$relativeCandidate = [IO.Path]::GetRelativePath($repositoryRoot, $candidatePath)
$relativeCandidateGit = $relativeCandidate.Replace([IO.Path]::DirectorySeparatorChar, '/')

try {
    $savedSeed = $env:SOMEENGINE_ECS_FUZZ_SEED
    $savedSteps = $env:SOMEENGINE_ECS_FUZZ_STEPS
    $savedEvidence = $env:SOMEENGINE_ECS_FUZZ_EVIDENCE
    $savedCommit = $env:SOMEENGINE_ECS_FUZZ_COMMIT_SHA
    try {
        $env:SOMEENGINE_ECS_FUZZ_SEED = $Seed
        $env:SOMEENGINE_ECS_FUZZ_STEPS = $Steps.ToString([Globalization.CultureInfo]::InvariantCulture)
        $env:SOMEENGINE_ECS_FUZZ_EVIDENCE = $candidatePath
        $env:SOMEENGINE_ECS_FUZZ_COMMIT_SHA = $commitSha
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
        $env:SOMEENGINE_ECS_FUZZ_COMMIT_SHA = $savedCommit
    }

    if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
        throw 'The campaign test succeeded without emitting its required evidence artifact.'
    }
    $evidenceDocument = Get-Content -LiteralPath $candidatePath -Raw | ConvertFrom-Json
    $seedValue = if ($Seed.StartsWith('0x', [StringComparison]::OrdinalIgnoreCase)) {
        [Convert]::ToUInt64($Seed.Substring(2), 16)
    }
    else {
        [UInt64]::Parse($Seed, [Globalization.CultureInfo]::InvariantCulture)
    }
    $expectedSeed = '0x{0:x16}' -f $seedValue
    if ($evidenceDocument.schemaVersion -ne 1 -or
        -not $evidenceDocument.clean -or
        -not $evidenceDocument.passed -or
        $evidenceDocument.commitSha -ne $commitSha -or
        $evidenceDocument.seed -ne $expectedSeed -or
        $evidenceDocument.steps -ne $Steps -or
        $evidenceDocument.maximumLogicalEntities -ne 1024 -or
        $evidenceDocument.fullVerificationInterval -ne 128 -or
        $evidenceDocument.prngAlgorithm -ne 'xorshift64star-v1' -or
        $evidenceDocument.durationMilliseconds -le 0 -or
        $evidenceDocument.successfulBatches -lt 0 -or
        $evidenceDocument.rejectedBatches -lt 0 -or
        $evidenceDocument.rejectedImmediateOperations -lt 0 -or
        $evidenceDocument.stateDigest -notmatch '^[0-9a-f]{64}$') {
        throw 'The emitted long-fuzz evidence is incomplete or does not match the requested campaign.'
    }

    $finalStatus = if ($evidenceInsideRepository) {
        @(& git status --porcelain=v1 --untracked-files=all -- . ":(exclude)$relativeCandidateGit")
    }
    else {
        @(& git status --porcelain=v1 --untracked-files=all)
    }
    $finalStatusExitCode = $LASTEXITCODE
    $finalCommitSha = (& git rev-parse --verify HEAD).Trim().ToLowerInvariant()
    if ($finalStatusExitCode -ne 0 -or
        $LASTEXITCODE -ne 0 -or
        $finalStatus.Count -ne 0 -or
        $finalCommitSha -ne $commitSha) {
        throw 'The Git worktree or HEAD changed while the long-fuzz campaign was running.'
    }

    [IO.File]::Move($candidatePath, $evidencePath, $true)
    Write-Host "Long-fuzz evidence written to $evidencePath"
}
finally {
    if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
        Remove-Item -LiteralPath $candidatePath -Force
    }
}
