using SomeEngine.Assets.Pipeline;

namespace SomeEngine.Assets.Schema;

/// <summary>
/// Immutable mesh content selected at an extraction boundary. The asset keeps this version's
/// streamed source available while the data is referenced; disposing the asset ends that access.
/// Streamers retain their own source reference for each registration and in-progress read.
/// </summary>
public sealed class MeshData
{
    private readonly Mesh _asset;
    internal MeshData(Mesh asset, MeshPayloadSource? source)
    {
        _asset = asset;
        Source = source;
        Revision = asset.Revision;
        AssetGuid = asset.AssetGuid;
        Name = asset.Name;
        VertexEncoding = asset.VertexEncoding;
    }

    internal MeshPayloadSource? Source { get; }
    public ulong Revision { get; }
    public string? AssetGuid { get; }
    public string? Name { get; }
    public string? VertexEncoding { get; }
    public bool IsStreamed => Source is not null;

    internal bool TryRetainPayloadSource(out MeshPayloadSource? source) =>
        _asset.TryRetainPayloadSource(this, out source);
}
