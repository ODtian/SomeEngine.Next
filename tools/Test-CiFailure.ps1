[CmdletBinding()]
param([switch] $DemonstrateFailure)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporary = Join-Path $repository ('.tmp/ci-diagnostics-' + [Guid]::NewGuid().ToString('N'))
$results = if ($DemonstrateFailure) { Join-Path $repository 'artifacts/test-results/ci-diagnostics' }
    else { Join-Path $temporary 'results' }
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $project = Join-Path $temporary 'CiDiagnostics.Tests.csproj'
    [IO.File]::WriteAllText($project, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
</Project>
"@)
    [IO.File]::WriteAllText((Join-Path $temporary 'DiagnosticsTests.cs'), @"
using System;
using System.IO;
using Xunit;
using Xunit.Abstractions;

public class DiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public void SoftwareFailureRetainsImageAndLog()
    {
        string directory = Environment.GetEnvironmentVariable("SOMEENGINE_TEST_ARTIFACTS")!;
        File.WriteAllBytes(Path.Combine(directory, "failure.png"), Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII="));
        output.WriteLine("software failure diagnostic");
        Assert.Fail("Intentional software failure for CI diagnostics verification.");
    }

    [Fact, Trait("Category", "Hardware")]
    public void HardwareCategoryHasAnIndependentEntry() => Assert.Fail("Hardware selected.");

    [Fact, Trait("Category", "LongFuzz")]
    public void LongFuzzCategoryHasAnIndependentEntry() => Assert.Fail("Long fuzz selected.");

    [Fact, Trait("Category", "Performance")]
    public void PerformanceCategoryHasAnIndependentEntry() => Assert.Fail("Performance selected.");
}
"@)
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test.ps1') -Project $project -ResultsDirectory $results
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -eq 0) { throw 'The intentional software failure must fail the test command.' }
    $trx = Get-ChildItem -LiteralPath $results -Filter '*.trx' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    [xml] $report = Get-Content -Raw -LiteralPath $trx.FullName
    $counters = $report.TestRun.ResultSummary.Counters
    if ($counters.total -ne '1' -or $counters.failed -ne '1') {
        throw 'The default command must execute only the software failure.'
    }
    if ($report.TestRun.Results.UnitTestResult.Output.StdOut -notmatch 'software failure diagnostic') {
        throw 'TRX must retain the failing test output.'
    }
    if ((Get-Item -LiteralPath (Join-Path $results 'failure.png')).Length -eq 0) {
        throw 'The failure image must reach the workflow artifact directory.'
    }
    if ((Get-Item -LiteralPath (Join-Path $results 'Software.log')).Length -eq 0) {
        throw 'The test command must retain its console log.'
    }
    Write-Host 'Verified: software failure exits nonzero; TRX, console log and PNG are retained; hardware, long fuzz and performance are filtered.'
    if ($DemonstrateFailure) { exit $testExitCode }
}
finally {
    $temporary = [IO.Path]::GetFullPath($temporary)
    $temporaryRoot = [IO.Path]::GetFullPath((Join-Path $repository '.tmp')) + [IO.Path]::DirectorySeparatorChar
    if (!$temporary.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CI diagnostics cleanup must stay inside the repository temporary directory.'
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
