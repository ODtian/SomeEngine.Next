using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class AcceptedDiagnosticBaseline
{
    public static bool Contains(
        SyntaxNodeAnalysisContext context,
        AnalyzerHarnessConfig config,
        string id,
        SyntaxToken identifier,
        int observed)
    {
        string assembly = context.SemanticModel.Compilation.AssemblyName ?? string.Empty;
        string path = (identifier.SyntaxTree?.FilePath ?? string.Empty).Replace('\\', '/');
        int line = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        string symbol = identifier.ValueText;

        return config.Complexity.AcceptedCheckpointDiagnostics.Any(entry =>
            entry.Id.Equals(id, StringComparison.Ordinal) &&
            entry.Assembly.Equals(assembly, StringComparison.Ordinal) &&
            path.EndsWith(entry.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) &&
            entry.Line == line &&
            entry.Symbol.Equals(symbol, StringComparison.Ordinal) &&
            observed <= entry.MaximumObserved &&
            !string.IsNullOrWhiteSpace(entry.Reason));
    }
}
