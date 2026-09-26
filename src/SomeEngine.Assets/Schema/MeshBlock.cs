using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Geometry bounds and source region correspondence shared by mesh representations.</summary>
[AssetBlock("SomeEngine.Assets.Schema.MeshBlock"), BinarySerializable]
[BinaryFields(nameof(Bounds), nameof(Regions), nameof(RegionSources))]
public sealed partial class MeshBlock : AssetPageBlock
{
    public partial Bounds? Bounds { get; set; }
    public partial IList<MeshRegion> Regions { get; set; }
    public partial IList<MeshRegionSource> RegionSources { get; set; }
}
