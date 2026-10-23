using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Resources;

public sealed partial class Terrain
{
    /// <summary>Captures fields and graph on entry, then waits for every selected CPU page.</summary>
    public ValueTask<TerrainCpuData> CaptureCpuAsync()
    {
        lock (Gate)
        {
            var heights = ImmutableArray.CreateBuilder<HeightFieldValues>();
            var voxels = ImmutableArray.CreateBuilder<VoxelFieldValues>();
            var dependencies = new List<JobHandle>();
            foreach (HeightField field in _heightFields)
            {
                HeightFieldData data = field.Data;
                dependencies.Add(data.Edit.Completion);
                int size = field.Grid.PageSize;
                var pages = ImmutableArray.CreateBuilder<HeightPageValues>();
                foreach (var entry in data.Pages)
                {
                    dependencies.Add(field.CaptureCpuRead(new(entry.Key.X * size, entry.Key.Z * size, size, size)).Completion);
                    pages.Add(new(entry.Key.X, entry.Key.Z, entry.Value.Samples!, entry.Value.Revision, entry.Value.Id));
                }
                heights.Add(new(field.Id, field.Name, field.Grid, field.InitialHeight, data.Edit.Revision, pages.ToImmutable()));
            }
            foreach (VoxelField field in _voxelFields)
            {
                VoxelFieldData data = field.Data;
                dependencies.Add(data.Edit.Completion);
                int size = field.Grid.PageSize;
                var pages = ImmutableArray.CreateBuilder<VoxelPageValues>();
                foreach (var entry in data.Pages)
                {
                    dependencies.Add(field.CaptureCpuRead(new(entry.Key.X * size, entry.Key.Y * size,
                        entry.Key.Z * size, size, size, size)).Completion);
                    pages.Add(new(entry.Key.X, entry.Key.Y, entry.Key.Z, entry.Value.Samples!, entry.Value.Revision, entry.Value.Id));
                }
                voxels.Add(new(field.Id, field.Name, field.Grid, field.InitialValue, data.Edit.Revision, pages.ToImmutable()));
            }
            var nodes = new Dictionary<TerrainSurface, TerrainSurfaceValues>(ReferenceEqualityComparer.Instance);
            TerrainSurfaceValues? surface = _surface is null ? null : CaptureSurface(_surface, nodes);
            var values = new TerrainCpuData(_revision, heights.ToImmutable(), voxels.ToImmutable(), surface);
            JobHandle completion = Editing.Scheduler.CombineDependencies(CollectionsMarshal.AsSpan(dependencies));
            return CompleteCaptureAsync(values, completion);
        }
    }

    private static async ValueTask<TerrainCpuData> CompleteCaptureAsync(TerrainCpuData data, JobHandle completion)
    {
        await completion.WaitAsync().ConfigureAwait(false);
        return data;
    }

    private static TerrainSurfaceValues CaptureSurface(TerrainSurface surface,
        Dictionary<TerrainSurface, TerrainSurfaceValues> nodes)
    {
        if (nodes.TryGetValue(surface, out TerrainSurfaceValues? existing)) return existing;
        TerrainSurfaceValues values = surface switch
        {
            HeightSurface height => new HeightSurfaceValues(height.Field.Id),
            VoxelSurface voxel => new VoxelSurfaceValues(voxel.Field.Id),
            BlendSurface blend => new BlendSurfaceValues(CaptureSurface(blend.First, nodes), CaptureSurface(blend.Second, nodes),
                blend.Weight, blend.Region, blend.FadeDistance),
            _ => throw new NotSupportedException($"Surface type {surface.GetType().Name} has no CPU data representation."),
        };
        nodes.Add(surface, values);
        return values;
    }

    internal static Terrain FromCpuData(TerrainEditing editing, TerrainCpuData values)
    {
        var terrain = new Terrain(editing) { _revision = values.Revision };
        foreach (HeightFieldValues field in values.HeightFields)
        {
            var pages = field.Pages.ToDictionary(page => (page.X, page.Z),
                page => new TerrainFieldPage(GetSamples(page.Samples), default, page.Revision, page.Id));
            terrain._heightFields.Add(new HeightField(terrain, field.Id, field.Name, field.Grid, field.InitialHeight,
                new(pages, new(field.Revision, default))));
        }
        foreach (VoxelFieldValues field in values.VoxelFields)
        {
            var pages = field.Pages.ToDictionary(page => (page.X, page.Y, page.Z),
                page => new TerrainFieldPage(GetSamples(page.Samples), default, page.Revision, page.Id));
            terrain._voxelFields.Add(new VoxelField(terrain, field.Id, field.Name, field.Grid, field.InitialValue,
                new(pages, new(field.Revision, default))));
        }
        var nodes = new Dictionary<TerrainSurfaceValues, TerrainSurface>(ReferenceEqualityComparer.Instance);
        terrain._surface = values.Surface is null ? null : CreateSurface(values.Surface, terrain, nodes);
        return terrain;
    }

    private static float[] GetSamples(ReadOnlyMemory<float> samples) =>
        MemoryMarshal.TryGetArray(samples, out ArraySegment<float> array) && array.Offset == 0 && array.Count == array.Array!.Length
            ? array.Array : samples.ToArray();

    private static TerrainSurface CreateSurface(TerrainSurfaceValues values, Terrain terrain,
        Dictionary<TerrainSurfaceValues, TerrainSurface> nodes)
    {
        if (nodes.TryGetValue(values, out TerrainSurface? existing)) return existing;
        TerrainSurface surface = values switch
        {
            HeightSurfaceValues height => new HeightSurface(terrain.HeightFields[height.FieldId]),
            VoxelSurfaceValues voxel => new VoxelSurface(terrain.VoxelFields[voxel.FieldId]),
            BlendSurfaceValues blend when blend.Region is { } region => new BlendSurface(
                CreateSurface(blend.First, terrain, nodes), CreateSurface(blend.Second, terrain, nodes),
                blend.Weight, region, blend.FadeDistance),
            BlendSurfaceValues blend => new BlendSurface(CreateSurface(blend.First, terrain, nodes),
                CreateSurface(blend.Second, terrain, nodes), blend.Weight),
            _ => throw new NotSupportedException($"Surface data {values.GetType().Name} has no terrain source."),
        };
        nodes.Add(values, surface);
        return surface;
    }
}
