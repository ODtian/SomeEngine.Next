param(
    [string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configPath = Join-Path $repoRoot "harness\config.json"
$config = Get-Content -Raw $configPath | ConvertFrom-Json
$resultsRoot = Join-Path $repoRoot "harness\coverage\results"
$reportPath = Join-Path $repoRoot $config.coverage.reportPath

if (Test-Path $resultsRoot) {
    Remove-Item -LiteralPath $resultsRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $resultsRoot | Out-Null

$testProjects = @($config.projects.testProjects |
    ForEach-Object { Get-Item (Join-Path $repoRoot $_.path) } |
    Sort-Object FullName)

$coverageFilters = @($config.coverage.excludedTestTraits |
    ForEach-Object { "$($_.name)!=$($_.value)" })
$noBuildArgs = @()
if ($NoBuild) {
    $noBuildArgs = @("--no-build")
}

$coverageFilterArgs = @()
if ($coverageFilters.Count -gt 0) {
    $coverageFilterArgs = @("--filter", ($coverageFilters -join "&"))
}

foreach ($project in $testProjects) {
    dotnet test $project.FullName @noBuildArgs --no-restore --configuration $Configuration --verbosity quiet @coverageFilterArgs --collect "XPlat Code Coverage" --results-directory $resultsRoot
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$required = @{}
foreach ($assembly in $config.coverage.requiredAssemblies) {
    $required[$assembly] = $true
}

$document = New-Object System.Xml.XmlDocument
$declaration = $document.CreateXmlDeclaration("1.0", "utf-8", $null)
$document.AppendChild($declaration) | Out-Null
$coverage = $document.CreateElement("coverage")
$coverage.SetAttribute("version", "1.9")
$coverage.SetAttribute("timestamp", [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString())
$document.AppendChild($coverage) | Out-Null

$sources = $document.CreateElement("sources")
$source = $document.CreateElement("source")
$source.InnerText = $repoRoot
$sources.AppendChild($source) | Out-Null
$coverage.AppendChild($sources) | Out-Null

$packagesNode = $document.CreateElement("packages")
$coverage.AppendChild($packagesNode) | Out-Null

$seen = @{}
$lineFacts = @{}
$linesCovered = 0
$linesValid = 0
$branchesCovered = 0
$branchesValid = 0

function Read-BranchCoverage($line) {
    $coverageText = $line."condition-coverage"
    if ($null -eq $coverageText) {
        return @{ Covered = 0; Valid = 0 }
    }

    $match = [regex]::Match([string]$coverageText, "\((\d+)/(\d+)\)")
    if (-not $match.Success) {
        return @{ Covered = 0; Valid = 0 }
    }

    return @{
        Covered = [int]$match.Groups[1].Value
        Valid = [int]$match.Groups[2].Value
    }
}

foreach ($file in Get-ChildItem $resultsRoot -Recurse -Filter coverage.cobertura.xml) {
    [xml]$xml = Get-Content -Raw $file.FullName
    foreach ($package in $xml.coverage.packages.package) {
        if (-not $required.ContainsKey($package.name)) {
            continue
        }

        $seen[$package.name] = $true

        foreach ($class in $package.classes.class) {
            $className = [string]$class.name
            $classFile = [string]$class.filename
            foreach ($method in $class.methods.method) {
                foreach ($line in $method.lines.line) {
                    $key = "{0}|{1}|{2}|{3}" -f $package.name, $className, $classFile, $line.number
                    if (-not $lineFacts.ContainsKey($key)) {
                        $lineFacts[$key] = @{
                            Package = [string]$package.name
                            Hits = 0
                            BranchCovered = 0
                            BranchValid = 0
                        }
                    }

                    $fact = $lineFacts[$key]
                    $fact.Hits = [Math]::Max([int]$fact.Hits, [int]$line.hits)

                    if ($line.branch -eq "True") {
                        $branch = Read-BranchCoverage $line
                        $fact.BranchCovered = [Math]::Max([int]$fact.BranchCovered, [int]$branch.Covered)
                        $fact.BranchValid = [Math]::Max([int]$fact.BranchValid, [int]$branch.Valid)
                    }
                }
            }
        }
    }
}

$packageStats = @{}
foreach ($assembly in $config.coverage.requiredAssemblies) {
    $packageStats[$assembly] = @{
        LinesCovered = 0
        LinesValid = 0
        BranchesCovered = 0
        BranchesValid = 0
    }
}

foreach ($fact in $lineFacts.Values) {
    $stats = $packageStats[$fact.Package]
    $stats.LinesValid++
    if ([int]$fact.Hits -gt 0) {
        $stats.LinesCovered++
    }

    $stats.BranchesCovered += [int]$fact.BranchCovered
    $stats.BranchesValid += [int]$fact.BranchValid
}

foreach ($assembly in $config.coverage.requiredAssemblies) {
    $stats = $packageStats[$assembly]
    $linesCovered += [int]$stats.LinesCovered
    $linesValid += [int]$stats.LinesValid
    $branchesCovered += [int]$stats.BranchesCovered
    $branchesValid += [int]$stats.BranchesValid

    $packageNode = $document.CreateElement("package")
    $packageNode.SetAttribute("name", $assembly)
    $packageLineRate = if ([int]$stats.LinesValid -eq 0) { 0.0 } else { [double]$stats.LinesCovered / [double]$stats.LinesValid }
    $packageBranchRate = if ([int]$stats.BranchesValid -eq 0) { 0.0 } else { [double]$stats.BranchesCovered / [double]$stats.BranchesValid }
    $packageNode.SetAttribute("line-rate", $packageLineRate.ToString([Globalization.CultureInfo]::InvariantCulture))
    $packageNode.SetAttribute("branch-rate", $packageBranchRate.ToString([Globalization.CultureInfo]::InvariantCulture))
    $packagesNode.AppendChild($packageNode) | Out-Null
}

$lineRate = if ($linesValid -eq 0) { 0.0 } else { [double]$linesCovered / $linesValid }
$branchRate = if ($branchesValid -eq 0) { 0.0 } else { [double]$branchesCovered / $branchesValid }

$coverage.SetAttribute("line-rate", $lineRate.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("branch-rate", $branchRate.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("lines-covered", $linesCovered.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("lines-valid", $linesValid.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("branches-covered", $branchesCovered.ToString([Globalization.CultureInfo]::InvariantCulture))
$coverage.SetAttribute("branches-valid", $branchesValid.ToString([Globalization.CultureInfo]::InvariantCulture))

$reportDirectory = Split-Path -Parent $reportPath
if (-not (Test-Path $reportDirectory)) {
    New-Item -ItemType Directory -Path $reportDirectory | Out-Null
}
$document.Save($reportPath)

$missing = @($config.coverage.requiredAssemblies | Where-Object { -not $seen.ContainsKey($_) })
if ($missing.Count -gt 0) {
    Write-Error ("Coverage report is missing migrated product assemblies: " + ($missing -join ", "))
    exit 1
}

Write-Host ("Coverage report: {0}" -f $reportPath)
Write-Host ("Line coverage: {0}/{1} ({2:P2})" -f $linesCovered, $linesValid, $lineRate)
Write-Host ("Branch coverage: {0}/{1} ({2:P2})" -f $branchesCovered, $branchesValid, $branchRate)

