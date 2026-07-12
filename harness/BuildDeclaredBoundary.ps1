param(
    [string]$Configuration = "Debug",
    [string]$Verbosity = "minimal",
    [ValidateSet("All", "Source")]
    [string]$ProjectSet = "All",
    [switch]$NoRestore,
    [switch]$NoIncremental,
    [switch]$QualityAnalyzerEnabled,
    [string]$NoWarn = "",
    [string]$WarningsAsErrors = "",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$configPath = Join-Path $repoRoot "harness\config.json"
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

$projectPaths = [System.Collections.Generic.List[string]]::new()

function Add-ProjectPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $normalized = $Path.Replace("/", "\")
    if (-not $projectPaths.Contains($normalized)) {
        $projectPaths.Add($normalized)
    }
}

foreach ($sourceProject in $config.externalDependencies.sourceProjects) {
    Add-ProjectPath $sourceProject.path
}

foreach ($localPackage in $config.externalDependencies.localPackages) {
    Add-ProjectPath $localPackage.producerProject
}

foreach ($project in $config.projects.buildSupportProjects) {
    Add-ProjectPath $project.path
}

foreach ($project in $config.projects.productProjects) {
    Add-ProjectPath $project.path
}

if ($ProjectSet -eq "All") {
    foreach ($project in $config.projects.testProjects) {
        Add-ProjectPath $project.path
    }

    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repoRoot "harness") -Recurse -Filter *.csproj) {
        $relative = [System.IO.Path]::GetRelativePath($repoRoot, $project.FullName)
        if ($relative -notmatch "\\bin\\" -and $relative -notmatch "\\obj\\") {
            Add-ProjectPath $relative
        }
    }
}

foreach ($relativePath in $projectPaths) {
    $fullPath = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw "Declared boundary project does not exist: $relativePath"
    }
}

$solutionPath = Join-Path $repoRoot ".declared-boundary-$ProjectSet-$PID.slnx"
$xmlSettings = [System.Xml.XmlWriterSettings]::new()
$xmlSettings.Indent = $true
$xmlSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
$writer = $null
try {
    $writer = [System.Xml.XmlWriter]::Create($solutionPath, $xmlSettings)
    $writer.WriteStartElement("Solution")
    foreach ($relativePath in $projectPaths) {
        $writer.WriteStartElement("Project")
        $writer.WriteAttributeString("Path", $relativePath)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.Flush()
    $writer.Dispose()
    $writer = $null

    Write-Host "Building declared boundary graph: $($projectPaths.Count) projects in one MSBuild invocation."
    $buildArgs = @(
        "build",
        $solutionPath,
        "--configuration",
        $Configuration,
        "-v",
        $Verbosity,
        "-m",
        # Package availability belongs to this build gate; the separately hosted
        # NuGet vulnerability feed must not turn a successful restore into NU1900.
        "/p:NuGetAudit=false"
    )

    if ($NoRestore) {
        $buildArgs += "--no-restore"
    }

    if ($NoIncremental) {
        $buildArgs += "--no-incremental"
    }

    if ($QualityAnalyzerEnabled) {
        $buildArgs += "/p:SomeEngineQualityAnalyzerEnabled=true"
    }

    if (-not [string]::IsNullOrWhiteSpace($NoWarn)) {
        $buildArgs += "/p:NoWarn=$NoWarn"
    }

    if (-not [string]::IsNullOrWhiteSpace($WarningsAsErrors)) {
        $buildArgs += "/p:WarningsAsErrors=$WarningsAsErrors"
    }

    if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
        $artifactsPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
        $buildArgs += "/p:UseArtifactsOutput=true"
        $buildArgs += "/p:ArtifactsPath=$artifactsPath"
    }

    & dotnet @buildArgs
    $buildExitCode = $LASTEXITCODE
}
finally {
    if ($null -ne $writer) {
        $writer.Dispose()
    }
    Remove-Item -LiteralPath $solutionPath -Force -ErrorAction SilentlyContinue
}

if ($buildExitCode -ne 0) {
    exit $buildExitCode
}
