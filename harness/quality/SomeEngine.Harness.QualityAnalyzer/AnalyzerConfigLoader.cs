using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class AnalyzerConfigLoader
{
    private const string ConfigFileName = "config.json";

    private static AnalyzerHarnessConfig? _cached;
    private static readonly object Gate = new();

    public static AnalyzerHarnessConfig Load(AnalyzerOptions options)
    {
        lock (Gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }
        }

        var file = options.AdditionalFiles
            .FirstOrDefault(candidate => System.IO.Path.GetFileName(candidate.Path) == ConfigFileName);
        if (file is null)
        {
            return Cache(new AnalyzerHarnessConfig());
        }

        var text = file.GetText()?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Cache(new AnalyzerHarnessConfig());
        }

        var optionsJson = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        return Cache(JsonSerializer.Deserialize<AnalyzerHarnessConfig>(text!, optionsJson) ?? new AnalyzerHarnessConfig());
    }

    private static AnalyzerHarnessConfig Cache(AnalyzerHarnessConfig config)
    {
        lock (Gate)
        {
            _cached = config;
            return _cached;
        }
    }
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
}
