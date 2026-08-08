using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.SourceGen;
using SomeEngine.ECS.Systems;
using System.Collections.Immutable;

namespace SomeEngine.ECS.SourceGen.Tests;

public sealed class JobEntityGeneratorDiagnosticsTests
{
    [Fact]
    public void GeneratesImmutableAccessAndParallelAdapterForUnmanagedDirectSignature()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

public struct Position : SomeEngine.ECS.IComponent { public int X; }
public struct Velocity : SomeEngine.ECS.IComponent { public int X; }

public partial struct Integrate : IJobEntity
{
    public void Execute(Entity entity, in Velocity velocity, ref Position position)
    {
        position.X += velocity.X + entity.Index;
    }
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
        Assert.Contains("GeneratedQueryAccessDescriptor", generated);
        Assert.Contains("ScheduleParallel", generated);
        Assert.Contains("row.Read<", generated);
        Assert.Contains("row.ReadWrite<", generated);
    }

    [Fact]
    public void RejectsDuplicateDirectAliases()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct Position : SomeEngine.ECS.IComponent { public int X; }
public partial struct Invalid : IJobEntity
{
    public void Execute(in Position first, ref Position second) { }
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG102");
    }

    [Fact]
    public void RejectsWritableDerivedRelationshipComponent()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct Derived : IRelationshipTarget { public int Count; }
public partial struct Invalid : IJobEntity
{
    public void Execute(ref Derived derived) { }
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG103");
    }

    [Fact]
    public void RejectsManagedJobFields()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct Position : SomeEngine.ECS.IComponent { public int X; }
public partial struct Invalid : IJobEntity
{
    public string Name;
    public void Execute(ref Position position) { }
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG104");
    }

    [Fact]
    public void RejectsManagedAutoPropertyBackingFields()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct Position : SomeEngine.ECS.IComponent { public int X; }
public partial struct Invalid : IJobEntity
{
    public string Name { get; set; }
    public void Execute(ref Position position) { }
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG104");
    }

    [Fact]
    public void RejectsManagedReferenceBearingDirectStorage()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct ManagedComponent : SomeEngine.ECS.IComponent { public string Name; }
public struct ManagedBuffer : IBufferElement { public object Value; }
public partial struct Invalid : IJobEntity
{
    public void Execute(in ManagedComponent component, BufferView<ManagedBuffer> buffer) { }
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG110");
    }

    [Theory]
    [InlineData("public nint Handle { get; set; }")]
    [InlineData("public System.IntPtr Handle { get; set; }")]
    [InlineData("public nuint Handle { get; set; }")]
    public void RejectsNativeSizedAutoPropertyBackingFields(string member)
    {
        GeneratorTestResult result = RunGenerator($$"""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct NativeHandleComponent : SomeEngine.ECS.IComponent
{
    {{member}}
}
public partial struct Invalid : IJobEntity
{
    public void Execute(in NativeHandleComponent component) { }
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG110");
    }

    [Fact]
    public void SafeAutoPropertiesAndFrameworkValueFieldsKeepStaticAliasProof()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct SafeLeaf
{
    public long Value { get; set; }
}
public struct SafeComponent : SomeEngine.ECS.IComponent
{
    public SafeLeaf Leaf { get; set; }
    public decimal Amount { get; set; }
    public System.Guid Id { get; set; }
}
public partial struct Valid : IJobEntity
{
    public void Execute(ref SafeComponent component) { }
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
        Assert.Contains("CreateGeneratedTableAccess<", generated, StringComparison.Ordinal);
        Assert.Contains("UnsafeAccessorKind.StaticMethod", generated, StringComparison.Ordinal);
        Assert.Contains("SafeComponent>", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalRelationshipWriterIsGeneratedWithoutParallelSurface()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct ParentLike : IRelationshipSource { public int Value; }
public partial struct SerialTopology : IJobEntity
{
    public void Execute(ref ParentLike parent) { }
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
        Assert.DoesNotContain("ScheduleParallel", generated);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG108");
    }

    [Fact]
    public void SupportsBufferBorrowAndDirectSparseReference()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct Item : IBufferElement { public int Value; }
public struct Rare : ISparseComponent { public int Value; }
public partial struct Mixed : IJobEntity
{
    public void Execute(BufferView<Item> items, ref Rare rare) { }
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
        Assert.Contains("ReadBuffer", generated);
        Assert.Contains("ReadWriteSparse", generated);
    }

    [Fact]
    public void SupportsClosedGenericUnmanagedComponentJobs()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public partial struct GenericWrite<T> : IJobEntity
    where T : unmanaged, SomeEngine.ECS.IComponent
{
    public void Execute(ref T value) { }
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
        Assert.Contains("ScheduleParallel<T>", generated);
        Assert.Contains("where T : unmanaged", generated);
        Assert.Contains("GeneratedQueryAccess.Table<T>", generated);
        Assert.DoesNotContain("GeneratedQueryAccess.GeneratedTable<T>", generated);
    }

    [Fact]
    public void ClosedAliasFreeComponent_EmitsStaticAliasCertificateFactory()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
public struct Position : SomeEngine.ECS.IComponent { public int X; public int Y; }
public partial struct Integrate : IJobEntity
{
    public void Execute(ref Position value) { }
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
        Assert.True(
            generated.Contains("CreateGeneratedTableAccess<", StringComparison.Ordinal),
            generated);
        Assert.Contains("UnsafeAccessorKind.StaticMethod", generated, StringComparison.Ordinal);
        Assert.Contains("Position>", generated);
    }

    [Fact]
    public void SupportsParentAndEndpointReadWriteShapesAsSerialTopologyAccess()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
public readonly struct Scene : IHierarchyDomain { }
public struct Link : SomeEngine.ECS.IComponent { public int Value; }
public partial struct TopologyEdit : IJobEntity
{
    public void Execute(
        in Parent<Scene> parent,
        ref DirectedRelationEndpoints<Link> endpoints) { }
}
""");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG108");
        string generated = string.Join("\n", result.GeneratedTrees.Select(static tree => tree.GetText().ToString()));
        Assert.Contains("row.Read<", generated);
        Assert.Contains("row.ReadWrite<", generated);
        Assert.DoesNotContain("ScheduleParallel", generated);
    }

    [Fact]
    public void RejectsDuplicateEntityIdentityParameters()
    {
        GeneratorTestResult result = RunGenerator("""
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
public partial struct Invalid : IJobEntity
{
    public void Execute(Entity first, Entity second) { }
}
""");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "SECSSG102");
    }

    private static GeneratorTestResult RunGenerator(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "JobEntityGeneratorDiagnostics",
            [syntaxTree],
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver
            .Create(new JobEntityGenerator())
            .WithUpdatedParseOptions(parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
        GeneratorDriverRunResult result = driver.GetRunResult();
        return new GeneratorTestResult(
            result.Diagnostics.Concat(output.GetDiagnostics()).ToImmutableArray(),
            result.GeneratedTrees);
    }

    private static MetadataReference[] GetReferences()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .ToList();
        references.Add(MetadataReference.CreateFromFile(typeof(World).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(IJobEntity).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SomeEngine.Job.JobHandle).Assembly.Location));
        return references.ToArray();
    }

    private sealed record GeneratorTestResult(
        ImmutableArray<Diagnostic> Diagnostics,
        ImmutableArray<SyntaxTree> GeneratedTrees);
}
