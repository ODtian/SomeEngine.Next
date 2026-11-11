$ErrorActionPreference = 'Stop'
if ($args.Count -eq 0) { throw 'Usage: Invoke-Benchmark.ps1 [--cpu|--gpu] [--session id] [--timeout seconds] [--sample-pattern regex --sample-max target|--sample-min target] [--diagnostic scope] <executable> [arguments...]' }

$project = Join-Path $PSScriptRoot 'BenchmarkQueue/BenchmarkQueue.csproj'
$application = Join-Path $PSScriptRoot 'BenchmarkQueue/bin/ResourceQueue/Release/net10.0/SomeEngine.CommandQueue.dll'
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'BenchmarkQueue') -File | Where-Object Extension -in '.cs', '.csproj'
if (!(Test-Path -LiteralPath $application) -or ($sources.LastWriteTimeUtc | Sort-Object -Descending | Select-Object -First 1) -gt (Get-Item -LiteralPath $application).LastWriteTimeUtc) {
    & dotnet build $project -c Release -p:BaseOutputPath=bin/ResourceQueue/ -v quiet | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& dotnet $application @args
exit $LASTEXITCODE
