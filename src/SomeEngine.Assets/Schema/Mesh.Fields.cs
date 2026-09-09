using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

public sealed partial class Mesh
{
    private MeshData _content = new();

    public string? Name { get => _content.Name; set => _content.Name = value; }
    public Bounds? Bounds { get => _content.Bounds; set => _content.Bounds = value; }
    public string? VertexEncoding { get => _content.VertexEncoding; set => _content.VertexEncoding = value; }
    public ulong PayloadKey { get => _content.PayloadKey; set => _content.PayloadKey = value; }
    public ulong PayloadLength { get => _content.PayloadLength; set => _content.PayloadLength = value; }
    public Memory<byte>? Payload { get => _content.Payload; set => _content.Payload = value; }
    public ulong BvhOffset { get => _content.BvhOffset; set => _content.BvhOffset = value; }
    public IList<MeshPayloadPageDigest>? PageDigests { get => _content.PageDigests; set => _content.PageDigests = value; }
    public ulong BvhLength { get => _content.BvhLength; set => _content.BvhLength = value; }
    public Memory<byte>? BvhSha256 { get => _content.BvhSha256; set => _content.BvhSha256 = value; }
    public IList<MeshRegion>? Regions { get => _content.Regions; set => _content.Regions = value; }
    public IList<string>? MaterialGuids { get => _content.MaterialGuids; set => _content.MaterialGuids = value; }
}
