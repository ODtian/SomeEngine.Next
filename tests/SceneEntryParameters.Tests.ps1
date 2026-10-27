[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$measurement = Join-Path $repository 'tools/Measure-Scenes.ps1'
if (![IO.File]::Exists($measurement)) { throw [IO.FileNotFoundException]::new('The scene measurement entry is missing.', $measurement) }
# The invalid parameters must be rejected during public PowerShell argument binding.
# If a validation attribute is accidentally removed, this nonexistent app is a
# second guard: the script still cannot submit a queue task or launch an engine.
$missingApplication = Join-Path $repository ('missing-scene-check-' + [Guid]::NewGuid().ToString('N') + '.exe')
if ([IO.File]::Exists($missingApplication)) { throw [IO.IOException]::new('The guard path already exists.') }

function Assert-ParameterRejected {
    param([string]$Name, [scriptblock]$Action)
    try { & $Action }
    catch [System.Management.Automation.ParameterBindingException] {
        return [pscustomobject]@{ Check = $Name; Passed = $true }
    }
    throw [InvalidOperationException]::new("The parameter binding did not reject: $Name")
}

$checks = @(
    Assert-ParameterRejected 'StartupFrames below one is rejected' {
        & $measurement -ApplicationPath $missingApplication -StartupFrames 0
    }
    Assert-ParameterRejected 'StartupFrames above 120 is rejected' {
        & $measurement -ApplicationPath $missingApplication -StartupFrames 121
    }
    Assert-ParameterRejected 'An unknown shading path is rejected' {
        & $measurement -ApplicationPath $missingApplication -ShadingPath 'not-a-shading-path'
    }
    Assert-ParameterRejected 'An unknown scene playback mode is rejected' {
        & $measurement -ApplicationPath $missingApplication -ScenePlayback 'not-a-playback-mode'
    }
    Assert-ParameterRejected 'An empty selected scene is rejected' {
        & $measurement -ApplicationPath $missingApplication -Scene ''
    }
)
# Flat-output guards run inside the public script body. An absent application
# would stop too early; a read-locked, non-PE props file reaches those guards but
# cannot become an engine if a guard regresses. No -Trace: no fallback directory.
$flatDirectory = Join-Path $repository 'samples/DefaultGame/obj'
if (![IO.Directory]::Exists($flatDirectory)) {
    throw [IO.DirectoryNotFoundException]::new('Flat-output checks require the existing DefaultGame obj directory.')
}
$guardApplication = Join-Path $repository 'Directory.Build.props'
$environmentNames = @('SOMEENGINE_MEASUREMENT_OUTPUT_DIRECTORY', 'SOMEENGINE_MEASUREMENT_OUTPUT_BASENAME')
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Assert-FlatOutputRejected {
    param([hashtable]$Case)
    $collision = $null
    try {
        if ($Case.CollisionSuffix) {
            $collisionPath = Join-Path $flatDirectory ($Case.Basename + $Case.CollisionSuffix)
            # CreateNew cannot replace foreign content. DeleteOnClose cleans up
            # only this owned handle/file, even when the assertion below fails.
            $collision = [IO.FileStream]::new($collisionPath, [IO.FileMode]::CreateNew,
                [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read, 4096,
                [IO.FileOptions]::DeleteOnClose)
            $collision.WriteByte(0x78)
            $collision.Flush()
            if (![IO.File]::Exists($collisionPath)) {
                throw [IO.IOException]::new('The collision fixture is not visible.')
            }
        }
        [Environment]::SetEnvironmentVariable('SOMEENGINE_MEASUREMENT_OUTPUT_DIRECTORY', $Case.Directory, 'Process')
        [Environment]::SetEnvironmentVariable('SOMEENGINE_MEASUREMENT_OUTPUT_BASENAME', $Case.Basename, 'Process')
        $rejected = $false
        try {
            & $measurement -ApplicationPath $guardApplication -FromBenchmarkQueue -OnDefaultDesktop 6>$null
        } catch {
            # Different failures (including Process.Start rejecting the props
            # file) must not masquerade as successful flat-output validation.
            if ($_.Exception.Message -cne $Case.Error) { throw }
            $rejected = $true
        }
        if (!$rejected) {
            throw [InvalidOperationException]::new("Flat output was not rejected: $($Case.Name)")
        }
        return [pscustomobject]@{ Check = $Case.Name; Passed = $true }
    } finally {
        if ($null -ne $collision) {
            try {
                $collision.Position = 0
                if ($collision.Length -ne 1 -or $collision.ReadByte() -ne 0x78) {
                    throw [IO.IOException]::new('The collision fixture content changed.')
                }
            } finally { $collision.Dispose() }
        }
    }
}

$applicationGuard = $null
try {
    $applicationGuard = [IO.File]::Open($guardApplication, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $firstByte = $applicationGuard.ReadByte()
    $secondByte = $applicationGuard.ReadByte()
    if ($firstByte -eq 0x4d -and $secondByte -eq 0x5a) {
        throw [IO.InvalidDataException]::new('The application guard must not be a Windows executable.')
    }
    $basename = 'flat-check-' + [Guid]::NewGuid().ToString('N')
    $missingDirectory = Join-Path $flatDirectory ('missing-flat-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $missingDirectory) {
        throw [IO.IOException]::new('The absent-directory guard path already exists.')
    }
    $invalid = 'Flat measurement output requires an existing absolute directory and an ASCII basename.'
    $exists = 'Flat measurement output already exists.'
    $flatCases = @(
        @{ Name = 'Flat output rejects a relative directory'; Directory = 'samples/DefaultGame/obj'; Basename = $basename; Error = $invalid }
        @{ Name = 'Flat output rejects a nonexistent directory'; Directory = $missingDirectory; Basename = $basename; Error = $invalid }
        @{ Name = 'Flat output rejects an invalid basename'; Directory = $flatDirectory; Basename = 'bad.name'; Error = $invalid }
        @{ Name = 'Flat output rejects an existing report'; Directory = $flatDirectory; Basename = $basename; CollisionSuffix = '-results.json'; Error = $exists }
        @{ Name = 'Flat output rejects existing frame samples'; Directory = $flatDirectory; Basename = $basename; CollisionSuffix = '-frame-samples.json'; Error = $exists }
    )
    foreach ($case in $flatCases) {
        $checks += Assert-FlatOutputRejected $case
    }
    if (Test-Path -LiteralPath $missingDirectory) {
        throw [IO.IOException]::new('The rejected missing output directory was created.')
    }
} finally {
    try {
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    } finally {
        if ($null -ne $applicationGuard) { $applicationGuard.Dispose() }
    }
}
$checks | Format-Table Check, Passed
if ($checks.Count -ne 10 -or @($checks | Where-Object Passed -ne $true).Count -ne 0) {
    throw [InvalidOperationException]::new('The public parameter and flat-output checks did not all complete.')
}
