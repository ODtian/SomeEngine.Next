using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

public sealed class AnalyzerRegistrationTests
{
    [Fact]
    public void SupportedDiagnosticsAreExactlyTheConfiguredHarnessRules()
    {
        var analyzer = new SomeEngineQualityAnalyzer();
        var actual = analyzer.SupportedDiagnostics
            .Select(descriptor => descriptor.Id)
            .OrderBy(id => id)
            .ToArray();

        var expected = new[]
        {
            "SE001",
            "SE002",
            "SE010",
            "SE020",
            "SE021",
            "SE022",
            "SE023",
            "SE024",
            "SE030",
            "SE031",
            "SE052",
        };

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NamingReflectionAndHardcodeAreTheOnlyForbiddenRuleSet()
    {
        var analyzer = new SomeEngineQualityAnalyzer();
        var blacklistDescriptors = analyzer.SupportedDiagnostics
            .Where(descriptor => descriptor.Title.ToString().Contains("Forbidden"))
            .Select(descriptor => descriptor.Id)
            .OrderBy(id => id)
            .ToArray();

        Assert.Equal(new[] { "SE001", "SE002", "SE030" }, blacklistDescriptors);
    }

    [Fact]
    public void FirstRoundHardRulesAreErrorsAndStyleRulesAreWarnings()
    {
        var analyzer = new SomeEngineQualityAnalyzer();
        var severities = analyzer.SupportedDiagnostics
            .ToDictionary(descriptor => descriptor.Id, descriptor => descriptor.DefaultSeverity);

        foreach (string id in new[] { "SE001", "SE002", "SE020", "SE021", "SE022", "SE023", "SE024", "SE030" })
        {
            Assert.Equal(DiagnosticSeverity.Error, severities[id]);
        }

        foreach (string id in new[] { "SE010", "SE031", "SE052" })
        {
            Assert.Equal(DiagnosticSeverity.Warning, severities[id]);
        }
    }
}
