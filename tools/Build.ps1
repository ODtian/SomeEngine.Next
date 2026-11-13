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
    & dotnet build tools/BuildContent/BuildContent.csproj -c $Configuration -m:4 -nr:false -v minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet (Join-Path $repository "tools/BuildContent/bin/$Configuration/net10.0/BuildContent.dll") sample samples/DefaultGame $repository `
        2>&1 | Tee-Object (Join-Path $results 'content.log')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet build SomeEngine.slnx -c $Configuration -m:4 -nr:false -v minimal 2>&1 | Tee-Object (Join-Path $results 'build.log')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet publish samples/DefaultGame/DefaultGame.csproj -c $Configuration --no-build --no-restore `
        -o "samples/DefaultGame/bin/game/$Configuration/net10.0" -v minimal `
        2>&1 | Tee-Object (Join-Path $results 'publish.log')
    exit $LASTEXITCODE
}
finally { Pop-Location }
