using System.Numerics;
using DotRecast.Core;
using DotRecast.Recast;
using static DotRecast.Recast.RcRecast;

namespace SomeEngine.Navigation;

internal sealed record NavigationRecastField(RcCompactHeightfield Compact, int[] OriginalAreas,
    NavigationSurfaceSpan[] Supports, float[] Ceilings, List<int> AreaCodes);

internal static class NavigationRecast
{
    internal static NavigationMeshTileData BuildTile(NavigationSurface surface, NavigationSurfaceTile tile,
        NavigationProfile profile, NavigationPolicy policy, NavigationMeshSettings settings)
    {
        NavigationRecastField field = CreateField(surface, tile, profile, policy);
        bool[] covered = EvaluateFootprint(field,
            NavigationSurfaceTraversal.RasterizeFootprintSweep(profile.Footprint.Span, surface.Settings.CellSize), profile);
        for (int index = 0; index < covered.Length; index++)
            if (!covered[index]) field.Compact.areas[index] = RC_NULL_AREA;
        return Polygonize(surface, tile, field, field.AreaCodes, settings, out _);
    }

    internal static NavigationRecastField CreateField(NavigationSurface surface, NavigationSurfaceTile tile,
        NavigationProfile profile, NavigationPolicy policy)
    {
        var context = new RcContext();
        float cellSize = surface.Settings.CellSize, cellHeight = surface.Settings.CellHeight;
        int border = (int)MathF.Ceiling(NavigationSurfaceTraversal.GetRotationRadius(profile.Footprint.Span) / cellSize) + 3;
        int width = (int)MathF.Ceiling((tile.InteriorBounds.Maximum.X - tile.InteriorBounds.Minimum.X) / cellSize) + 2 * border;
        int depth = (int)MathF.Ceiling((tile.InteriorBounds.Maximum.Z - tile.InteriorBounds.Minimum.Z) / cellSize) + 2 * border;
        Vector3 origin = tile.InteriorBounds.Minimum - new Vector3(border * cellSize, cellHeight, border * cellSize);
        var field = new RcHeightfield(width, depth, new(origin.X, origin.Y, origin.Z),
            new(origin.X + width * cellSize, surface.Bounds.Maximum.Y, origin.Z + depth * cellSize), cellSize, cellHeight, border);
        var areaIds = new Dictionary<int, int>();
        var decodedAreas = new List<int> { 0 };
        var sourceSpans = new NavigationSurfaceSpan[width * depth][];
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            Vector2 point = new(origin.X + (x + 0.5f) * cellSize, origin.Z + (z + 0.5f) * cellSize);
            ReadOnlySpan<NavigationSurfaceSpan> column = surface.GetColumn(point);
            if (!surface.Bounds.Contains(point)) column = tile.GetColumn(point);
            int cell = z * width + x;
            sourceSpans[cell] = column.ToArray();
            RcSpan? previous = null;
            for (int layer = 0; layer < column.Length; layer++)
            {
                NavigationSurfaceSpan span = column[layer];
                float ceiling = layer + 1 < column.Length ? column[layer + 1].MinimumHeight : float.PositiveInfinity;
                if (!areaIds.TryGetValue(span.Area, out int area))
                {
                    area = decodedAreas.Count;
                    areaIds.Add(span.Area, area);
                    decodedAreas.Add(span.Area);
                }
                var solid = new RcSpan
                {
                    smin = (int)MathF.Floor((span.MinimumHeight - origin.Y) / cellHeight),
                    smax = (int)MathF.Ceiling((span.MaximumHeight - origin.Y) / cellHeight),
                    area = NavigationSurfaceTraversal.IsWalkable(span, ceiling, profile, policy) ? area : RC_NULL_AREA,
                };
                if (previous is null) field.spans[cell] = solid;
                else previous.next = solid;
                previous = solid;
            }
        }
        int walkableHeight = (int)MathF.Ceiling(profile.Height / cellHeight);
        int walkableClimb = (int)MathF.Floor(profile.MaximumStepHeight / cellHeight);
        RcFilters.FilterLowHangingWalkableObstacles(context, walkableClimb, field);
        RcFilters.FilterLedgeSpans(context, walkableHeight, walkableClimb, field);
        RcFilters.FilterWalkableLowHeightSpans(context, walkableHeight, field);
        // Keep the selected slope, policy and precise source clearance after Recast's occupancy filters.
        for (int cell = 0; cell < sourceSpans.Length; cell++)
        {
            RcSpan? solid = field.spans[cell];
            NavigationSurfaceSpan[] column = sourceSpans[cell];
            for (int layer = 0; layer < column.Length; layer++, solid = solid!.next)
            {
                float ceiling = layer + 1 < column.Length ? column[layer + 1].MinimumHeight : float.PositiveInfinity;
                if (!NavigationSurfaceTraversal.IsWalkable(column[layer], ceiling, profile, policy)) solid!.area = RC_NULL_AREA;
            }
        }
        RcCompactHeightfield compact = RcCompacts.BuildCompactHeightfield(context, walkableHeight, walkableClimb, field);
        (NavigationSurfaceSpan[] supports, float[] ceilings) = MapSupports(compact, sourceSpans, origin.Y, cellHeight);
        return new(compact, (int[])compact.areas.Clone(), supports, ceilings, decodedAreas);
    }

    internal static NavigationMeshTileData Polygonize(NavigationSurface surface, NavigationSurfaceTile tile,
        NavigationRecastField field, List<int> areas, NavigationMeshSettings settings, out int[] polygonClasses)
    {
        var context = new RcContext();
        RcCompactHeightfield compact = field.Compact;
        RcRegions.BuildRegionsMonotone(context, compact, settings.MinimumRegionCells, settings.MergeRegionCells);
        RcContourSet contours = RcContours.BuildContours(context, compact, settings.MaximumContourError,
            (int)MathF.Round(settings.MaximumEdgeLength / surface.Settings.CellSize), RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);
        RcPolyMesh mesh = RcMeshs.BuildPolyMesh(context, contours, settings.MaximumPolygonVertices);
        polygonClasses = mesh.areas.AsSpan(0, mesh.npolys).ToArray();
        if (mesh.npolys == 0) return new(new([], [], [], []), []);
        RcPolyMeshDetail detail = RcMeshDetails.BuildPolyMeshDetail(context, mesh, compact,
            settings.DetailSampleDistance, settings.DetailMaximumError);
        return Convert(surface, tile, mesh, detail, areas, settings.ContentVersion,
            NavigationSurfaceTraversal.GetRotationRadius(profile.Footprint.Span));
    }

    private static (NavigationSurfaceSpan[], float[]) MapSupports(RcCompactHeightfield compact,
        NavigationSurfaceSpan[][] columns, float originHeight, float cellHeight)
    {
        var supports = new NavigationSurfaceSpan[compact.spanCount];
        var ceilings = new float[compact.spanCount];
        for (int cell = 0; cell < compact.cells.Length; cell++)
        {
            RcCompactCell range = compact.cells[cell];
            for (int index = range.index; index < range.index + range.count; index++)
            {
                float height = originHeight + compact.spans[index].y * cellHeight;
                float closest = float.PositiveInfinity;
                for (int layer = 0; layer < columns[cell].Length; layer++)
                {
                    NavigationSurfaceSpan span = columns[cell][layer];
                    float distance = MathF.Abs(span.MaximumHeight - height);
                    if (distance >= closest) continue;
                    closest = distance;
                    supports[index] = span;
                    ceilings[index] = layer + 1 < columns[cell].Length ? columns[cell][layer + 1].MinimumHeight : float.PositiveInfinity;
                }
            }
        }
        return (supports, ceilings);
    }

    internal static bool[] EvaluateFootprint(NavigationRecastField field, NavigationCellOffset[] footprint, NavigationProfile profile)
    {
        RcCompactHeightfield compact = field.Compact;
        var result = new bool[compact.spanCount];
        for (int z = 0; z < compact.height; z++)
        for (int x = 0; x < compact.width; x++)
        {
            RcCompactCell range = compact.cells[z * compact.width + x];
            for (int index = range.index; index < range.index + range.count; index++)
            {
                bool admitted = field.OriginalAreas[index] != RC_NULL_AREA;
                foreach (NavigationCellOffset offset in footprint)
                {
                    int otherX = x + offset.X, otherZ = z + offset.Z;
                    bool covered = false;
                    if (otherX >= 0 && otherX < compact.width && otherZ >= 0 && otherZ < compact.height)
                    {
                        RcCompactCell other = compact.cells[otherZ * compact.width + otherX];
                        for (int neighbor = other.index; neighbor < other.index + other.count; neighbor++)
                            if (field.OriginalAreas[neighbor] != RC_NULL_AREA && NavigationSurfaceTraversal.CanTraverse(
                                field.Supports[index], field.Ceilings[index], field.Supports[neighbor], field.Ceilings[neighbor], profile))
                            { covered = true; break; }
                    }
                    if (!covered) { admitted = false; break; }
                }
                result[index] = admitted;
            }
        }
        return result;
    }

    private static NavigationMeshTileData Convert(NavigationSurface surface, NavigationSurfaceTile tile,
        RcPolyMesh mesh, RcPolyMeshDetail detail, List<int> areas, ulong contentVersion, float bakedRadius)
    {
        var positions = new Vector3[mesh.nverts];
        for (int index = 0; index < positions.Length; index++)
        {
            var position = new Vector3(mesh.bmin.X + mesh.verts[index * 3] * mesh.cs,
                mesh.bmin.Y + mesh.verts[index * 3 + 1] * mesh.ch, mesh.bmin.Z + mesh.verts[index * 3 + 2] * mesh.cs);
            positions[index] = RestoreHeight(surface, tile, position);
        }
        var details = new Vector3[detail.nverts];
        for (int index = 0; index < details.Length; index++)
            details[index] = RestoreHeight(surface, tile, new(detail.verts[index * 3], detail.verts[index * 3 + 1], detail.verts[index * 3 + 2]));
        var vertices = new List<int>();
        var polygons = new NavigationPolygon[mesh.npolys];
        var centers = new Vector3[mesh.npolys];
        var triangles = new List<NavigationDetailTriangle>();
        for (int index = 0; index < mesh.npolys; index++)
        {
            int vertexStart = vertices.Count, row = index * mesh.nvp * 2;
            for (int vertex = 0; vertex < mesh.nvp && mesh.polys[row + vertex] != RC_MESH_NULL_IDX; vertex++)
            {
                int position = mesh.polys[row + vertex];
                vertices.Add(position);
                centers[index] += positions[position];
            }
            int count = vertices.Count - vertexStart;
            centers[index] /= count;
            int detailVertex = detail.meshes[index * 4], detailTriangle = detail.meshes[index * 4 + 2];
            int triangleCount = detail.meshes[index * 4 + 3], triangleStart = triangles.Count;
            for (int triangle = detailTriangle; triangle < detailTriangle + triangleCount; triangle++)
                triangles.Add(new(detailVertex + detail.tris[triangle * 4], detailVertex + detail.tris[triangle * 4 + 1],
                    detailVertex + detail.tris[triangle * 4 + 2]));
            polygons[index] = new(vertexStart, count, 0, 0, areas[mesh.areas[index]], 0, triangleStart, triangleCount, tile.Coordinate);
        }
        var portals = new List<NavigationPortal>();
        var borders = new List<NavigationMeshBorder>();
        for (int index = 0; index < polygons.Length; index++)
        {
            NavigationPolygon polygon = polygons[index];
            int start = portals.Count, row = index * mesh.nvp * 2;
            for (int edge = 0; edge < polygon.VertexCount; edge++)
            {
                Vector3 a = positions[vertices[polygon.VertexStart + edge]];
                Vector3 b = positions[vertices[polygon.VertexStart + (edge + 1) % polygon.VertexCount]];
                int neighbor = mesh.polys[row + mesh.nvp + edge];
                if (neighbor == RC_MESH_NULL_IDX) continue;
                if ((neighbor & 0x8000) != 0) borders.Add(new(index, a, b));
                else portals.Add(NavigationMeshBuilder.Portal(neighbor, a, b, centers[index], centers[neighbor]));
            }
            polygons[index] = polygon with { PortalStart = start, PortalCount = portals.Count - start };
        }
        return new(new(positions, vertices.ToArray(), polygons, portals.ToArray(), details, triangles.ToArray(), contentVersion,
            bakedRadius: bakedRadius), borders.ToArray());
    }

    private static Vector3 RestoreHeight(NavigationSurface surface, NavigationSurfaceTile tile, Vector3 position)
    {
        position.X = Math.Clamp(position.X, tile.InteriorBounds.Minimum.X, tile.InteriorBounds.Maximum.X);
        position.Z = Math.Clamp(position.Z, tile.InteriorBounds.Minimum.Z, tile.InteriorBounds.Maximum.Z);
        float closest = float.PositiveInfinity, height = position.Y;
        // Mesh samples can lie on a cell edge; both touching cells contribute to the selected height layer.
        float reach = surface.Settings.CellSize * 0.01f;
        for (int z = -1; z <= 1; z++)
        for (int x = -1; x <= 1; x++)
        {
            Vector2 sample = new(position.X + x * reach, position.Z + z * reach);
            ReadOnlySpan<NavigationSurfaceSpan> column = surface.GetColumn(sample);
            if (!surface.Bounds.Contains(sample)) column = tile.GetColumn(sample);
            foreach (NavigationSurfaceSpan span in column)
            {
                float distance = MathF.Abs(span.Height - position.Y);
                if (span.Normal.Y <= 0 || distance >= closest) continue;
                closest = distance;
                height = span.Height;
            }
        }
        return new(position.X, height, position.Z);
    }
}
