using SomeEngine.Job;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

public static class HeightGpuEditing
{
    /// <summary>Captures a value-type GPU brush and submits a new height version.</summary>
    public static TerrainEdit EditGpu<TBrush>(this HeightField field, TBrush brush, JobHandle dependency = default)
        where TBrush : struct, IHeightBrushGpu
    {
        Resources.Terrain terrain = field.Terrain;
        TerrainEdit edit;
        HeightRegion range;
        lock (terrain.Gate)
        {
            HeightFieldData source = field.Data;
            range = brush.Range.Intersect(field.Grid.Range);
            var completion = new TerrainCompletion(terrain.Editing.Scheduler);
            edit = new TerrainEdit(terrain.NextRevision(), completion.Handle);
            var pages = new Dictionary<(int X, int Z), TerrainFieldPage>(source.Pages);
            var outputs = new List<HeightPageWrite>();
            int size = field.Grid.PageSize;
            for (int z = range.MinZ / size; z * size < range.MaxZ; z++)
            for (int x = range.MinX / size; x * size < range.MaxX; x++)
            {
                var page = new TerrainFieldPage(size * size, new TerrainGpuPageSource(), completion.Handle);
                pages[(x, z)] = page;
                outputs.Add(new(x, z, page));
            }
            var request = new HeightGpuRequest<TBrush>(terrain.NextRequestSequence(),
                terrain.Editing.Scheduler.CombineDependencies([source.Edit.Completion, dependency]), completion,
                field.Grid, field.InitialHeight, source, range, outputs.ToArray(), brush);
            terrain.RetainRequest(request);
            field.Data = new HeightFieldData(pages, edit);
        }
        terrain.NotifyChange(new HeightFieldChange(field, range, brush.ReadBorder, edit));
        return edit;
    }
}

internal readonly record struct HeightPageWrite(int X, int Z, TerrainFieldPage Page);

internal sealed class TerrainGpuPageSource : ITerrainPageSource
{
    public TerrainDataRequest CreateReadRequest(Resources.Terrain terrain, TerrainFieldPage page, float[] samples,
        TerrainCompletion completion) => new TerrainPageReadRequest(terrain.NextRequestSequence(), page, samples, completion);
}
