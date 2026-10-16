$ErrorActionPreference = 'Stop'
if ($args.Count -eq 0) { throw 'Usage: Invoke-Benchmark.ps1 <executable> [arguments...]' }

$project = Join-Path $PSScriptRoot 'BenchmarkQueue/BenchmarkQueue.csproj'
$application = Join-Path $PSScriptRoot 'BenchmarkQueue/bin/Release/net10.0/BenchmarkQueue.dll'
if (!(Test-Path -LiteralPath $application)) {
    & dotnet build $project -c Release -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& dotnet $application @args
exit $LASTEXITCODE
