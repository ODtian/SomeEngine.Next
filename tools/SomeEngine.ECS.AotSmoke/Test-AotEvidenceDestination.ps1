Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'AotEvidenceDestination.ps1')

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Assert-ThrowsMessage {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Action,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedMessage
    )

    $threw = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $threw = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected error containing '$ExpectedMessage', received '$($_.Exception.Message)'."
        }
    }
    if (-not $threw) {
        throw "Expected an error containing '$ExpectedMessage'."
    }
}

Assert-ThrowsMessage `
    -ExpectedMessage 'cannot overwrite tracked repository file' `
    -Action {
        Resolve-AotEvidenceDestination `
            -RepositoryRoot $repositoryRoot `
            -Evidence (Join-Path $repositoryRoot 'justfile')
    }

$unignoredPath = Join-Path $repositoryRoot 'ecs-aot-evidence-must-not-be-created.json'
Assert-ThrowsMessage `
    -ExpectedMessage 'must use a git-ignored artifact path' `
    -Action {
        Resolve-AotEvidenceDestination `
            -RepositoryRoot $repositoryRoot `
            -Evidence $unignoredPath
    }

$ignoredPath = Join-Path $repositoryRoot 'artifacts/ecs-aot-destination-test.json'
$resolvedIgnored = Resolve-AotEvidenceDestination `
    -RepositoryRoot $repositoryRoot `
    -Evidence $ignoredPath
if ($resolvedIgnored -ne [IO.Path]::GetFullPath($ignoredPath)) {
    throw 'The ignored in-repository evidence path did not resolve exactly.'
}

$outsidePath = Join-Path ([IO.Path]::GetTempPath()) 'SomeEngine.ECS.AotSmoke/evidence.json'
$resolvedOutside = Resolve-AotEvidenceDestination `
    -RepositoryRoot $repositoryRoot `
    -Evidence $outsidePath
if ($resolvedOutside -ne [IO.Path]::GetFullPath($outsidePath)) {
    throw 'The outside-repository evidence path did not resolve exactly.'
}

Write-Output 'AOT evidence destination tests passed: tracked, unignored, ignored, and external paths.'
