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
$checks | Format-Table Check, Passed
if ($checks.Count -ne 5 -or @($checks | Where-Object Passed -ne $true).Count -ne 0) {
    throw [InvalidOperationException]::new('The public parameter checks did not all complete.')
}
