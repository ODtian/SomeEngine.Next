param(
    [string]$PublishResultPath,
    [string]$NativeDirectory
)

$ErrorActionPreference = 'Stop'
$publish = Get-Content -LiteralPath $PublishResultPath -Raw | ConvertFrom-Json
if ($publish.ExitCode -ne 0) { exit $publish.ExitCode }
$executable = Join-Path $NativeDirectory 'SomeEngine.Animation.Checks.exe'
Write-Output ('nativeAot=True; executableBytes={0}' -f (Get-Item -LiteralPath $executable).Length)
& $executable
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $executable --view-switch-benchmark
exit $LASTEXITCODE
