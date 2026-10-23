using System.Numerics;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

/// <summary>A completed resident set; retained paths and submitted jobs keep their tile objects.</summary>
public sealed class HeadingNavigationSceneContent(HeadingNavigationRegionGraph graph,
    ReadOnlyMemory<HeadingNavigationMeshTile> tiles = default, long version = 1)
{
    public HeadingNavigationRegionGraph Graph { get; } = graph;
    public ReadOnlyMemory<HeadingNavigationMeshTile> Tiles { get; } = tiles;
    public long Version { get; } = version;
    public bool TryGetTile(NavigationTileCoordinate coordinate, out HeadingNavigationMeshTile tile)
    {
        foreach (HeadingNavigationMeshTile candidate in Tiles.Span)
            if (candidate.Tile == coordinate) { tile = candidate; return true; }
        tile = null!;
        return false;
    }

    public HeadingNavigationMeshTile? FindTile(Vector3 position, float? heading = null)
    {
        HeadingNavigationMeshTile? result = null;
        float nearest = float.PositiveInfinity;
        foreach (HeadingNavigationMeshTile tile in Tiles.Span)
        {
            int region = tile.Mesh.FindRegion(position, heading);
            if (region < 0) continue;
            tile.Mesh.Geometry.TrySampleHeight(region, position, out float height);
            float distance = MathF.Abs(height - position.Y);
            if (distance < nearest) { nearest = distance; result = tile; }
        }
        return result;
    }

    public NavigationTileCoordinate? FindTileInDirectory(Vector3 position)
    {
        NavigationTileCoordinate? result = null;
        float nearest = float.PositiveInfinity;
        foreach (NavigationRegionTile tile in Graph.Tiles.Span)
        {
            NavigationBounds bounds = tile.Bounds;
            if (position.X < bounds.Minimum.X || position.X > bounds.Maximum.X || position.Z < bounds.Minimum.Z || position.Z > bounds.Maximum.Z) continue;
            float distance = MathF.Abs(position.Y - Math.Clamp(position.Y, bounds.Minimum.Y, bounds.Maximum.Y));
            if (distance < nearest) { nearest = distance; result = tile.Tile; }
        }
        return result;
    }
}

/// <summary>A heading representation with a persistent entrance graph and explicitly resident detailed tiles.</summary>
public sealed class HeadingNavigationScene(HeadingNavigationSceneContent content) : INavigationSpace<HeadingNavigationScene>
{
    private HeadingNavigationSceneContent _content = content;
    public HeadingNavigationSceneContent Content => Volatile.Read(ref _content);
    internal void Apply(HeadingNavigationMeshTile[] tiles) =>
        Volatile.Write(ref _content, new(Content.Graph, tiles, Content.Version + 1));
    public static long GetVersion(HeadingNavigationScene space) => space.Content.Version;
    public static bool TryGetHeight(HeadingNavigationScene space, Vector3 position, out float height)
    {
        HeadingNavigationMeshTile? tile = space.Content.FindTile(position);
        height = position.Y;
        return tile is not null && HeadingNavigationMesh.TryGetHeight(tile.Mesh, position, out height);
    }
    public static void CollectResources(HeadingNavigationScene space, ref JobResourceAccessBuilder resources)
    {
        resources.Read(space);
        HeadingNavigationSceneContent current = space.Content;
        HeadingNavigationRegionGraph.CollectResources(current.Graph, ref resources);
        resources.Read(current.Tiles);
        foreach (HeadingNavigationMeshTile tile in current.Tiles.Span) HeadingNavigationMesh.CollectResources(tile.Mesh, ref resources);
    }
}
