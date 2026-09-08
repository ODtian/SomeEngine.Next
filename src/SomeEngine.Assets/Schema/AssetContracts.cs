#if SOMEENGINE_NATIVE_ASSET_CONTRACTS
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using SomeEngine.Serialization;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Schema;

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class Texture
{
    public Texture()
    {
    }

    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public TextureDimension Dimension { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Depth { get; set; }
    public uint MipLevelCount { get; set; }
    public uint ArrayLayerCount { get; set; }
    public Format Format { get; set; }
    public Format SampledFormat { get; set; }
    public SomeEngine.Graphics.TextureViewDimension SampledDimension { get; set; }
    public IList<TextureMipTile>? MipTiles { get; set; }
}

/// <summary>
/// Describes one independently streamable texture tile. Payload is populated only by the
/// eager data reader; binary documents keep it null and use ChunkKey for on-demand reads.
/// </summary>
[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class TextureMipTile
{
    public TextureMipTile()
    {
    }


    public uint MipLevel { get; set; }
    public uint TileX { get; set; }
    public uint TileY { get; set; }
    public uint ArrayLayer { get; set; }
    public uint Face { get; set; }
    public uint DepthSlice { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public ulong RowPitch { get; set; }
    public ulong SlicePitch { get; set; }
    public ulong ChunkKey { get; set; }
    public ulong DecodedLength { get; set; }
    [BinaryChunk(nameof(ChunkKey), nameof(DecodedLength))]
    [BinaryIgnore]
    public Memory<byte>? Payload { get; set; }
}

public enum ValueType : byte
{
    Undefined = 0,
    Int8 = 1,
    Int16 = 2,
    Int32 = 3,
    UInt8 = 4,
    UInt16 = 5,
    UInt32 = 6,
    Float16 = 7,
    Float32 = 8,
    Float64 = 9,
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class Bounds
{
    public Bounds()
    {
    }


    public Vec3 Center { get; set; } = new();
    public float Radius { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class Mesh
{
    public Mesh()
    {
    }

    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public Bounds? Bounds { get; set; }
    public string? VertexEncoding { get; set; }
    public ulong PayloadKey { get; set; }
    public ulong PayloadLength { get; set; }
    [BinaryChunk(nameof(PayloadKey), nameof(PayloadLength))]
    [BinaryIgnore]
    public Memory<byte>? Payload { get; set; }
    public ulong BvhOffset { get; set; }
    public IList<MeshPayloadPageDigest>? PageDigests { get; set; }
    public ulong BvhLength { get; set; }
    public Memory<byte>? BvhSha256 { get; set; }
    public IList<MeshRegion>? Regions { get; set; }
    public IList<string>? MaterialGuids { get; set; }
}

/// <summary>
/// Root-authenticated layout and content identity for one independently streamed mesh page.
/// The cooker derives every value from the payload; consumers must not accept caller-authored
/// descriptors that disagree with the page header or digest.
/// </summary>
[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class MeshPayloadPageDigest
{
    public MeshPayloadPageDigest()
    {
    }


    public ulong Offset { get; set; }
    public uint Length { get; set; }
    public uint ClusterCount { get; set; }
    public ulong VertexEncodingHash { get; set; }
    public Memory<byte>? Sha256 { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class MeshRegion
{
    public MeshRegion()
    {
    }


    public string? Name { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class Vec3
{
    public Vec3()
    {
    }


    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public enum DescriptorType : byte
{
    None = 0,
    ConstantBuffer = 1,
    SampledTexture = 2,
    StorageTexture = 3,
    ReadOnlyBuffer = 4,
    StorageBuffer = 5,
    Sampler = 6,
    AccelerationStructure = 7,
}

public enum AccessEffect : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = 3,
}

[Flags]
public enum ShaderQualifiers : uint
{
    None = 0,
    Atomic = 1,
    Append = 2,
    Consume = 4,
    RasterOrdered = 8,
    Feedback = 16,
}

public enum ShaderStage : byte
{
    Vertex = 0,
    Hull = 1,
    Domain = 2,
    Geometry = 3,
    Pixel = 4,
    Compute = 5,
    Amplification = 6,
    Mesh = 7,
    RayGen = 8,
    RayMiss = 9,
    RayClosestHit = 10,
    RayAnyHit = 11,
    RayIntersection = 12,
    Callable = 13,
    Node = 14,
}

public enum StorageFormat : ushort
{
    R8UNorm = 1,
    R8G8UNorm = 2,
    R8G8B8A8UNorm = 3,
    R8G8B8A8UNormSrgb = 4,
    B8G8R8A8UNorm = 5,
    R16UInt = 6,
    R16Float = 7,
    R16G16Float = 8,
    R16G16B16A16Float = 9,
    R32UInt = 10,
    R32Float = 11,
    R32G32Float = 12,
    R32G32B32Float = 13,
    R32G32B32A32Float = 14,
    D24UNormS8UInt = 15,
    D32Float = 16,
}

public enum TextureViewDimension : byte
{
    Texture1D,
    Texture1DArray,
    Texture2D,
    Texture2DArray,
    Texture2DMS,
    Texture2DMSArray,
    Cube,
    CubeArray,
    Texture3D,
}

public enum TextureSampleType : byte
{
    Float = 1,
    UInt = 2,
    SInt = 3,
    Depth = 4,
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class BackendReflection
{
    public BackendReflection()
    {
    }


    public string? Backend { get; set; }
    public ShaderReflectionData? Reflection { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class DependencyEntry
{
    public DependencyEntry()
    {
    }


    public string? Path { get; set; }
    public string? ContentHash { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ImportTrace
{
    public ImportTrace()
    {
    }


    public string? SourceGuid { get; set; }
    public string? SourcePath { get; set; }
    public string? SubAssetKey { get; set; }
    public string? ContentFingerprint { get; set; }
    public IList<DependencyEntry>? Dependencies { get; set; }
    public uint ImporterVersion { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class Shader
{
    public Shader()
    {
    }

    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public ImportTrace? ImportTrace { get; set; }
    public IList<CookedShaderProgram>? Programs { get; set; }
    public IList<ShaderEntryPointAttribute>? EntryPointAttributes { get; set; }
    public IList<BackendReflection>? Reflections { get; set; }
    public IList<ShaderEntryPointReflection>? EntryPointReflections { get; set; }
    public ShaderMetadata? Metadata { get; set; }
    public IList<ShaderEntryPointMetadata>? EntryPointMetadata { get; set; }
    public uint SchemaVersion { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class CookedShaderProgram
{
    public CookedShaderProgram()
    {
    }


    public string? Backend { get; set; }
    public ulong DataChunkKey { get; set; }
    public ulong DataDecodedLength { get; set; }
    [BinaryChunk(nameof(DataChunkKey), nameof(DataDecodedLength))]
    [BinaryIgnore]
    public Memory<byte>? Data { get; set; }
    public string? ContentHash { get; set; }

    [BinaryIgnore]
    public SlangShaderSharp.ShaderProgram? Program { get; internal set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderEntryPointAttribute
{
    public ShaderEntryPointAttribute()
    {
    }


    public int ProgramIndex { get; set; }
    public int EntryPointIndex { get; set; }
    public string? Name { get; set; }
    public IList<string>? Args { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderEntryPointMetadata
{
    public ShaderEntryPointMetadata()
    {
    }


    public int ProgramIndex { get; set; }
    public int EntryPointIndex { get; set; }
    public Memory<byte>? VertexLayoutDescriptor { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderEntryPointReflection
{
    public ShaderEntryPointReflection()
    {
    }


    public string? Backend { get; set; }
    public string? EntryPoint { get; set; }
    public ShaderStage Stage { get; set; }
    public ShaderReflectionData? Reflection { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderMaterialBinding
{
    public ShaderMaterialBinding()
    {
    }


    public string? Name { get; set; }
    public byte ResourceType { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderMaterialScalarField
{
    public ShaderMaterialScalarField()
    {
    }


    public string? Name { get; set; }
    public uint Offset { get; set; }
    public uint Size { get; set; }
    public uint RowCount { get; set; }
    public uint ColumnCount { get; set; }
    public byte ScalarType { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderMaterialScalarLayout
{
    public ShaderMaterialScalarLayout()
    {
    }


    public string? Name { get; set; }
    public uint Size { get; set; }
    public IList<ShaderMaterialScalarField>? Fields { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderInstanceProperty
{
    public ShaderInstanceProperty()
    {
    }


    public string? CanonicalId { get; set; }
    public string? MaterialScalarLayoutName { get; set; }
    public string? MaterialScalarName { get; set; }
    public string? Accessor { get; set; }
    public uint Size { get; set; }
    public uint Alignment { get; set; }
    public uint RowCount { get; set; }
    public uint ColumnCount { get; set; }
    public byte ScalarType { get; set; }
    public Memory<byte>? DefaultValue { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderMetadata
{
    public ShaderMetadata()
    {
    }


    public IList<string>? Tags { get; set; }
    public IList<ShaderMaterialBinding>? MaterialBindings { get; set; }
    public IList<ShaderMaterialScalarLayout>? MaterialScalarLayouts { get; set; }
    public IList<ShaderInstanceProperty>? InstanceProperties { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderReflectionData
{
    public ShaderReflectionData()
    {
    }


    public IList<ShaderResourceReflection>? Resources { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderResourceReflection
{
    public ShaderResourceReflection()
    {
    }


    public string? Name { get; set; }
    public uint Stages { get; set; }
    public uint Binding { get; set; }
    public uint Space { get; set; }
    public DescriptorType Kind { get; set; }
    public uint DescriptorCount { get; set; } = 1;
    public AccessEffect Effect { get; set; }
    public ShaderQualifiers Qualifiers { get; set; }
    public TextureViewDimension? TextureDimension { get; set; }
    public TextureSampleType? TextureSampleType { get; set; }
    public StorageFormat? StorageFormat { get; set; }
    public uint SlangResourceShape { get; set; }
    public uint SlangScalarType { get; set; }
    public uint SlangImageFormat { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class ClusterShaders
{
    public ClusterShaders()
    {
    }

    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public IList<ClusterShaderOperation>? Operations { get; set; }

}

public enum ClusterShaderOperationRole : byte
{
    None = 0,
    BvhTraversal = 1,
    CullReset = 2,
    CullPhaseOne = 3,
    CullPhaseTwo = 5,
    RasterBinningReset = 6,
    RasterBinningCount = 7,
    RasterBinningReserve = 8,
    RasterBinningScatter = 9,
    SoftwareVisibilityRaster = 10,
    HardwareVisibilityRaster = 11,
    VisibilityClearImage = 31,
    VisibilityClearBuffer = 32,
    VisibilityResolveImage = 33,
    VisibilityResolveBuffer = 34,
    HiZInitialize = 13,
    HiZReduce = 14,
    HiZReducePair = 15,
    MaterialBinningReset = 16,
    MaterialBinningCount = 17,
    MaterialBinningReserve = 18,
    MaterialBinningScatter = 19,
    VisibilityResolve = 20,
    TessellationBinningReset = 24,
    TessellationBinningCount = 25,
    TessellationBinningReserve = 26,
    TessellationBinningScatter = 27,
    BvhTraversalInitialize = 28,
    BvhDeferredResume = 30,
    SceneBounds = 35,
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class ClusterShaderOperation
{
    public ClusterShaderOperation()
    {
    }


    public ClusterShaderOperationRole Role { get; set; }
    public IList<ShaderRef>? Shaders { get; set; }
}

#endif
