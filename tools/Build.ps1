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
    # Existing test projects consume these generated shader assets during compilation.
    & dotnet run --project samples/DefaultGame/DefaultGame.csproj -c Debug -- sample `
        2>&1 | Tee-Object (Join-Path $results 'content.log')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet build SomeEngine.slnx -c $Configuration -v minimal 2>&1 | Tee-Object (Join-Path $results 'build.log')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet publish samples/DefaultGame/DefaultGame.csproj -c Release `
        -o samples/DefaultGame/bin/game/Release/net10.0 -v minimal `
        2>&1 | Tee-Object (Join-Path $results 'publish.log')
    exit $LASTEXITCODE
}
finally { Pop-Location }
