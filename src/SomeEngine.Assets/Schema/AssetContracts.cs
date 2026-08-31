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
    public uint VertexStride { get; set; }
    public ulong PayloadKey { get; set; }
    public ulong PayloadLength { get; set; }
    [BinaryChunk(nameof(PayloadKey), nameof(PayloadLength))]
    [BinaryIgnore]
    public Memory<byte>? Payload { get; set; }
    public ulong BvhOffset { get; set; }
    public IList<MeshPayloadPageDigest>? PageDigests { get; set; }
    public ulong BvhLength { get; set; }
    public Memory<byte>? BvhSha256 { get; set; }
    public Vec3? QuantOrigin { get; set; }
    public float QuantStep { get; set; }
    public IList<MeshRegion>? Regions { get; set; }
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
    public uint VertexStride { get; set; }
    public Vec3? QuantOrigin { get; set; }
    public float QuantStep { get; set; }
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
    public IList<ShaderBytecode>? Variants { get; set; }
    public IList<ShaderEntryPointAttribute>? EntryPointAttributes { get; set; }
    public IList<BackendReflection>? Reflections { get; set; }
    public IList<ShaderEntryPointReflection>? EntryPointReflections { get; set; }
    public ShaderMetadata? Metadata { get; set; }
    public IList<ShaderEntryPointMetadata>? EntryPointMetadata { get; set; }
    public uint SchemaVersion { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderBytecode
{
    public ShaderBytecode()
    {
    }


    public string? Backend { get; set; }
    public ulong DataChunkKey { get; set; }
    public ulong DataDecodedLength { get; set; }
    [BinaryChunk(nameof(DataChunkKey), nameof(DataDecodedLength))]
    [BinaryIgnore]
    public Memory<byte>? Data { get; set; }
    public string? EntryPoint { get; set; }
    public ShaderStage Stage { get; set; }
    public string? ContentHash { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderEntryPointAttribute
{
    public ShaderEntryPointAttribute()
    {
    }


    public int VariantIndex { get; set; }
    public string? Name { get; set; }
    public IList<string>? Args { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ShaderEntryPointMetadata
{
    public ShaderEntryPointMetadata()
    {
    }


    public int VariantIndex { get; set; }
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
public partial class ShaderMaterialInstanceProperty
{
    public ShaderMaterialInstanceProperty()
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
    public IList<ShaderMaterialInstanceProperty>? MaterialInstanceProperties { get; set; }
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
    RasterDeformBinningReset = 6,
    RasterDeformBinningCount = 7,
    RasterDeformBinningReserve = 8,
    RasterDeformBinningScatter = 9,
    DeformCachePopulate = 10,
    SoftwareVisibilityRaster = 11,
    HardwareVisibilityRaster = 12,
    SoftwareDepthMerge = 13,
    HiZInitialize = 14,
    HiZReduce = 15,
    HiZReducePair = 16,
    MaterialBinningReset = 17,
    MaterialBinningCount = 18,
    MaterialBinningReserve = 19,
    MaterialBinningScatter = 20,
    MotionVectors = 21,
    VisibilityResolve = 22,
    TemporalResolve = 23,
    ToneMapAndPresent = 24,
}

public enum ClusterBoundsSupport : byte
{
    NotApplicable = 0,
    Finite = 1,
    Unbounded = 2,
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class ClusterShaderOperation
{
    public ClusterShaderOperation()
    {
    }


    public ClusterShaderOperationRole Role { get; set; }
    public ClusterBoundsSupport BoundsSupport { get; set; }
    public IList<ShaderRef>? Shaders { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial struct ParamValue : IBinaryContract<ParamValue>, IBinaryCustomViewContract<ParamValue>
{
    public enum ItemKind : byte
    {
        FloatVal = 1,
        IntVal = 2,
        BoolVal = 3,
        Vec2Val = 4,
        Vec3Val = 5,
        Vec4Val = 6,
        NONE = 0,
    }

    private readonly object? value;

    public ItemKind Kind => (ItemKind)Discriminator;
    public byte Discriminator { get; }

    public ParamValue(FloatVal value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Discriminator = 1;
        this.value = value;
    }

    public ParamValue(IntVal value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Discriminator = 2;
        this.value = value;
    }

    public ParamValue(BoolVal value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Discriminator = 3;
        this.value = value;
    }

    public ParamValue(Vec2Val value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Discriminator = 4;
        this.value = value;
    }

    public ParamValue(Vec3Val value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Discriminator = 5;
        this.value = value;
    }

    public ParamValue(Vec4Val value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Discriminator = 6;
        this.value = value;
    }

    public static implicit operator ParamValue(FloatVal value) => new(value);
    public static implicit operator ParamValue(IntVal value) => new(value);
    public static implicit operator ParamValue(BoolVal value) => new(value);
    public static implicit operator ParamValue(Vec2Val value) => new(value);
    public static implicit operator ParamValue(Vec3Val value) => new(value);
    public static implicit operator ParamValue(Vec4Val value) => new(value);

    public FloatVal FloatVal => Item1;
    public FloatVal Item1 => Discriminator == 1 ? (FloatVal)value! : throw WrongKind(ItemKind.FloatVal);
    public IntVal IntVal => Item2;
    public IntVal Item2 => Discriminator == 2 ? (IntVal)value! : throw WrongKind(ItemKind.IntVal);
    public BoolVal BoolVal => Item3;
    public BoolVal Item3 => Discriminator == 3 ? (BoolVal)value! : throw WrongKind(ItemKind.BoolVal);
    public Vec2Val Vec2Val => Item4;
    public Vec2Val Item4 => Discriminator == 4 ? (Vec2Val)value! : throw WrongKind(ItemKind.Vec2Val);
    public Vec3Val Vec3Val => Item5;
    public Vec3Val Item5 => Discriminator == 5 ? (Vec3Val)value! : throw WrongKind(ItemKind.Vec3Val);
    public Vec4Val Vec4Val => Item6;
    public Vec4Val Item6 => Discriminator == 6 ? (Vec4Val)value! : throw WrongKind(ItemKind.Vec4Val);

    public bool TryGet([NotNullWhen(true)] out FloatVal? value)
    {
        value = Discriminator == 1 ? (FloatVal)this.value! : null;
        return value is not null;
    }

    public bool TryGet([NotNullWhen(true)] out IntVal? value)
    {
        value = Discriminator == 2 ? (IntVal)this.value! : null;
        return value is not null;
    }

    public bool TryGet([NotNullWhen(true)] out BoolVal? value)
    {
        value = Discriminator == 3 ? (BoolVal)this.value! : null;
        return value is not null;
    }

    public bool TryGet([NotNullWhen(true)] out Vec2Val? value)
    {
        value = Discriminator == 4 ? (Vec2Val)this.value! : null;
        return value is not null;
    }

    public bool TryGet([NotNullWhen(true)] out Vec3Val? value)
    {
        value = Discriminator == 5 ? (Vec3Val)this.value! : null;
        return value is not null;
    }

    public bool TryGet([NotNullWhen(true)] out Vec4Val? value)
    {
        value = Discriminator == 6 ? (Vec4Val)this.value! : null;
        return value is not null;
    }

    public TReturn Match<TReturn>(
        Func<FloatVal, TReturn> caseFloatVal,
        Func<IntVal, TReturn> caseIntVal,
        Func<BoolVal, TReturn> caseBoolVal,
        Func<Vec2Val, TReturn> caseVec2Val,
        Func<Vec3Val, TReturn> caseVec3Val,
        Func<Vec4Val, TReturn> caseVec4Val)
        => Discriminator switch
        {
            1 => caseFloatVal((FloatVal)value!),
            2 => caseIntVal((IntVal)value!),
            3 => caseBoolVal((BoolVal)value!),
            4 => caseVec2Val((Vec2Val)value!),
            5 => caseVec3Val((Vec3Val)value!),
            6 => caseVec4Val((Vec4Val)value!),
            _ => throw InvalidDiscriminator(Discriminator),
        };

    public static Guid TypeId => BinaryTypeId.FromLogicalName("SomeEngine.Assets.Schema.ParamValue");

    public static ulong SchemaFingerprint => BinaryFieldKey.FromName(
        "SomeEngine.Assets.Schema.ParamValue|1:Float32|2:Int32|3:Boolean|4:Float32x2|5:Float32x3|6:Float32x4");

    public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
    public static uint SchemaEpoch => 1;

    public static void Write(ref BinaryDataWriter writer, ParamValue value)
    {
        if (value.Discriminator > 6)
            throw InvalidDiscriminator(value.Discriminator);

        writer.WriteByte(value.Discriminator);
        switch (value.Discriminator)
        {
            case 0:
                return;
            case 1:
                writer.WriteSingle(value.Item1.V);
                return;
            case 2:
                writer.WriteInt32(value.Item2.V);
                return;
            case 3:
                writer.WriteBoolean(value.Item3.V);
                return;
            case 4:
                writer.WriteSingle(value.Item4.X);
                writer.WriteSingle(value.Item4.Y);
                return;
            case 5:
                writer.WriteSingle(value.Item5.X);
                writer.WriteSingle(value.Item5.Y);
                writer.WriteSingle(value.Item5.Z);
                return;
            case 6:
                writer.WriteSingle(value.Item6.X);
                writer.WriteSingle(value.Item6.Y);
                writer.WriteSingle(value.Item6.Z);
                writer.WriteSingle(value.Item6.W);
                return;
            default:
                throw InvalidDiscriminator(value.Discriminator);
        }
    }

    public static ParamValue Read(ref BinaryDataReader reader)
    {
        byte discriminator = reader.ReadByte();
        return discriminator switch
        {
            0 => default,
            1 => new ParamValue(new FloatVal { V = reader.ReadSingle() }),
            2 => new ParamValue(new IntVal { V = reader.ReadInt32() }),
            3 => new ParamValue(new BoolVal { V = reader.ReadBoolean() }),
            4 => new ParamValue(new Vec2Val { X = reader.ReadSingle(), Y = reader.ReadSingle() }),
            5 => new ParamValue(new Vec3Val
            {
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Z = reader.ReadSingle(),
            }),
            6 => new ParamValue(new Vec4Val
            {
                X = reader.ReadSingle(),
                Y = reader.ReadSingle(),
                Z = reader.ReadSingle(),
                W = reader.ReadSingle(),
            }),
            _ => throw InvalidDiscriminator(discriminator),
        };
    }

    public static void ValidateView(ref BinaryViewReader reader)
    {
        byte discriminator = reader.ReadByte();
        switch (discriminator)
        {
            case 0:
                return;
            case 1:
                _ = reader.ReadSingle();
                return;
            case 2:
                _ = reader.ReadInt32();
                return;
            case 3:
                _ = reader.ReadBoolean();
                return;
            case 4:
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                return;
            case 5:
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                return;
            case 6:
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                return;
            default:
                throw InvalidDiscriminator(discriminator);
        }
    }

    private InvalidOperationException WrongKind(ItemKind requested)
        => new($"ParamValue contains {Kind}, not {requested}.");

    private static InvalidDataException InvalidDiscriminator(byte discriminator)
        => new($"Invalid ParamValue discriminator {discriminator}.");
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class BoolVal
{
    public BoolVal()
    {
    }


    public bool V { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ComponentEntry
{
    public ComponentEntry()
    {
    }


    public string? TypeName { get; set; }
    public string? Json { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class FloatVal
{
    public FloatVal()
    {
    }


    public float V { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class IntVal
{
    public IntVal()
    {
    }


    public int V { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class Material
{
    public Material()
    {
    }

    public string? AssetGuid { get; set; }
    public string? Name { get; set; }
    public IList<PassEntry>? Passes { get; set; }
    public IList<TextureBinding>? Textures { get; set; }
    public IList<ScalarParam>? Scalars { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class PassEntry
{
    public PassEntry()
    {
    }


    public ShaderRef? Shader { get; set; }
    public IList<TagEntry>? Tags { get; set; }
    public IList<ComponentEntry>? Components { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ScalarParam
{
    public ScalarParam()
    {
    }


    public string? Name { get; set; }
    public ParamValue? Value { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class TagEntry
{
    public TagEntry()
    {
    }


    public string? Name { get; set; }
    public int Value { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class TextureBinding
{
    public TextureBinding()
    {
    }


    public string? Name { get; set; }
    public string? TextureGuid { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class Vec2Val
{
    public Vec2Val()
    {
    }


    public float X { get; set; }
    public float Y { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class Vec3Val
{
    public Vec3Val()
    {
    }


    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class Vec4Val
{
    public Vec4Val()
    {
    }


    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public sealed partial class MaterialInstance
{
    public MaterialInstance()
    {
    }

    public string? AssetGuid { get; set; }
    public string? ParentGuid { get; set; }
    public IList<ParamOverride>? Overrides { get; set; }
    public IList<ScalarOverride>? ScalarOverrides { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ParamOverride
{
    public ParamOverride()
    {
    }


    public string? Name { get; set; }
    public string? TextureGuid { get; set; }
}

[BinaryContract(BinaryCompatibility.ExactSchema)]
public partial class ScalarOverride
{
    public ScalarOverride()
    {
    }


    public string? Name { get; set; }
    public ParamValue? Value { get; set; }
}

#endif
