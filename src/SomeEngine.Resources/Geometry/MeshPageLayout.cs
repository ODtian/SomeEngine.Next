using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Geometry;

/// <summary>
/// Page header (48 bytes). Cluster owns only topology and opaque vertex-blob locations.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MeshPageHeader
{
    public uint ClusterCount;        //  0
    public uint TotalVertexCount;    //  4
    public uint TotalTriangleCount;  //  8
    public uint VertexBlobBytes;     // 12
    public uint ClustersOffset;      // 16
    public uint VertexBlobsOffset;   // 20
    public uint IndicesOffset;       // 24
    public uint ExtensionDataOffset; // 28
    public ulong VertexEncodingHash; // 32
    public uint ExtensionDataSize;   // 40
    public uint Flags;            // 44

    public const int Size = 48;
    public const int MaxPageSize = 131072;
    public const int RootPageSize = 32768;
    public const uint RootPage = 1;
}

