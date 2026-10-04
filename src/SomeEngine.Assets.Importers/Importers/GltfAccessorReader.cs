using System.Text.Json.Nodes;
using SharpGLTF.Schema2;

namespace SomeEngine.Assets.Importers;

/// <summary>Reads shared decoded source components, including meshopt and sparse accessor data.</summary>
public sealed class GltfAccessorReader
{
    private readonly GltfAccessorStreams _streams;
    private readonly CancellationToken _cancellationToken;
    public GltfAccessorReader(ModelRoot source) : this(source, JsonNode.Parse(source.GetJsonPreview())!, default) { }
    internal GltfAccessorReader(ModelRoot source, JsonNode document, CancellationToken cancellationToken)
    {
        _streams = new(source, document);
        _cancellationToken = cancellationToken;
    }
    public VertexStream ReadComponents(Accessor accessor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VertexStream values = Read("", accessor);
        cancellationToken.ThrowIfCancellationRequested();
        return values;
    }
    internal VertexStream Read(string name, Accessor accessor, bool morph = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        VertexStream values = _streams.Read(name, accessor, morph);
        _cancellationToken.ThrowIfCancellationRequested();
        return values;
    }
}
