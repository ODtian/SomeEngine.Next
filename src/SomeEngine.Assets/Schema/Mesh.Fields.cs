using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

[Asset(".mesh.asset")]
public sealed partial class Mesh
{
    public partial string? Name { get; set; }
    public partial Bounds? Bounds { get; set; }
    public partial string? VertexEncoding { get; set; }
    public partial ulong PayloadKey { get; set; }
    public partial ulong PayloadLength { get; set; }
    [BinaryChunk(nameof(PayloadKey), nameof(PayloadLength))]
    [BinaryIgnore]
    public partial ReadOnlyMemory<byte>? Payload { get; set; }
    public partial ulong BvhOffset { get; set; }
    public partial ImmutableList<MeshPayloadPageDigest>? PageDigests { get; set; }
    public partial ulong BvhLength { get; set; }
    public partial ReadOnlyMemory<byte>? BvhSha256 { get; set; }
    public partial ImmutableList<MeshRegion>? Regions { get; set; }
    [AssetReference(typeof(Material))]
    public partial ImmutableList<string>? MaterialGuids { get; set; }
}
