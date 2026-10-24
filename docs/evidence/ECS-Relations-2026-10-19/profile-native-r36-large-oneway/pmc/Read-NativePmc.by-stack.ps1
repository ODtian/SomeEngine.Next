[CmdletBinding()]
param(
    [string]$EtlPath = 'F:\.codex\worktrees\ecs-relations-worker-3\SomeEngine.Next\artifacts\ecs-relations\measurements\profile-native-r36-large-oneway\pmc-session\pmc.etl',
    [int]$TargetProcessId = 29672,
    [string]$NativeImageName = 'SomeEngine.ECS.Relations.Benchmarks.exe',
    [string]$OutputDirectory = '',
    [switch]$CompileOnly
)

$ErrorActionPreference = 'Stop'
$traceDirectory = 'C:\Users\boqi\.dotnet\tools\.store\dotnet-trace\9.0.661903\dotnet-trace\9.0.661903\tools\net8.0\any'
$traceAssembly = Join-Path $traceDirectory 'Microsoft.Diagnostics.Tracing.TraceEvent.dll'
$serializationAssembly = Join-Path $traceDirectory 'Microsoft.Diagnostics.FastSerialization.dll'
[Reflection.Assembly]::LoadFrom($serializationAssembly) | Out-Null
[Reflection.Assembly]::LoadFrom($traceAssembly) | Out-Null
$frameworkReferences = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
Add-Type -Path (Join-Path $PSScriptRoot 'NativePmcReader.cs') -ReferencedAssemblies ($frameworkReferences + @($traceAssembly, $serializationAssembly))

if ($CompileOnly) {
    'NativePmcReader compiled against the installed TraceEvent DLL.'
    return
}

if ($OutputDirectory -eq '') { $OutputDirectory = Split-Path -Parent $EtlPath }
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$summaryPath = [PmcAnalysis.NativePmcReader]::Read($EtlPath, $TargetProcessId, $NativeImageName, $OutputDirectory)
Write-Output $summaryPath
