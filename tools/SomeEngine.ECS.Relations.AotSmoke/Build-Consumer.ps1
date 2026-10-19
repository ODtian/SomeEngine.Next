param(
    [string]$QueueScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../tools/Invoke-Benchmark.ps1')),
    [string]$SourceCommit,
    [string]$Session = $env:CODEX_THREAD_ID
)

$ErrorActionPreference = 'Stop'
$queueTool = [IO.Path]::GetFullPath($QueueScript)
$consumerProject = Join-Path $PSScriptRoot 'SomeEngine.ECS.Relations.AotSmoke.csproj'
[xml]$consumerXml = Get-Content -LiteralPath $consumerProject -Raw
$packageVersion = [string]$consumerXml.Project.PropertyGroup.SomeEnginePackageVersion
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$SourceCommit) { $SourceCommit = (git -C $repositoryRoot rev-parse HEAD).Trim() }
$packageArea = Join-Path $repositoryRoot 'artifacts/ecs-relations'
$packageFeed = Join-Path $packageArea 'feed'
$packageCache = Join-Path $packageArea 'cache'
$nativeOutput = Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/native-publish'
$packageNames = @('SomeEngine.Diagnostics', 'SomeEngine.Job.Contracts', 'SomeEngine.Job', 'SomeEngine.ECS.SourceGen',
    'SomeEngine.ECS', 'SomeEngine.ECS.Relations', 'SomeEngine.ECS.Systems', 'SomeEngine.Physics')
$stages = [Collections.Generic.List[object]]::new()
$stagePath = Join-Path $packageArea "package-stages-$SourceCommit.json"
$packStagePath = Join-Path $packageArea "package-pack-stages-$SourceCommit.json"
$sourceArgument = "-p:SourceRevisionId=$SourceCommit"

function Submit-Command([string[]]$Command) {
    $submission = & $queueTool --session $Session @Command
    if ($LASTEXITCODE -ne 0) { throw "Queue submission failed: $($Command -join ' ')" }
    $queued = $submission | ConvertFrom-Json
    Write-Host "QUEUED $($queued.Id): $($Command -join ' ')"
    return $queued
}

function Complete-Command($Queued, [string]$Stage) {
    $resultFile = Join-Path $Queued.OutputDirectory 'result.json'
    while (!(Test-Path -LiteralPath $resultFile)) { Start-Sleep -Seconds 1 }
    $completed = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
    if ($Stage -eq 'PackPackages') {
        foreach ($pack in (Get-Content -LiteralPath $packStagePath -Raw | ConvertFrom-Json)) {
            $pack | Add-Member -NotePropertyName QueueId -NotePropertyValue $Queued.Id
            $stages.Add($pack)
        }
    }
    else {
        $stages.Add([pscustomobject]@{
            Stage = $Stage; SourceCommit = $SourceCommit; QueueId = $Queued.Id
            Executable = $completed.Executable; Arguments = $completed.Arguments
            StartedAtUtc = $completed.StartedAtUtc; FinishedAtUtc = $completed.FinishedAtUtc
            ExitCode = $completed.ExitCode
        })
    }
    $stages | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $stagePath -Encoding utf8
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
        '-c', 'Release', '--no-restore', '-p:EmitCompilerGeneratedFiles=true', $sourceArgument)) 'BuildPackages'
    $packSettings = [pscustomobject]@{
        RepositoryRoot = $repositoryRoot; PackageNames = $packageNames; PackageVersion = $packageVersion
        PackageFeed = $packageFeed; SourceCommit = $SourceCommit; StagePath = $packStagePath
    } | ConvertTo-Json -Compress
    $encodedSettings = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($packSettings))
    $packScript = '$settings = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(''' + $encodedSettings + ''')) | ConvertFrom-Json' + "`n" + @'
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $settings.RepositoryRoot
$stages = [Collections.Generic.List[object]]::new()
foreach ($name in $settings.PackageNames) {
    $arguments = @('pack', "src/$name/$name.csproj", '-c', 'Release', '--no-build',
        "-p:Version=$($settings.PackageVersion)", "-p:SourceRevisionId=$($settings.SourceCommit)", '-o', $settings.PackageFeed)
    $started = [DateTime]::UtcNow
    & dotnet @arguments
    $code = $LASTEXITCODE
    $stage = [pscustomobject]@{
        Stage = "Pack:$name"; SourceCommit = $settings.SourceCommit
        Executable = 'dotnet'; Arguments = $arguments
        StartedAtUtc = $started; FinishedAtUtc = [DateTime]::UtcNow; ExitCode = $code
    }
    $stages.Add($stage)
    $stages | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $settings.StagePath -Encoding utf8
    Write-Output ('PACK_STAGE ' + ($stage | ConvertTo-Json -Compress))
    if ($code -ne 0) { exit $code }
}
'@
    $encodedPack = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($packScript))
    Complete-Command (Submit-Command @('pwsh', '-NoProfile', '-EncodedCommand', $encodedPack)) 'PackPackages'
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
        '--configfile', (Join-Path $PSScriptRoot 'NuGet.Config'), $sourceArgument)) 'RestoreConsumer'
    $generatedDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj/Release/net10.0/win-x64/generated'))
    $consumerDirectory = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
    if (!$generatedDirectory.StartsWith($consumerDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated files must be within $consumerDirectory"
    }
    if (Test-Path -LiteralPath $generatedDirectory) { Remove-Item -LiteralPath $generatedDirectory -Recurse -Force }
    Complete-Command (Submit-Command @('dotnet', 'build', $consumerProject, '-c', 'Release', '--no-restore',
        "-p:RestorePackagesPath=$packageCache", '-t:Rebuild', $sourceArgument)) 'RebuildConsumer'
    Complete-Command (Submit-Command @('dotnet', (Join-Path $PSScriptRoot 'bin/Release/net10.0/win-x64/SomeEngine.ECS.Relations.AotSmoke.dll'))) 'RunConsumerJit'
    Complete-Command (Submit-Command @('dotnet', 'publish', $consumerProject, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '--no-restore', '-p:PublishAot=true', '-p:PublishAotUsingRuntimePack=true',
        "-p:RestorePackagesPath=$packageCache", '-p:TrimmerSingleWarn=false', '-o', $nativeOutput, $sourceArgument)) 'PublishConsumerNative'
    Complete-Command (Submit-Command @((Join-Path $nativeOutput 'SomeEngine.ECS.Relations.AotSmoke.exe'))) 'RunConsumerNative'
}
finally { Set-Location -LiteralPath $previousDirectory }
