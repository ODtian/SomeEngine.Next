$ErrorActionPreference = 'Stop'
$queueTool = 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1'
$consumerProject = Join-Path $PSScriptRoot 'SomeEngine.ECS.Relations.AotSmoke.csproj'
[xml]$consumerXml = Get-Content -LiteralPath $consumerProject -Raw
$packageVersion = [string]$consumerXml.Project.PropertyGroup.SomeEnginePackageVersion
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$packageArea = Join-Path $repositoryRoot 'artifacts/ecs-relations'
$packageFeed = Join-Path $packageArea 'feed'
$packageCache = Join-Path $packageArea 'cache'
$nativeOutput = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/native-publish'
$packageNames = @('SomeEngine.Diagnostics', 'SomeEngine.Job.Contracts', 'SomeEngine.Job', 'SomeEngine.ECS.SourceGen',
    'SomeEngine.ECS', 'SomeEngine.ECS.Relations', 'SomeEngine.ECS.Systems', 'SomeEngine.Physics')

function Submit-Command([string[]]$Command) {
    $submission = & $queueTool @Command
    if ($LASTEXITCODE -ne 0) { throw "Queue submission failed: $($Command -join ' ')" }
    $queued = $submission | ConvertFrom-Json
    Write-Host "QUEUED $($queued.Id): $($Command -join ' ')"
    return $queued
}

function Complete-Command($Queued) {
    $resultFile = Join-Path $Queued.OutputDirectory 'result.json'
    while (!(Test-Path -LiteralPath $resultFile)) { Start-Sleep -Seconds 1 }
    $completed = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    Write-Host ($completed | Select-Object Id, ExitCode, StartedAtUtc, FinishedAtUtc | ConvertTo-Json -Compress)
    Get-Content -LiteralPath (Join-Path $Queued.OutputDirectory 'stdout.log') -Tail 12 | Write-Host
    if ($completed.ExitCode -ne 0) {
        Get-Content -LiteralPath (Join-Path $Queued.OutputDirectory 'stderr.log') -Tail 20 | Write-Host
        throw "Queued command failed: $($Queued.Id)"
    }
}

$previousDirectory = Get-Location
try {
    Set-Location -LiteralPath $repositoryRoot
    New-Item -ItemType Directory -Path $packageFeed, $packageCache -Force | Out-Null
    Complete-Command (Submit-Command @('dotnet', 'build', 'src/SomeEngine.Physics/SomeEngine.Physics.csproj',
        '-c', 'Release', '--no-restore', '-p:EmitCompilerGeneratedFiles=true'))
    $packCommands = foreach ($name in $packageNames) {
        Submit-Command @('dotnet', 'pack', "src/$name/$name.csproj", '-c', 'Release', '--no-build',
            "-p:Version=$packageVersion", '-o', $packageFeed)
    }
    foreach ($command in $packCommands) { Complete-Command $command }
    foreach ($name in $packageNames) {
        $versionDirectory = [IO.Path]::GetFullPath((Join-Path $packageCache "$($name.ToLowerInvariant())/$packageVersion"))
        $cacheDirectory = [IO.Path]::GetFullPath($packageCache) + [IO.Path]::DirectorySeparatorChar
        if (!$versionDirectory.StartsWith($cacheDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package cache path must be within $cacheDirectory"
        }
        if (Test-Path -LiteralPath $versionDirectory) { Remove-Item -LiteralPath $versionDirectory -Recurse -Force }
    }
    Complete-Command (Submit-Command @('dotnet', 'restore', $consumerProject, '-r', 'win-x64',
        '-p:PublishAot=true', '-p:_IsPublishing=true', "-p:RestorePackagesPath=$packageCache",
        '--configfile', (Join-Path $PSScriptRoot 'NuGet.Config')))
    $generatedDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj/Release/net10.0/win-x64/generated'))
    $consumerDirectory = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
    if (!$generatedDirectory.StartsWith($consumerDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated files must be within $consumerDirectory"
    }
    if (Test-Path -LiteralPath $generatedDirectory) { Remove-Item -LiteralPath $generatedDirectory -Recurse -Force }
    Complete-Command (Submit-Command @('dotnet', 'build', $consumerProject, '-c', 'Release', '--no-restore',
        "-p:RestorePackagesPath=$packageCache", '-t:Rebuild'))
    Complete-Command (Submit-Command @('dotnet', (Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/SomeEngine.ECS.Relations.AotSmoke.dll')))
    Complete-Command (Submit-Command @('dotnet', 'publish', $consumerProject, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '--no-restore', '-p:PublishAot=true', '-p:PublishAotUsingRuntimePack=true',
        "-p:RestorePackagesPath=$packageCache", '-p:TrimmerSingleWarn=false', '-o', $nativeOutput))
    Complete-Command (Submit-Command @((Join-Path $nativeOutput 'SomeEngine.ECS.Relations.AotSmoke.exe')))
}
finally { Set-Location -LiteralPath $previousDirectory }
