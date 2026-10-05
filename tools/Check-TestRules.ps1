[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $ResultsDirectory = 'artifacts/test-results'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location -LiteralPath $repository
try {
    $results = [IO.Path]::GetFullPath($ResultsDirectory)
    [IO.Directory]::CreateDirectory($results) | Out-Null
    $projects = Get-ChildItem -LiteralPath (Join-Path $repository 'tests') -Filter '*.csproj' -Recurse |
        Sort-Object FullName
    $log = Join-Path $results 'test-rules.log'
    [IO.File]::WriteAllText($log, '')
    $checkExitCode = 0
    foreach ($project in $projects) {
        & dotnet msbuild $project.FullName -t:CheckTestRules "-p:Configuration=$Configuration" -v:minimal 2>&1 |
            Tee-Object -FilePath $log -Append
        if ($LASTEXITCODE -ne 0) { $checkExitCode = 1 }
    }
    Write-Output "Test rules checked in $($projects.Count) test projects." | Tee-Object -FilePath $log -Append
    exit $checkExitCode
}
finally { Pop-Location }
