using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

internal abstract class TerrainGpuRequest(long sequence, JobHandle dependency, TerrainCompletion completion)
    : TerrainDataRequest(sequence, dependency, completion)
{
    internal abstract GraphPassId Record(RenderTerrainData data, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension);
}

internal sealed class HeightGpuRequest<TBrush>(long sequence, JobHandle dependency, TerrainCompletion completion,
    HeightGrid grid, float initialHeight, HeightFieldData source, HeightRegion range, HeightPageWrite[] outputs, TBrush brush)
    : TerrainGpuRequest(sequence, dependency, completion) where TBrush : struct, IHeightBrushGpu
{
    internal override GraphPassId Record(RenderTerrainData data, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension)
    {
        if (outputs.Length == 0) return default;
        HeightGpuInput input = data.CreateHeightInput(ref frame, ref extension, grid, source,
            range.Expand(brush.ReadBorder).Intersect(grid.Range));
        GraphPassId previous = default;
        foreach (HeightPageWrite output in outputs)
        {
            var page = new HeightRegion(output.X * grid.PageSize, output.Z * grid.PageSize, grid.PageSize, grid.PageSize);
            GraphBufferId destination = data.GetOutput(ref frame, output.Page);
            var context = new HeightBrushGpuContext(data, frame, extension, grid, range, page, initialHeight,
                input, destination, previous);
            brush.Record(ref context);
            previous = context.LastPass;
        }
        return previous;
    }
}

internal sealed class TerrainPageReadRequest(long sequence, TerrainFieldPage page, float[] samples,
    TerrainCompletion completion) : TerrainGpuRequest(sequence, page.Completion, completion)
{
    internal TerrainFieldPage Page { get; } = page;
    internal float[] Samples { get; } = samples;
    internal override GraphPassId Record(RenderTerrainData data, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension) => data.RecordReadback(this, ref frame, ref extension);
}

internal readonly record struct HeightGpuInput(GraphBufferSlice Samples, GraphBufferSlice PageTable,
    int MinPageX, int MinPageZ, int PageWidth);
