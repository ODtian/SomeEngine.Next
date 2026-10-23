using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct NavigationGeometrySource(NavigationGeometry Geometry, Matrix4x4 Transform, ulong Version,
    bool Walkable = true, bool Solid = false);

public sealed record NavigationSurfaceSettings
{
    public float CellSize { get; init; } = 0.25f;
    public float CellHeight { get; init; } = 0.1f;
    public int TileSize { get; init; } = 32;
    /// <summary>World-space footprint padding, extended by one cell for neighboring columns.</summary>
    public float BorderSize { get; init; }
    public int BorderCells => (int)MathF.Ceiling(BorderSize / CellSize) + 1;
}

/// <summary>Fixed source references and versions used by all tiles of one build. Supplied arrays remain immutable during use.</summary>
public sealed class NavigationBuildInput(
    ReadOnlyMemory<NavigationGeometrySource> sources,
    NavigationBounds region,
    NavigationSurfaceSettings settings)
{
    public NavigationBuildInput(NavigationGeometrySource[] sources, NavigationBounds region,
        NavigationSurfaceSettings settings) : this((ReadOnlyMemory<NavigationGeometrySource>)sources, region, settings) { }

    public ReadOnlyMemory<NavigationGeometrySource> Sources { get; } = sources;
    public NavigationBounds Region { get; } = region;
    public NavigationSurfaceSettings Settings { get; } = settings;
}

/// <summary>Retains a request's inputs, workspace and output until its completion is consumed.</summary>
public sealed class NavigationBuild<TState>(TState state, JobHandle completion)
{
    public TState State { get; } = state;
    public JobHandle Completion { get; } = completion;
    public bool IsCompleted => Completion.IsCompleted;
    public TState Complete()
    {
        Completion.Complete();
        return State;
    }
}

public sealed class NavigationSurfaceBuildState
{
    public NavigationBuildInput Input { get; }
    /// <summary>The complete surface is available after the build's completion handle succeeds.</summary>
    public NavigationSurface Surface { get; internal set; } = null!;
    internal NavigationSurfaceTile[] Tiles { get; }
    internal ReadOnlyMemory<NavigationTileCoordinate> Coordinates { get; }
    internal List<NavigationSurfaceSpan>?[][] Columns { get; }

    internal NavigationSurfaceBuildState(NavigationBuildInput input, ReadOnlyMemory<NavigationTileCoordinate> coordinates)
    {
        Input = input;
        Coordinates = coordinates;
        Tiles = new NavigationSurfaceTile[coordinates.Length];
        Columns = new List<NavigationSurfaceSpan>?[coordinates.Length][];
    }
}
