using System.Numerics;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

public sealed partial class TerrainScene
{
    private sealed class GeometryLevel
    {
        public Dictionary<(int X, int Z), List<GeometryData>> Columns { get; } = [];
    }

    private void RemoveGeometry(GeometryData geometry)
    {
        GeometryKey key = geometry.Key;
        Dictionary<int, GeometryLevel> levels = _geometryByTerrain[key.Terrain];
        GeometryLevel level = levels[key.Lod];
        var coordinate = (key.Coordinates.X, key.Coordinates.Z);
        List<GeometryData> column = level.Columns[coordinate];
        column.Remove(geometry);
        if (column.Count == 0) level.Columns.Remove(coordinate);
        if (level.Columns.Count == 0) levels.Remove(key.Lod);
        if (levels.Count == 0) _geometryByTerrain.Remove(key.Terrain);
    }

    private void MarkChangedGeometry(TerrainChange change)
    {
        if (!_geometryByTerrain.TryGetValue(change.Terrain, out var levels)) return;
        TerrainSurface? surface = change.Terrain.Surface;
        TerrainFieldInputs fields = GpuAdapter?.Attributes.Fields ?? _adapter?.Fields ?? default;
        Vector2 minimum = default, maximum = default;
        bool bounded = false;
        float minimumY = float.NegativeInfinity, maximumY = float.PositiveInfinity;
        if (surface is not null)
        {
            switch (change)
            {
                case HeightFieldChange height:
                    if (!surface.Uses(height.Field) && !fields.HeightFieldIds.AsSpan().Contains(height.Field.Id)) return;
                    float heightSpacing = height.Field.Grid.SampleSpacing;
                    float heightBorder = (height.ReadBorder + 1) * heightSpacing;
                    minimum = new Vector2(height.Range.MinX, height.Range.MinZ) * heightSpacing - new Vector2(heightBorder);
                    maximum = new Vector2(height.Range.MaxX - 1, height.Range.MaxZ - 1) * heightSpacing + new Vector2(heightBorder);
                    bounded = true;
                    break;
                case VoxelFieldChange voxel:
                    if (!surface.Uses(voxel.Field) && !fields.VoxelFieldIds.AsSpan().Contains(voxel.Field.Id)) return;
                    float voxelSpacing = voxel.Field.Grid.SampleSpacing;
                    float voxelBorder = (voxel.ReadBorder + 1) * voxelSpacing;
                    minimumY = voxel.Range.MinY * voxelSpacing - voxelBorder;
                    maximumY = (voxel.Range.MaxY - 1) * voxelSpacing + voxelBorder;
                    minimum = new Vector2(voxel.Range.MinX, voxel.Range.MinZ) * voxelSpacing - new Vector2(voxelBorder);
                    maximum = new Vector2(voxel.Range.MaxX - 1, voxel.Range.MaxZ - 1) * voxelSpacing + new Vector2(voxelBorder);
                    bounded = true;
                    break;
            }
        }
        var candidates = new List<GeometryData>();
        foreach ((int lod, GeometryLevel level) in levels)
        {
            if (!bounded)
            {
                foreach (List<GeometryData> column in level.Columns.Values) candidates.AddRange(column);
                continue;
            }
            float cellSize = MathF.ScaleB(Grid.CellSize, lod);
            float width = Grid.CellCount * cellSize;
            float border = ReadBorder * cellSize;
            int minX = (int)MathF.Ceiling((minimum.X - border) / width) - 1;
            int maxX = (int)MathF.Floor((maximum.X + border) / width);
            int minZ = (int)MathF.Ceiling((minimum.Y - border) / width) - 1;
            int maxZ = (int)MathF.Floor((maximum.Y + border) / width);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
                if (level.Columns.TryGetValue((x, z), out List<GeometryData>? column))
                    foreach (GeometryData geometry in column)
                        if ((geometry.Key.Coordinates.Y + 1) * width >= minimumY - border
                            && geometry.Key.Coordinates.Y * width <= maximumY + border)
                            candidates.Add(geometry);
        }
        _batches.TryGetValue(change.Terrain, out MeshBatch? batch);
        var input = new GeometryInfluence[candidates.Count];
        for (int index = 0; index < input.Length; index++)
        {
            GeometryData geometry = candidates[index];
            input[index] = new(geometry.Id, geometry.Revision, Grid.GetBounds(geometry.Key.Coordinates, geometry.Key.Lod),
                batch is not null && batch.Work.Revision >= change.Revision && batch.GeometryIds.Contains(geometry.Id));
        }
        var affected = new bool[input.Length];
        var work = new CalculateInfluence(change, surface, fields, ReadBorder, input, affected);
        if (WorldJobs.TryGetScheduler(_world!, out Scheduler? scheduler))
            scheduler.ExecuteParallel(work, input.Length, JobScheduleOptions.AutomaticBatchSize);
        else
            JobSystem.ExecuteParallel(work, input.Length, JobScheduleOptions.AutomaticBatchSize);
        for (int index = 0; index < affected.Length; index++)
            if (affected[index]) _dirtyGeometry.Add(input[index].Id);
    }

    private readonly record struct GeometryInfluence(int Id, ulong Revision, TerrainChunkBounds Bounds, bool BuildCoversChange);

    private readonly struct CalculateInfluence(TerrainChange change, TerrainSurface? surface, TerrainFieldInputs fields,
        int readBorder, GeometryInfluence[] input, bool[] output) : IJobParallelFor
    {
        public void Execute(int index)
        {
            GeometryInfluence geometry = input[index];
            if (geometry.Revision >= change.Revision || geometry.BuildCoversChange) return;
            float border = readBorder * geometry.Bounds.CellSize;
            bool attributeChanged = change switch
            {
                HeightFieldChange height => fields.HeightFieldIds.AsSpan().Contains(height.Field.Id) && height.Affects(geometry.Bounds.Volume, border),
                VoxelFieldChange voxel => fields.VoxelFieldIds.AsSpan().Contains(voxel.Field.Id) && voxel.Affects(geometry.Bounds.Volume, border),
                _ => false,
            };
            output[index] = surface is null || change.Affects(surface, geometry.Bounds.Volume, border) || attributeChanged;
        }
    }
}