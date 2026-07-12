using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class AnalyzerConfigLoader
{
    private const string ConfigFileName = "config.json";
    private const string AcceptedBaselineFileName = "graphics-hard-baseline.v1.json";

    public static AnalyzerHarnessConfig Load(AnalyzerOptions options)
    {
        var file = options.AdditionalFiles
            .FirstOrDefault(candidate => System.IO.Path.GetFileName(candidate.Path) == ConfigFileName);
        if (file is null)
        {
            return new AnalyzerHarnessConfig();
        }

        var text = file.GetText()?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AnalyzerHarnessConfig();
        }

        var optionsJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        AnalyzerHarnessConfig config =
            JsonSerializer.Deserialize<AnalyzerHarnessConfig>(text!, optionsJson) ?? new AnalyzerHarnessConfig();
        LoadAcceptedBaseline(options, optionsJson, config);
        return config;
    }

    private static void LoadAcceptedBaseline(
        AnalyzerOptions options,
        JsonSerializerOptions jsonOptions,
        AnalyzerHarnessConfig config)
    {
        var file = options.AdditionalFiles.FirstOrDefault(candidate =>
            System.IO.Path.GetFileName(candidate.Path) == AcceptedBaselineFileName);
        string? text = file?.GetText()?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        AnalyzerAcceptedBaselineDocument? baseline =
            JsonSerializer.Deserialize<AnalyzerAcceptedBaselineDocument>(text!, jsonOptions);
        if (baseline is null)
        {
            return;
        }

        config.Complexity.AcceptedCheckpointCommit = baseline.CheckpointCommit;
        config.Complexity.AcceptedCheckpointDiagnostics = baseline.Entries;
    }
}

internal sealed class AnalyzerAcceptedBaselineDocument
{
    public int SchemaVersion { get; set; }
    public string CheckpointCommit { get; set; } = "";
    public List<AnalyzerAcceptedDiagnostic> Entries { get; set; } = [];
}

internal sealed class AnalyzerHarnessConfig
{
    public AnalyzerNamingConfig Naming { get; set; } = new();
    public AnalyzerStyleConfig Style { get; set; } = new();
    public AnalyzerComplexityConfig Complexity { get; set; } = new();
}

internal sealed class AnalyzerNamingConfig
{
    public List<string> ForbiddenClassSuffixes { get; set; } = [];
    public List<string> ForbiddenMethodSuffixes { get; set; } = [];
    public List<string> ClassWhitelist { get; set; } = [];
}

internal sealed class AnalyzerStyleConfig
{
    public List<string> AllowedVarContexts { get; set; } = [];
    public bool WarnOnImplicitCast { get; set; } = true;
}

internal sealed class AnalyzerComplexityConfig
{
    public int MaxCyclomaticComplexity { get; set; } = 12;
    public int MaxMethodLines { get; set; } = 60;
    public int MaxMethodsPerClass { get; set; } = 25;
    public int MaxFieldsPerClass { get; set; } = 20;
    public int MaxCoupledTypes { get; set; } = 8;
    public string AcceptedCheckpointCommit { get; set; } = "";
    public List<AnalyzerAcceptedDiagnostic> AcceptedCheckpointDiagnostics { get; set; } = [];
}

internal sealed class AnalyzerAcceptedDiagnostic
{
    public string Id { get; set; } = "";
    public string Assembly { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    public string Symbol { get; set; } = "";
    public int MaximumObserved { get; set; }
    public string Reason { get; set; } = "";
}
