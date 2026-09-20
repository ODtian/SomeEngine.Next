#if SOMEENGINE_NATIVE_ASSET_CONTRACTS
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using SomeEngine.Serialization;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Schema;



/// <summary>
/// Describes one independently streamable texture tile. Payload is populated only by the
/// eager data reader; binary documents keep it null and use ChunkKey for on-demand reads.
/// </summary>
[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record TextureMipTile
{
    public TextureMipTile()
    {
    }


    public uint MipLevel { get; init; }
    public uint TileX { get; init; }
    public uint TileY { get; init; }
    public uint ArrayLayer { get; init; }
    public uint Face { get; init; }
    public uint DepthSlice { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public ulong RowPitch { get; init; }
    public ulong SlicePitch { get; init; }
    public ulong ChunkKey { get; init; }
    public ulong DecodedLength { get; init; }
    [BinaryChunk(nameof(ChunkKey), nameof(DecodedLength))]
    [BinaryIgnore]
    public ReadOnlyMemory<byte>? Payload { get; init; }
    internal TextureMipTile CopyFields() => this;

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
public sealed partial record Bounds
{
    public Bounds()
    {
    }


    public Vec3 Center { get; init; } = new();
    public float Radius { get; init; }
}



/// <summary>
/// Root-authenticated layout and content identity for one independently streamed mesh page.
/// The cooker derives every value from the payload; consumers must not accept caller-authored
/// descriptors that disagree with the page header or digest.
/// </summary>
[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record MeshPayloadPageDigest
{
    public MeshPayloadPageDigest()
    {
    }


    public ulong Offset { get; init; }
    public uint Length { get; init; }
    public uint ClusterCount { get; init; }
    public ulong VertexEncodingHash { get; init; }
    public ReadOnlyMemory<byte>? Sha256 { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record MeshRegion
{
    public MeshRegion()
    {
    }


    public string? Name { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record Vec3
{
    public Vec3()
    {
    }


    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
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
public sealed partial record BackendReflection
{
    public BackendReflection()
    {
    }


    public string? Backend { get; init; }
    public ShaderReflectionData? Reflection { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record DependencyEntry
{
    public DependencyEntry()
    {
    }


    public string? Path { get; init; }
    public string? ContentHash { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ImportTrace
{
    public ImportTrace()
    {
    }


    public string? SourceGuid { get; init; }
    public string? SourcePath { get; init; }
    public string? SubAssetKey { get; init; }
    public string? ContentFingerprint { get; init; }
    public ImmutableList<DependencyEntry>? Dependencies { get; init; }
    public uint ImporterVersion { get; init; }
}



[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record CookedShaderProgram
{
    public CookedShaderProgram()
    {
    }


    public string? Backend { get; init; }
    public ulong DataChunkKey { get; init; }
    public ulong DataDecodedLength { get; init; }
    [BinaryChunk(nameof(DataChunkKey), nameof(DataDecodedLength))]
    [BinaryIgnore]
    public ReadOnlyMemory<byte>? Data { get; init; }
    public string? ContentHash { get; init; }

    [BinaryIgnore]
    public SlangShaderSharp.ShaderProgram? Program { get; internal init; }
    internal CookedShaderProgram CopyFields() => this;

}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderEntryPointAttribute
{
    public ShaderEntryPointAttribute()
    {
    }


    public int ProgramIndex { get; init; }
    public int EntryPointIndex { get; init; }
    public string? Name { get; init; }
    public ImmutableList<string>? Args { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderEntryPointMetadata
{
    public ShaderEntryPointMetadata()
    {
    }


    public int ProgramIndex { get; init; }
    public int EntryPointIndex { get; init; }
    public ReadOnlyMemory<byte>? VertexLayoutDescriptor { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderEntryPointReflection
{
    public ShaderEntryPointReflection()
    {
    }


    public string? Backend { get; init; }
    public string? EntryPoint { get; init; }
    public ShaderStage Stage { get; init; }
    public ShaderReflectionData? Reflection { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderMaterialBinding
{
    public ShaderMaterialBinding()
    {
    }


    public string? Name { get; init; }
    public byte ResourceType { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderMaterialScalarField
{
    public ShaderMaterialScalarField()
    {
    }


    public string? Name { get; init; }
    public uint Offset { get; init; }
    public uint Size { get; init; }
    public uint RowCount { get; init; }
    public uint ColumnCount { get; init; }
    public byte ScalarType { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderMaterialScalarLayout
{
    public ShaderMaterialScalarLayout()
    {
    }


    public string? Name { get; init; }
    public uint Size { get; init; }
    public ImmutableList<ShaderMaterialScalarField>? Fields { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderInstanceProperty
{
    public ShaderInstanceProperty()
    {
    }


    public string? CanonicalId { get; init; }
    public string? MaterialScalarLayoutName { get; init; }
    public string? MaterialScalarName { get; init; }
    public string? Accessor { get; init; }
    public uint Size { get; init; }
    public uint Alignment { get; init; }
    public uint RowCount { get; init; }
    public uint ColumnCount { get; init; }
    public byte ScalarType { get; init; }
    public ReadOnlyMemory<byte>? DefaultValue { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderMetadata
{
    public ShaderMetadata()
    {
    }


    public ImmutableList<string>? Tags { get; init; }
    public ImmutableList<ShaderMaterialBinding>? MaterialBindings { get; init; }
    public ImmutableList<ShaderMaterialScalarLayout>? MaterialScalarLayouts { get; init; }
    public ImmutableList<ShaderInstanceProperty>? InstanceProperties { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderReflectionData
{
    public ShaderReflectionData()
    {
    }


    public ImmutableList<ShaderResourceReflection>? Resources { get; init; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial record ShaderResourceReflection
{
    public ShaderResourceReflection()
    {
    }


    public string? Name { get; init; }
    public uint Stages { get; init; }
    public uint Binding { get; init; }
    public uint Space { get; init; }
    public DescriptorType Kind { get; init; }
    public uint DescriptorCount { get; init; } = 1;
    public AccessEffect Effect { get; init; }
    public ShaderQualifiers Qualifiers { get; init; }
    public TextureViewDimension? TextureDimension { get; init; }
    public TextureSampleType? TextureSampleType { get; init; }
    public StorageFormat? StorageFormat { get; init; }
    public uint SlangResourceShape { get; init; }
    public uint SlangScalarType { get; init; }
    public uint SlangImageFormat { get; init; }
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
public sealed partial record ClusterShaderOperation
{
    public ClusterShaderOperation()
    {
    }


    public ClusterShaderOperationRole Role { get; init; }
    public ImmutableList<ShaderRef>? Shaders { get; init; }
}

#endif
