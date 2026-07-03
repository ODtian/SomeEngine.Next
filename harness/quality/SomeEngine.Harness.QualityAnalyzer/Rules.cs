using Microsoft.CodeAnalysis;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class Rules
{
    public const string CategoryNaming = "Naming";
    public const string CategoryStyle = "Style";
    public const string CategoryComplexity = "Complexity";
    public const string CategorySafety = "Safety";

    public static DiagnosticDescriptor Create(
        string id,
        string title,
        string messageFormat,
        string category,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
        => new(
            id: id,
            title: title,
            messageFormat: messageFormat,
            category: category,
            defaultSeverity: severity,
            isEnabledByDefault: true,
            description: title);
}


