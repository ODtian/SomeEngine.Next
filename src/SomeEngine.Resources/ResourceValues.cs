
#nullable enable

using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using SomeEngine.Serialization;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;



/// <summary>
/// Describes the coordinates and storage layout of one texture tile.
/// </summary>
[BinarySerializable(BinaryCompatibility.ExactSchema, LogicalName = "SomeEngine.Assets.Schema.TextureMipTile"), TextSerializable]
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

}

[BinaryName("SomeEngine.Assets.Schema.ValueType")]
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

[BinarySerializable(BinaryCompatibility.ExactSchema, LogicalName = "SomeEngine.Assets.Schema.Bounds")]
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
[BinarySerializable(BinaryCompatibility.ExactSchema, LogicalName = "SomeEngine.Assets.Schema.MeshPayloadPageDigest")]
public sealed partial record MeshPayloadPageDigest
{
    public MeshPayloadPageDigest()
    {
    }


    public ulong Offset { get; init; }
    public uint Length { get; init; }
    public uint ClusterCount { get; init; }
    public ulong VertexEncodingHash { get; init; }
    public ImmutableArray<byte> Sha256 { get; init; } = [];
}

[BinarySerializable(BinaryCompatibility.ExactSchema, LogicalName = "SomeEngine.Assets.Schema.MeshRegion")]
public sealed partial record MeshRegion
{
    public MeshRegion()
    {
    }


    public string? Name { get; init; }
}

[BinarySerializable(BinaryCompatibility.ExactSchema, LogicalName = "SomeEngine.Assets.Schema.Vec3")]
public sealed partial record Vec3
{
    public Vec3()
    {
    }


    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}

[BinaryName("SomeEngine.Assets.Schema.ClusterShaderOperationRole")]
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
    PageRequests = 36,
    LeafFlagFixups = 37,
}

