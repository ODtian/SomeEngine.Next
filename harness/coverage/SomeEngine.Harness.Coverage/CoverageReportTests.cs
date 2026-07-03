using System.Globalization;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Coverage;

public sealed class CoverageReportTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void CoberturaReportCoversRequiredProductAssemblies()
    {
        var reportPath = Path.Combine(HarnessConfig.ResolveRepoRoot(), Config.Coverage.ReportPath);
        Assert.True(
            File.Exists(reportPath),
            $"Coverage report must exist at {Config.Coverage.ReportPath}. Generate it with dotnet test --collect \"XPlat Code Coverage\" and merge to this Cobertura path.");

        var document = XDocument.Load(reportPath);
        var root = document.Root ?? throw new InvalidDataException("Coverage report has no root element.");
        var lineRate = ReadRate(root, "line-rate");
        var branchRate = ReadRate(root, "branch-rate");

        Assert.True(lineRate >= Config.Coverage.MinLineRate,
            $"Line coverage {lineRate:P2} is below required {Config.Coverage.MinLineRate:P2}.");
        Assert.True(branchRate >= Config.Coverage.MinBranchRate,
            $"Branch coverage {branchRate:P2} is below required {Config.Coverage.MinBranchRate:P2}.");

        var coveredAssemblies = document.Descendants()
            .Where(element => element.Name.LocalName == "package")
            .Select(element => element.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var missing = Config.Coverage.RequiredAssemblies
            .Where(assembly => !coveredAssemblies.Contains(assembly))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Coverage report is missing required product assemblies:\n" + string.Join("\n", missing));
    }

    private static double ReadRate(XElement element, string attributeName)
    {
        var value = element.Attribute(attributeName)?.Value;
        Assert.False(string.IsNullOrWhiteSpace(value), $"Coverage report root must contain {attributeName}.");
        return double.Parse(value!, CultureInfo.InvariantCulture);
    }
}