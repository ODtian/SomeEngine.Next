using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;

namespace SomeEngine.Harness.QualityAnalyzer.Tests;

internal sealed class OfflineAnalyzerTest : CSharpAnalyzerTest<SomeEngineQualityAnalyzer, DefaultVerifier>
{
    private static readonly ImmutableArray<MetadataReference> RuntimeReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(File.Exists)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToImmutableArray<MetadataReference>();

    public OfflineAnalyzerTest()
    {
        // An empty package identity prevents the Roslyn test framework from contacting
        // NuGet at execution time. The installed runtime's trusted platform assemblies
        // provide the deterministic local compilation surface instead.
        ReferenceAssemblies = new ReferenceAssemblies("net10.0");
        TestState.AdditionalReferences.AddRange(RuntimeReferences);
    }
}
