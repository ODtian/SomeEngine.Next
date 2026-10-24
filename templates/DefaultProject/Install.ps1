param(
    [Parameter(Mandatory = $true)][string] $Destination,
    [Parameter(Mandatory = $true)][string] $EnginePath
)
$ErrorActionPreference = 'Stop'
$templateRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$projectRoot = [IO.Path]::GetFullPath($Destination)
$engineRoot = (Resolve-Path -LiteralPath $EnginePath).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $engineRoot 'SomeEngine.slnx'))) {
    throw 'EnginePath must identify a SomeEngine source checkout.'
}
if (Test-Path -LiteralPath $projectRoot) {
    if (-not (Test-Path -LiteralPath $projectRoot -PathType Container) -or
        @(Get-ChildItem -LiteralPath $projectRoot -Force).Count -ne 0) {
        throw 'The destination must be absent or empty. Installation never overwrites an existing project.'
    }
}
$pathSeparators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$projectPrefix = $projectRoot.TrimEnd($pathSeparators) + [IO.Path]::DirectorySeparatorChar
$templatePrefix = $templateRoot.TrimEnd($pathSeparators) + [IO.Path]::DirectorySeparatorChar
if ($projectRoot.StartsWith($templatePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Install outside the template source directory.'
}
$copies = @()
foreach ($source in Get-ChildItem -LiteralPath $templateRoot -Recurse -File -Force) {
    if ($source.FullName -match '[\\/](bin|obj|Library|CrowdInline|CrowdCache|\.git)[\\/]' -or
        $source.FullName -match '[\\/]assets[\\/]Shaders[\\/]generated[\\/]' -or
        $source.Name -match '\.asset$|\.import\.json$|\.(tmp|temp|bak)$|~$' -or
        $source.Name -eq 'EnginePath.props' -or $source.Name -eq 'NuGet.config') { continue }
    $relative = [IO.Path]::GetRelativePath($templateRoot, $source.FullName)
    $target = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
    if (-not $source.FullName.StartsWith($templatePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $target.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A template copy escaped the selected source or destination directory.'
    }
    $copies += [pscustomobject]@{ Source = $source.FullName; Target = $target }
}
New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
foreach ($copy in $copies) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($copy.Target)) -Force | Out-Null
    [IO.File]::Copy($copy.Source, $copy.Target, $false)
}
$relativeEngine = [IO.Path]::GetRelativePath($projectRoot, $engineRoot).Replace('\', '/')
$escapedEngine = [Security.SecurityElement]::Escape($relativeEngine)
[IO.File]::WriteAllText((Join-Path $projectRoot 'EnginePath.props'), "<Project><PropertyGroup><SomeEnginePath>$escapedEngine</SomeEnginePath></PropertyGroup></Project>`n")
$nugetSource = Join-Path $templateRoot 'NuGet.config'
if (-not (Test-Path -LiteralPath $nugetSource)) { $nugetSource = Join-Path $engineRoot 'NuGet.config' }
[xml] $nuget = Get-Content -Raw -LiteralPath $nugetSource
foreach ($source in $nuget.configuration.packageSources.add) {
    if ($source.key -eq 'SomeEngine Local Packages') {
        $source.value = [IO.Path]::GetRelativePath($projectRoot, (Join-Path $engineRoot 'artifacts/local-packages')).Replace('\', '/')
    }
}
$nuget.Save((Join-Path $projectRoot 'NuGet.config'))
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'Game.slnx'))) {
    [IO.File]::WriteAllText((Join-Path $projectRoot 'Game.slnx'), @'
<Solution>
  <Project Path="Game/Game.csproj" />
  <Project Path="Game.Assets/Game.Assets.csproj" />
  <Project Path="Game.Importing/Game.Importing.csproj" />
  <Project Path="Game.Build/Game.Build.csproj" />
  <Project Path="Game.App/Game.App.csproj" />
</Solution>
'@)
}
Write-Output "Created project sources in $projectRoot"
Write-Output 'Build Game.Build with --project and --engine, then run Game.App. Engine updates do not replace these sources.'
