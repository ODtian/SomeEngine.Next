param(
    [Parameter(Mandatory)][string[]]$QueueId,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$commands = foreach ($id in $QueueId -split ',') {
    $directory = Join-Path $repositoryRoot "artifacts/benchmark-queue/$id"
    [pscustomobject]@{
        Queue = Get-Content -Raw -LiteralPath (Join-Path $directory 'result.json') | ConvertFrom-Json
        Stdout = [IO.File]::ReadAllText((Join-Path $directory 'stdout.log'))
        Stderr = [IO.File]::ReadAllText((Join-Path $directory 'stderr.log'))
    }
}
$destination = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Output))
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
$commands | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output "COMMANDS $($commands.Count) $destination"
