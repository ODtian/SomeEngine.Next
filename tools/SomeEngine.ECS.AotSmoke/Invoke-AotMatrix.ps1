[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(win-(x64|arm64)|linux-(x64|arm64)|linux-musl-(x64|arm64)|osx-(x64|arm64))(,(win-(x64|arm64)|linux-(x64|arm64)|linux-musl-(x64|arm64)|osx-(x64|arm64)))*$')]
    [string] $Rids,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Evidence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $repositoryRoot
. (Join-Path $PSScriptRoot 'AotEvidenceDestination.ps1')

$ridList = @($Rids.Split(','))
if ($ridList.Count -ne @($ridList | Sort-Object -Unique).Count) {
    throw 'The NativeAOT RID list contains a duplicate.'
}

# This is intentionally the first external gate: dirty source must fail before restore/publish/run.
$initialStatus = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the Git worktree.'
}
if ($initialStatus.Count -ne 0) {
    throw "NativeAOT evidence requires a clean tracked and untracked worktree.`n$($initialStatus -join [Environment]::NewLine)"
}

$commitSha = (& git rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commitSha -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw 'Unable to capture a full Git commit SHA.'
}
$commitSha = $commitSha.ToLowerInvariant()

$evidencePath = Resolve-AotEvidenceDestination `
    -RepositoryRoot $repositoryRoot `
    -Evidence $Evidence

$results = @(
    foreach ($rid in $ridList) {
        # Preserve the child command's console log without adding its strings to pipeline results.
        & just ecs-aot $rid | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "NativeAOT certification failed for $rid with exit code $LASTEXITCODE."
        }

        $extension = if ($rid.StartsWith('win-', [StringComparison]::Ordinal)) { '.exe' } else { '' }
        $executable = Join-Path $repositoryRoot "tools/SomeEngine.ECS.AotSmoke/bin/Release/net10.0/$rid/cert-publish/SomeEngine.ECS.AotSmoke$extension"
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Native executable is missing for ${rid}: $executable"
        }

        [pscustomobject][ordered]@{
            rid = $rid
            executed = $true
            exitCode = 0
            executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        }
    }
)

$finalStatus = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to re-inspect the Git worktree.'
}
if ($finalStatus.Count -ne 0) {
    throw "The Git worktree changed while NativeAOT evidence was running.`n$($finalStatus -join [Environment]::NewLine)"
}
$finalCommitSha = (& git rev-parse --verify HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $finalCommitSha -ne $commitSha) {
    throw 'HEAD changed while NativeAOT evidence was running.'
}

$manifest = [ordered]@{
    schemaVersion = 2
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    commitSha = $commitSha
    clean = $true
    sdkVersion = (& dotnet --version).Trim()
    machineName = [Environment]::MachineName
    hostFramework = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    hostOperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    results = $results
}

$evidenceJson = ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine
$validatedEvidence = $evidenceJson | ConvertFrom-Json
$expectedManifestProperties = 'schemaVersion,createdUtc,commitSha,clean,sdkVersion,machineName,hostFramework,hostOperatingSystem,results'
if (($validatedEvidence.PSObject.Properties.Name -join ',') -ne $expectedManifestProperties -or
    $validatedEvidence.schemaVersion -ne 2 -or
    -not $validatedEvidence.clean -or
    $validatedEvidence.commitSha -ne $commitSha) {
    throw 'NativeAOT evidence manifest does not have the exact schema-2 identity shape.'
}
$validatedResults = @($validatedEvidence.results)
$expectedProperties = 'rid,executed,exitCode,executableSha256'
if ($validatedResults.Count -ne $ridList.Count) {
    throw 'NativeAOT evidence result count does not match the requested RID set.'
}
foreach ($result in $validatedResults) {
    if ($result -isnot [pscustomobject] -or
        ($result.PSObject.Properties.Name -join ',') -ne $expectedProperties) {
        throw 'NativeAOT evidence results must contain only the exact result object shape.'
    }
    if (-not $result.executed -or $result.exitCode -ne 0 -or
        $result.executableSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "NativeAOT evidence result for '$($result.rid)' is not successful and complete."
    }
}
if (($validatedResults.rid -join ',') -ne ($ridList -join ',')) {
    throw 'NativeAOT evidence result RIDs do not exactly match the requested RID set and order.'
}

$directory = [IO.Path]::GetDirectoryName($evidencePath)
if ($directory) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
$temporaryEvidencePath = [IO.Path]::Combine(
    $directory,
    '.' + [IO.Path]::GetFileName($evidencePath) + '.tmp.' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.File]::WriteAllText(
        $temporaryEvidencePath,
        $evidenceJson,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporaryEvidencePath, $evidencePath, $true)
}
finally {
    if (Test-Path -LiteralPath $temporaryEvidencePath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryEvidencePath -Force
    }
}
Write-Host "NativeAOT evidence written to $evidencePath"
