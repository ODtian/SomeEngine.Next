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
    & dotnet run --project templates/DefaultProject/Game.Build -c $Configuration -- `
        --project templates/DefaultProject --engine . 2>&1 | Tee-Object (Join-Path $results 'content.log')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet build SomeEngine.slnx -c $Configuration -v minimal 2>&1 | Tee-Object (Join-Path $results 'build.log')
    exit $LASTEXITCODE
}
finally { Pop-Location }
