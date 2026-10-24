using System.Numerics;
using SomeEngine.Core.Math;

namespace SomeEngine.Terrains;

internal static class TerrainLodSelection
{
    internal static TerrainChunkBounds[] Select(TerrainPlacement placement, TerrainChunkGrid grid, Vector3 cameraPosition)
    {
        var blocks = new List<TerrainChunkBounds>();
        TerrainChunkRange range = placement.Chunks;
        for (int z = range.MinZ; z < range.MaxZ; z++)
        for (int y = range.MinY; y < range.MaxY; y++)
        for (int x = range.MinX; x < range.MaxX; x++)
            SelectBlock(grid.GetBounds(new(x, y, z), placement.Lod));

        bool refined;
        do
        {
            refined = false;
            for (int first = 0; first < blocks.Count && !refined; first++)
            for (int second = first + 1; second < blocks.Count; second++)
            {
                if (Math.Abs(blocks[first].Lod - blocks[second].Lod) <= 1
                    || NeighborFace(blocks[first], blocks[second]) == TerrainTransitionFaces.None) continue;
                int coarse = blocks[first].Lod > blocks[second].Lod ? first : second;
                TerrainChunkBounds block = blocks[coarse];
                blocks.RemoveAt(coarse);
                blocks.AddRange(Children(block, grid));
                refined = true;
                break;
            }
        } while (refined);

        for (int first = 0; first < blocks.Count; first++)
        {
            TerrainTransitionFaces faces = TerrainTransitionFaces.None;
            for (int second = 0; second < blocks.Count; second++)
                if (blocks[second].Lod < blocks[first].Lod) faces |= NeighborFace(blocks[first], blocks[second]);
            blocks[first] = blocks[first] with { TransitionFaces = faces };
        }
        return blocks.ToArray();

        void SelectBlock(TerrainChunkBounds block)
        {
            if (block.Lod > 0 && placement.LodDistances is { } distances
                && Distance(block, placement.Transform, cameraPosition) < distances.RefinementDistances.Span[block.Lod - 1])
                foreach (TerrainChunkBounds child in Children(block, grid)) SelectBlock(child);
            else blocks.Add(block);
        }
    }

    private static IEnumerable<TerrainChunkBounds> Children(TerrainChunkBounds block, TerrainChunkGrid grid)
    {
        for (int z = 0; z < 2; z++)
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
            yield return grid.GetBounds(new(block.Coordinates.X * 2 + x, block.Coordinates.Y * 2 + y,
                block.Coordinates.Z * 2 + z), block.Lod - 1);
    }

    private static float Distance(TerrainChunkBounds block, TransformQvvs transform, Vector3 cameraPosition)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = transform.TransformPoint(new((corner & 1) == 0 ? block.Min.X : block.Max.X,
                (corner & 2) == 0 ? block.Min.Y : block.Max.Y, (corner & 4) == 0 ? block.Min.Z : block.Max.Z));
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return Vector3.Distance(cameraPosition, Vector3.Clamp(cameraPosition, min, max));
    }

    private static TerrainTransitionFaces NeighborFace(TerrainChunkBounds first, TerrainChunkBounds second)
    {
        Vector3 overlap = Vector3.Min(first.Max, second.Max) - Vector3.Max(first.Min, second.Min);
        for (int axis = 0; axis < 3; axis++)
        {
            if (overlap[(axis + 1) % 3] <= 0 || overlap[(axis + 2) % 3] <= 0) continue;
            if (first.Min[axis] == second.Max[axis]) return (TerrainTransitionFaces)(1 << (axis * 2));
            if (first.Max[axis] == second.Min[axis]) return (TerrainTransitionFaces)(2 << (axis * 2));
        }
        return TerrainTransitionFaces.None;
    }
}
