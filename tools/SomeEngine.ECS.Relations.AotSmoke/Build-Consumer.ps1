param(
    [string]$QueueScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../tools/Invoke-Benchmark.ps1')),
    [string]$Session = $env:CODEX_THREAD_ID
)

$ErrorActionPreference = 'Stop'
$queueTool = [IO.Path]::GetFullPath($QueueScript)
$consumerProject = Join-Path $PSScriptRoot 'SomeEngine.ECS.Relations.AotSmoke.csproj'
[xml]$consumerXml = Get-Content -LiteralPath $consumerProject -Raw
$packageVersion = [string]$consumerXml.Project.PropertyGroup.SomeEnginePackageVersion
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packageArea = Join-Path $repositoryRoot 'artifacts/ecs-relations'
$packageFeed = Join-Path $packageArea 'feed'
$packageCache = Join-Path $packageArea 'cache'
$nativeOutput = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/native-publish'
$packageNames = @('SomeEngine.Diagnostics', 'SomeEngine.Job.Resources', 'SomeEngine.Job',
    'SomeEngine.ECS.SourceGen', 'SomeEngine.ECS', 'SomeEngine.ECS.Relationships', 'SomeEngine.ECS.Systems')

function Complete-Command([string]$Stage, [string[]]$Command) {
    $submission = & $queueTool --session $Session --local --cpu --timeout 300 @Command
    if ($LASTEXITCODE -ne 0) { throw "Queue submission failed: $Stage" }
    $queued = $submission | ConvertFrom-Json
    Write-Host "QUEUED $($queued.Id): $Stage"
    $resultFile = Join-Path $queued.OutputDirectory 'result.json'
    while (!(Test-Path -LiteralPath $resultFile)) { Start-Sleep -Seconds 1 }
    $completed = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    Write-Host ($completed | Select-Object Id, ExitCode, StartedAtUtc, FinishedAtUtc | ConvertTo-Json -Compress)
    Get-Content -LiteralPath (Join-Path $queued.OutputDirectory 'stdout.log') -Tail 12 | Write-Host
    if ($completed.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $queued.OutputDirectory 'stderr.log') -Tail 20 | Write-Host
        throw "Queued command failed: $Stage ($($queued.Id))"
    }
}

$previousDirectory = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    New-Item -ItemType Directory -Path $packageFeed, $packageCache -Force | Out-Null
    Complete-Command 'Build packages' @('dotnet', 'build', 'src/SomeEngine.ECS.Systems/SomeEngine.ECS.Systems.csproj',
        '-c', 'Release', '-v:minimal', '-nodeReuse:false')
    foreach ($name in $packageNames) {
        Complete-Command "Pack $name" @('dotnet', 'pack', "src/$name/$name.csproj", '-c', 'Release',
            '--no-build', "-p:Version=$packageVersion", '-o', $packageFeed, '-nodeReuse:false')
    }
    Complete-Command 'Restore consumer' @('dotnet', 'restore', $consumerProject, '-r', 'win-x64',
        '-p:PublishAot=true', '-p:_IsPublishing=true', "-p:RestorePackagesPath=$packageCache",
        '--configfile', (Join-Path $PSScriptRoot 'NuGet.Config'), '-nodeReuse:false')
    Complete-Command 'Build consumer' @('dotnet', 'build', $consumerProject, '-c', 'Release', '--no-restore',
        "-p:RestorePackagesPath=$packageCache", '-nodeReuse:false')
    Complete-Command 'Run consumer JIT' @('dotnet',
        (Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/SomeEngine.ECS.Relations.AotSmoke.dll'))
    Complete-Command 'Publish consumer NativeAOT' @('dotnet', 'publish', $consumerProject, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '--no-restore', '-p:PublishAot=true', '-p:PublishAotUsingRuntimePack=true',
        "-p:RestorePackagesPath=$packageCache", '-p:TrimmerSingleWarn=false', '-o', $nativeOutput, '-nodeReuse:false')
    Complete-Command 'Run consumer NativeAOT' @((Join-Path $nativeOutput 'SomeEngine.ECS.Relations.AotSmoke.exe'))
}
finally { Set-Location -LiteralPath $previousDirectory }
