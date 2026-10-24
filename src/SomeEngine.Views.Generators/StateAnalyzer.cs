using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Views.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StateAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor PropertyRequired = new(
        "SEV015", "Declare a view state property",
        "Declare '{0}' as an instance partial property with get and set on a partial struct view",
        "Views", DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [PropertyRequired];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(static analysis =>
        {
            var property = (IPropertySymbol)analysis.Symbol;
            if (StatePropertyDeclarations.HasStateAttribute(property) && !StatePropertyDeclarations.CanImplement(property))
                analysis.ReportDiagnostic(Diagnostic.Create(PropertyRequired, property.Locations[0], property.Name));
        }, SymbolKind.Property);
    }
}
