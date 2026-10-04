[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(win-(x64|arm64)|linux-(x64|arm64)|linux-musl-(x64|arm64)|osx-(x64|arm64))(,(win-(x64|arm64)|linux-(x64|arm64)|linux-musl-(x64|arm64)|osx-(x64|arm64)))*$')]
    [string] $Rids,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Evidence,

    [ValidateSet('ECS', 'Views', IgnoreCase = $false)]
    [string] $Project = 'ECS'
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

$evidencePath = Resolve-AotEvidenceDestination `
    -RepositoryRoot $repositoryRoot `
    -Evidence $Evidence

$recipe = $Project.ToLowerInvariant() + '-aot'
$projectName = "SomeEngine.$Project.AotSmoke"
$results = @(
    foreach ($rid in $ridList) {
        # Preserve the child command's console log without adding its strings to pipeline results.
        & just $recipe $rid | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "NativeAOT execution failed for $rid with exit code $LASTEXITCODE."
        }

        $extension = if ($rid.StartsWith('win-', [StringComparison]::Ordinal)) { '.exe' } else { '' }
        $executable = Join-Path $repositoryRoot "tools/$projectName/bin/Release/net10.0/$rid/native-publish/$projectName$extension"
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

$manifest = [ordered]@{
    project = $Project
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sdkVersion = (& dotnet --version).Trim()
    machineName = [Environment]::MachineName
    hostFramework = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    hostOperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    results = $results
}

$evidenceJson = ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine
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
