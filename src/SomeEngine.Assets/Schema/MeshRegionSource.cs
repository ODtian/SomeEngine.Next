using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>Source correspondence for one imported region. Indices identify this import, not durable source identities.</summary>
[BinarySerializable, BinaryValue]
[BinaryFields(nameof(MeshIndex), nameof(PrimitiveIndex), nameof(MaterialIndex), nameof(Name))]
public readonly partial record struct MeshRegionSource(int MeshIndex, int PrimitiveIndex, int MaterialIndex, string Name);
