using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>A terrain-local volume and independent cell counts along its three axes.</summary>
public readonly record struct TerrainCollisionGrid(TerrainBounds Bounds, int CellsX, int CellsY, int CellsZ)
{
    public Vector3 CellSize => (Bounds.Max - Bounds.Min) / new Vector3(CellsX, CellsY, CellsZ);
    public Vector3 SamplePosition(Vector3 coordinates) => Bounds.Min + coordinates * CellSize;
    public TerrainBounds GetReadBounds(int border) => new(Bounds.Min - CellSize * border, Bounds.Max + CellSize * border);
}

/// <summary>Triangle geometry and the captured surface used to produce it, in terrain-local metres.</summary>
public sealed class TerrainCollisionMesh(TerrainSurfaceRead source, TerrainCollisionGrid grid,
    Vector3[] positions, uint[] indices)
{
    public TerrainSurfaceRead Source { get; } = source;
    public TerrainCollisionGrid Grid { get; } = grid;
    public ReadOnlyMemory<Vector3> Positions { get; } = positions;
    public ReadOnlyMemory<uint> Indices { get; } = indices;
}

public static class TerrainCollision
{
    /// <summary>Captures the submitted version on entry and builds after its CPU samples are ready.</summary>
    public static async ValueTask<TerrainCollisionMesh> BuildCollisionMeshAsync(this TerrainSurface surface,
        TerrainCollisionGrid grid, ITerrainMesher? mesher = null)
    {
        mesher ??= new MarchingCubesTerrainMesher();
        var result = new CollisionResult();
        TerrainSurfaceJob work = surface.ScheduleRead(grid.GetReadBounds(mesher.ReadBorder),
            new CollisionWork(grid, mesher, result), 1);
        await work.WaitAsync().ConfigureAwait(false);
        return result.Mesh!;
    }

    /// <summary>Builds from a fixed read covering the grid and the mesher's read border.</summary>
    public static TerrainCollisionMesh BuildCollisionMesh(this TerrainSurfaceRead source,
        TerrainCollisionGrid grid, ITerrainMesher? mesher = null)
    {
        mesher ??= new MarchingCubesTerrainMesher();
        var positions = new List<Vector3>();
        var indices = new List<uint>();
        var surface = new GridSurfaceRead(source, grid);
        for (int z = 0; z < grid.CellsZ; z++)
        for (int y = 0; y < grid.CellsY; y++)
        for (int x = 0; x < grid.CellsX; x++)
        {
            TerrainSurfaceMesh cell = mesher.Build(surface, new(new(x, y, z), 1, 1f));
            uint offset = (uint)positions.Count;
            foreach (Vector3 position in cell.Positions.Span) positions.Add(grid.SamplePosition(position));
            foreach (uint index in cell.Indices.Span) indices.Add(offset + index);
        }
        return new(source, grid, positions.ToArray(), indices.ToArray());
    }

    private sealed class GridSurfaceRead(TerrainSurfaceRead source, TerrainCollisionGrid grid)
        : TerrainSurfaceRead(source.Revision,
            new((source.Bounds.Min - grid.Bounds.Min) / grid.CellSize,
                (source.Bounds.Max - grid.Bounds.Min) / grid.CellSize), source.FieldVersions)
    {
        public override float Sample(Vector3 position) => source.Sample(grid.SamplePosition(position));
        public override JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => source.GetReadAccesses(scheduler);
    }

    private sealed class CollisionResult
    {
        public TerrainCollisionMesh? Mesh;
    }

    private readonly struct CollisionWork(TerrainCollisionGrid grid, ITerrainMesher mesher, CollisionResult result)
        : ITerrainSurfaceWork
    {
        public void Execute(TerrainSurfaceRead surface, int index) => result.Mesh = surface.BuildCollisionMesh(grid, mesher);
    }
}
