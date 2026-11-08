$ErrorActionPreference = 'Stop'
$MemoryMB = 4096
$TimeoutSeconds = 300
$IdleSeconds = 30
$ProcessIdFile = $null
$index = 0
# Limits precede the executable. Its remaining arguments pass through together.
while ($index -lt $args.Count) {
    $option = [string]$args[$index]
    if ($option -in @('--', '-Command')) { $index++; break }
    if ($option -notin @('-MemoryMB', '-TimeoutSeconds', '-IdleSeconds', '-ProcessIdFile')) { break }
    $value = $args[$index + 1]
    switch ($option) {
        '-MemoryMB' { $MemoryMB = [int]$value }
        '-TimeoutSeconds' { $TimeoutSeconds = [int]$value }
        '-IdleSeconds' { $IdleSeconds = [int]$value }
        '-ProcessIdFile' { $ProcessIdFile = [string]$value }
    }
    $index += 2
}
$Command = [string[]]@($args[$index..($args.Count - 1)] | ForEach-Object { $_ })
$application = Join-Path $PSScriptRoot 'ProcessWatchdog/bin/Release/net10.0/SomeEngine.ProcessWatchdog.dll'
if (!(Test-Path -LiteralPath $application)) {
    & dotnet build (Join-Path $PSScriptRoot 'ProcessWatchdog/ProcessWatchdog.csproj') -c Release -m:1 -nr:false -p:UseSharedCompilation=false -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$watchdogArguments = @('--memory-mb', $MemoryMB, '--timeout', $TimeoutSeconds, '--idle-timeout', $IdleSeconds)
if ($ProcessIdFile) { $watchdogArguments += @('--process-id-file', $ProcessIdFile) }
& dotnet $application @watchdogArguments -- @Command
exit $LASTEXITCODE
