[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Executable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$executablePath = [IO.Path]::GetFullPath($Executable)
$checkpointPath = Join-Path ([IO.Path]::GetDirectoryName($executablePath)) ('view-checkpoint-' + [Guid]::NewGuid().ToString('N') + '.bin')
try {
    & $executablePath write-checkpoint $checkpointPath
    if ($LASTEXITCODE -ne 0) {
        throw "Checkpoint write failed with exit code $LASTEXITCODE."
    }
    & $executablePath read-checkpoint $checkpointPath
    if ($LASTEXITCODE -ne 0) {
        throw "Checkpoint read failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $checkpointPath) {
        Remove-Item -LiteralPath $checkpointPath -Force
    }
}
