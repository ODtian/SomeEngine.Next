using SomeEngine.Job;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

public static class VoxelGpuEditing
{
    /// <summary>Captures a value-type GPU brush and submits a new voxel version.</summary>
    public static TerrainEdit EditGpu<TBrush>(this VoxelField field, TBrush brush, JobHandle dependency = default)
        where TBrush : struct, IVoxelBrushGpu
    {
        Resources.Terrain terrain = field.Terrain;
        TerrainEdit edit;
        lock (terrain.Gate)
        {
            VoxelFieldData source = field.Data;
            VoxelRegion range = brush.Range;
            JobHandle input = field.PrepareGpuRead(range.Expand(brush.ReadBorder));
            var completion = new TerrainCompletion(terrain.Editing.Scheduler);
            edit = new TerrainEdit(terrain.NextRevision(), completion.Handle);
            var pages = new Dictionary<(int X, int Y, int Z), TerrainFieldPage>(source.Pages);
            var outputs = new List<VoxelPageWrite>();
            VoxelGrid grid = field.Grid;
            int size = grid.PageSize;
            for (int z = grid.PageCoordinate(range.MinZ); z * size < range.MaxZ; z++)
            for (int y = grid.PageCoordinate(range.MinY); y * size < range.MaxY; y++)
            for (int x = grid.PageCoordinate(range.MinX); x * size < range.MaxX; x++)
            {
                var page = new TerrainFieldPage(size * size * size, new TerrainGpuPageSource(), completion.Handle, edit.Revision);
                pages[(x, y, z)] = page;
                outputs.Add(new(x, y, z, page));
            }
            var request = new VoxelGpuRequest<TBrush>(terrain.NextRequestSequence(),
                terrain.Editing.Scheduler.CombineDependencies([input, dependency]), completion,
                grid, field.InitialValue, source, range, outputs.ToArray(), brush);
            terrain.RetainRequest(request);
            field.Data = new VoxelFieldData(pages, edit);
        }
        terrain.NotifyChange(new VoxelFieldChange(field, brush.Range, brush.ReadBorder, edit));
        return edit;
    }
}

internal readonly record struct VoxelPageWrite(int X, int Y, int Z, TerrainFieldPage Page);

internal sealed class VoxelGpuRequest<TBrush>(long sequence, JobHandle dependency, TerrainCompletion completion,
    VoxelGrid grid, float initialValue, VoxelFieldData source, VoxelRegion range, VoxelPageWrite[] outputs, TBrush brush)
    : TerrainGpuRequest(sequence, dependency, completion) where TBrush : struct, IVoxelBrushGpu
{
    internal override GraphPassId Record(RenderTerrainData data, ref RenderGraphFrame frame,
        ref TerrainFrameCommands extension)
    {
        if (outputs.Length == 0) return default;
        VoxelGpuInput input = data.CreateVoxelInput(ref frame, ref extension, grid, source, range.Expand(brush.ReadBorder));
        GraphPassId previous = default;
        foreach (VoxelPageWrite output in outputs)
        {
            var page = new VoxelRegion(output.X * grid.PageSize, output.Y * grid.PageSize, output.Z * grid.PageSize,
                grid.PageSize, grid.PageSize, grid.PageSize);
            GraphBufferId destination = data.GetOutput(ref frame, output.Page);
            var context = new VoxelBrushGpuContext(data, frame, extension, grid, range, page, initialValue,
                input, destination, previous);
            brush.Record(ref context);
            previous = context.LastPass;
        }
        return previous;
    }
}

internal readonly record struct VoxelGpuInput(GraphBufferSlice Samples, GraphBufferSlice PageTable,
    int MinPageX, int MinPageY, int MinPageZ, int PageWidth, int PageHeight);
