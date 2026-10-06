using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Assets.Pipeline;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

public enum DepthCacheClass : uint
{
    Dynamic = 0,
    Static = 1,
}

/// <summary>Separates camera-deformed geometry from the player's world-space shadow representation.</summary>
public enum FirstPersonMeshKind : uint
{
    World = 0,
    FirstPerson = 1,
    WorldRepresentation = 2,
}

/// <summary>Selects views by the mesh and camera's caller-supplied player identifiers.</summary>
public enum MeshPlayerVisibility : uint
{
    All = 0,
    PlayerOnly = 1,
    OtherPlayers = 2,
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public readonly record struct FirstPersonMeshData
{
    private readonly uint _flags;
    public uint PlayerId { get; }
    public FirstPersonMeshKind Kind => (FirstPersonMeshKind)(_flags & 3u);
    public MeshPlayerVisibility PlayerVisibility => (MeshPlayerVisibility)(_flags >> 2);

    public FirstPersonMeshData(FirstPersonMeshKind kind, uint playerId,
        MeshPlayerVisibility playerVisibility = MeshPlayerVisibility.All)
    {
        _flags = (uint)kind | (uint)playerVisibility << 2;
        PlayerId = playerId;
    }
}

public struct MeshInstance : IComponent
{
    public required Mesh Mesh;
    public float BoundsExpansion;
    public DepthCacheClass DepthCacheClass;
    public FirstPersonMeshData FirstPerson;
    /// <summary>Optional explicit source for geometry pages not yet resident in Mesh.</summary>
    public MeshPayloadSource? PageSource;
}

