[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $UpdateContent,
    [switch] $ApplicationOnly,
    [string] $ProjectContentRoot,
    [string] $ImportedSceneContentRoot,
    [string] $ResultsDirectory = 'samples/DefaultGame/obj'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Reuse the existing project outputs. A relocated build would escape the size check.
foreach ($name in @('ArtifactsPath', 'BaseOutputPath', 'BaseIntermediateOutputPath', 'OutputPath',
        'IntermediateOutputPath', 'MSBuildProjectExtensionsPath', 'UseArtifactsOutput', 'PublishDir')) {
    if ([Environment]::GetEnvironmentVariable($name)) {
        throw "Remove the $name output override before using this incremental build entry."
    }
}
$properties = @('-p:UseArtifactsOutput=false')
foreach ($name in @('ProjectContentRoot', 'ImportedSceneContentRoot')) {
    $value = Get-Variable -Name $name -ValueOnly
    if (!$value) { continue }
    $value = [IO.Path]::GetFullPath($value, $repository)
    if (![IO.Directory]::Exists($value) -or $value.Contains(';')) {
        throw "$name must name an existing shared content directory without a semicolon."
    }
    Set-Variable -Name $name -Value $value
    $property = if ($name -eq 'ProjectContentRoot') { 'SomeEngineProjectRoot' } else { $name }
    $properties += "-p:$($property)=$value"
}
$results = [IO.Path]::GetFullPath($ResultsDirectory, $repository)
$existingResults = Join-Path $repository 'samples/DefaultGame/obj'
if ($results.TrimEnd([IO.Path]::DirectorySeparatorChar) -ne $existingResults) {
    throw "Build logs must use the application's normal intermediate directory: $existingResults"
}
[IO.Directory]::CreateDirectory($results) | Out-Null

# Only the declared projects' existing bin/obj build outputs are counted.
# Do not follow dependency links into another tree or scan unrelated disk contents.
[xml]$solution = Get-Content -LiteralPath (Join-Path $repository 'SomeEngine.slnx') -Raw
$outputRoots = @($solution.SelectNodes('//Project[@Path]') | ForEach-Object {
    $project = [IO.Path]::GetFullPath($_.Path, $repository)
    if (!$project.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Project output lies outside this worktree: $project"
    }
    $directory = [IO.Path]::GetDirectoryName($project)
    Join-Path $directory 'bin'
    Join-Path $directory 'obj'
})
$outputRoots += Join-Path $repository 'samples/DefaultGame/bin'
$outputRoots += Join-Path $repository 'samples/DefaultGame/obj'
$outputRoots = @($outputRoots | Sort-Object -Unique)
function Get-BuildOutputUsage {
    [long]$bytes = 0
    $directories = [Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    foreach ($path in $outputRoots) {
        $directory = [IO.DirectoryInfo]::new($path)
        if (!$directory.Exists) { continue }
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Build output must remain inside this worktree: $path"
        }
        $directories.Push($directory)
    }
    while ($directories.Count -gt 0) {
        foreach ($entry in $directories.Pop().EnumerateFileSystemInfos()) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($entry -is [IO.DirectoryInfo]) {
                if ($entry.Name -notin @('assets', 'Library', 'Performance', 'Captures')) {
                    $directories.Push($entry)
                }
            } else { $bytes += $entry.Length }
        }
    }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($repository))
    [pscustomobject]@{ OutputBytes = $bytes; FreeBytes = $drive.AvailableFreeSpace }
}
$before = Get-BuildOutputUsage
if ($before.FreeBytes -lt 2GB) { throw 'At least 2 GiB of free space is required before this incremental build.' }
Write-Host "Build outputs before: $($before.OutputBytes) bytes; free: $($before.FreeBytes) bytes."
$applicationProject = 'samples/DefaultGame/DefaultGame.csproj'
$buildTarget = if ($ApplicationOnly) { $applicationProject }
    else { 'SomeEngine.slnx' }
Push-Location -LiteralPath $repository
$buildExitCode = 0
try {
    # Content updates are explicit; ordinary code builds reuse the existing assets.
    if ($UpdateContent) {
        # Existing test projects consume these generated shader assets during compilation.
        & dotnet run --project $applicationProject -c Debug @properties -- sample 2>&1 | Tee-Object (Join-Path $results 'content.log')
        $buildExitCode = $LASTEXITCODE
    }
    if ($buildExitCode -eq 0) {
        & dotnet build $buildTarget -c $Configuration @properties -v minimal 2>&1 | Tee-Object (Join-Path $results 'build.log')
        $buildExitCode = $LASTEXITCODE
    }
    if ($buildExitCode -eq 0 -and $Configuration -eq 'Release') {
        # Let SomeEngine.Game.props and the SDK own RID and publish output paths.
        & dotnet publish $applicationProject -c $Configuration @properties -v minimal `
            2>&1 | Tee-Object (Join-Path $results 'publish.log')
        $buildExitCode = $LASTEXITCODE
    }
}
finally {
    Pop-Location
    $after = Get-BuildOutputUsage
    $growth = $after.OutputBytes - $before.OutputBytes
    Write-Host "Build outputs after: $($after.OutputBytes) bytes; growth: $growth bytes; free: $($after.FreeBytes) bytes."
    if ($growth -gt 2GB -or $after.FreeBytes -lt 1GB) {
        throw 'Build output grew by more than 2 GiB or free space fell below 1 GiB. Stop before another build; no files were removed.'
    }
}
exit $buildExitCode
