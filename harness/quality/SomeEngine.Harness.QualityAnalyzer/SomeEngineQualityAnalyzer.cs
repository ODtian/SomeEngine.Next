using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SomeEngine.Harness.QualityAnalyzer;

/// <summary>
/// Aggregates all SomeEngine code-quality diagnostic rules. Each rule lives in
/// its own analyzer file; this class only wires them into the analyzer manager.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SomeEngineQualityAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get
        {
            var descriptors = ImmutableArray.CreateBuilder<DiagnosticDescriptor>();
            descriptors.AddRange(NamingBlacklistAnalyzer.Descriptors);
            descriptors.AddRange(VarUsageAnalyzer.Descriptors);
            descriptors.AddRange(CyclomaticComplexityAnalyzer.Descriptors);
            descriptors.AddRange(MethodLengthAnalyzer.Descriptors);
            descriptors.AddRange(ClassSizeAnalyzer.Descriptors);
            descriptors.AddRange(CouplingComplexityAnalyzer.Descriptors);
            descriptors.AddRange(DuplicateEnumAnalyzer.Descriptors);
            descriptors.AddRange(ReflectionUsageAnalyzer.Descriptors);
            descriptors.AddRange(HardcodedLiteralAnalyzer.Descriptors);
            return descriptors.ToImmutable();
        }
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        NamingBlacklistAnalyzer.Register(context);

        VarUsageAnalyzer.Register(context);
        CyclomaticComplexityAnalyzer.Register(context);
        MethodLengthAnalyzer.Register(context);
        ClassSizeAnalyzer.Register(context);
        CouplingComplexityAnalyzer.Register(context);
        DuplicateEnumAnalyzer.Register(context);
        ReflectionUsageAnalyzer.Register(context);
        HardcodedLiteralAnalyzer.Register(context);
    }
}




