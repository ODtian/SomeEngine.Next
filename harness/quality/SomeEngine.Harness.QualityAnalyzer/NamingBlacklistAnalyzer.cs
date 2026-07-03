using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

internal static class NamingBlacklistAnalyzer
{
    public static ImmutableArray<DiagnosticDescriptor> Descriptors { get; } =
    [
        Rules.Create(
            "SE001",
            "Forbidden class name suffix",
            "Class '{0}' uses a forbidden suffix",
            Rules.CategoryNaming),
        Rules.Create(
            "SE002",
            "Forbidden method name suffix",
            "Method '{0}' uses a forbidden suffix",
            Rules.CategoryNaming),
    ];

    public static void Register(AnalysisContext context)
    {
        context.RegisterSymbolAction(symbolContext =>
        {
            var config = AnalyzerConfigLoader.Load(symbolContext.Options);
            var classSuffixes = config.Naming.ForbiddenClassSuffixes;
            var methodSuffixes = config.Naming.ForbiddenMethodSuffixes;
            var whitelist = new HashSet<string>(config.Naming.ClassWhitelist);

            if (symbolContext.Symbol is INamedTypeSymbol typeSymbol)
            {
                if (!whitelist.Contains(typeSymbol.Name) && EndsWithAny(typeSymbol.Name, classSuffixes))
                {
                    symbolContext.ReportDiagnostic(Diagnostic.Create(Descriptors[0], typeSymbol.Locations[0], typeSymbol.Name));
                }
            }

            if (symbolContext.Symbol is IMethodSymbol methodSymbol)
            {
                if (methodSymbol.MethodKind == MethodKind.Ordinary && EndsWithAny(methodSymbol.Name, methodSuffixes))
                {
                    symbolContext.ReportDiagnostic(Diagnostic.Create(Descriptors[1], methodSymbol.Locations[0], methodSymbol.Name));
                }
            }
        }, SymbolKind.NamedType, SymbolKind.Method);
    }

    private static bool EndsWithAny(string value, IReadOnlyList<string> suffixes)
    {
        for (var index = 0; index < suffixes.Count; index++)
        {
            if (value.EndsWith(suffixes[index], System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

