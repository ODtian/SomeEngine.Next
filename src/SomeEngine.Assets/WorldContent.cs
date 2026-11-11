using SomeEngine.Serialization;

namespace SomeEngine.Assets;

[BinarySerializable]
[BinaryFields(nameof(MinX), nameof(MinY), nameof(MinZ), nameof(MaxX), nameof(MaxY), nameof(MaxZ))]
public readonly partial record struct WorldBounds(double MinX, double MinY, double MinZ,
    double MaxX, double MaxY, double MaxZ);

[BinarySerializable]
[BinaryFields(nameof(Asset), nameof(Bounds))]
public readonly partial record struct WorldContent(AssetId<Asset> Asset, WorldBounds Bounds);
