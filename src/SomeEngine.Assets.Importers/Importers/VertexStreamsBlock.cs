using System.Collections.ObjectModel;

namespace SomeEngine.Assets.Importers;

/// <summary>One expanded accessor. Values follow the source component order; integers retain their full precision.</summary>
public sealed class VertexStream
{
    public int ComponentCount { get; }
    public ReadOnlyMemory<double> Values { get; }
    public int Count => Values.Length / ComponentCount;

    public VertexStream(int componentCount, ReadOnlySpan<double> values)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(componentCount);
        if (values.Length % componentCount != 0)
            throw new ArgumentException("Vertex values must contain complete component groups.", nameof(values));
        ComponentCount = componentCount;
        Values = values.ToArray();
    }

    internal VertexStream(int componentCount, double[] values)
    {
        ComponentCount = componentCount;
        Values = values;
    }
}

/// <summary>Triangle data shared by mesh importers before any pipeline chooses its vertex identity or encoding.</summary>
public sealed class VertexPrimitive
{
    public int SourcePrimitive { get; }
    public int Material { get; }
    public ReadOnlyMemory<uint> Indices { get; }
    public ReadOnlyMemory<uint> SourceVertices { get; }
    public IReadOnlyDictionary<string, VertexStream> Attributes { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, VertexStream>> MorphTargets { get; }
    /// <summary>Source tangents retained when flat normals require replacing the tangent basis.</summary>
    public VertexStream? SourceTangents { get; }
    /// <summary>Source tangent deltas retained when replacing a morph target's tangent basis.</summary>
    public IReadOnlyList<VertexStream?> SourceMorphTangents { get; }

    public VertexPrimitive(int sourcePrimitive, int material, ReadOnlySpan<uint> indices, ReadOnlySpan<uint> sourceVertices,
        IReadOnlyDictionary<string, VertexStream> attributes, IReadOnlyList<IReadOnlyDictionary<string, VertexStream>> morphTargets,
        VertexStream? sourceTangents = null, IReadOnlyList<VertexStream?>? sourceMorphTangents = null)
        : this(sourcePrimitive, material, indices.ToArray(), sourceVertices.ToArray(),
            attributes.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            morphTargets.Select(target => (IReadOnlyDictionary<string, VertexStream>)new ReadOnlyDictionary<string, VertexStream>(
                target.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal))).ToArray(),
            sourceTangents, sourceMorphTangents?.ToArray() ?? [])
    {
    }

    internal VertexPrimitive(int sourcePrimitive, int material, uint[] indices, uint[] sourceVertices,
        Dictionary<string, VertexStream> attributes, IReadOnlyDictionary<string, VertexStream>[] morphTargets,
        VertexStream? sourceTangents, VertexStream?[] sourceMorphTangents)
    {
        SourcePrimitive = sourcePrimitive;
        Material = material;
        Indices = indices;
        SourceVertices = sourceVertices;
        Attributes = new ReadOnlyDictionary<string, VertexStream>(attributes);
        MorphTargets = Array.AsReadOnly(morphTargets);
        SourceTangents = sourceTangents;
        SourceMorphTangents = Array.AsReadOnly(sourceMorphTangents);
    }
}

public enum VertexImportDiagnosticKind
{
    UnsupportedTopology,
    UnsupportedCompression,
    InvalidPrimitive,
    MissingTextureCoordinates,
    TangentGenerationFailed,
}

public sealed record VertexImportDiagnostic(int Primitive, VertexImportDiagnosticKind Kind, string Message);

/// <summary>Expanded source vertex streams and diagnostics, held only for the duration of asset import.</summary>
[AssetBlock("SomeEngine.Assets.Importers.VertexStreamsBlock")]
public sealed partial class VertexStreamsBlock : AssetBlock
{
    public static bool Persistent => false;
    public IReadOnlyList<VertexPrimitive> Primitives { get; }
    public IReadOnlyList<VertexImportDiagnostic> Diagnostics { get; }

    public VertexStreamsBlock(IReadOnlyList<VertexPrimitive> primitives, IReadOnlyList<VertexImportDiagnostic> diagnostics)
        : this(primitives.ToArray(), diagnostics.ToArray())
    {
    }

    internal VertexStreamsBlock(VertexPrimitive[] primitives, VertexImportDiagnostic[] diagnostics)
    {
        Primitives = Array.AsReadOnly(primitives);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    private VertexStreamsBlock(VertexStreamsBlock source, object? syncRoot = null) : base(source, syncRoot)
    {
        Primitives = source.Primitives;
        Diagnostics = source.Diagnostics;
    }

    public override VertexStreamsBlock Duplicate() => new(this);
    public override VertexStreamsBlock Duplicate(object syncRoot) => new(this, syncRoot);
}
