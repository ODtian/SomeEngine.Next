using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Terrain;

/// <summary>Prepares fixed trilinear cells, bounds distances, checks zero crossings and writes new pages on the GPU.</summary>
public readonly record struct SubdividedVoxelDistanceGpu(Shader Shader, int MaxSubdivisions = 10) : IVoxelDistanceGpu
{
    public void Record(ref VoxelDistanceGpuContext context)
    {
        VoxelRegion range = context.Settings.Range, read = context.Settings.ReadRegion;
        var parameters = new DistanceParameters(context.Grid.PageSize,
            context.SourcePageX, context.SourcePageY, context.SourcePageZ, context.SourcePageWidth, context.SourcePageHeight,
            read.MinX, read.MinY, read.MinZ, read.Width, read.Height, read.Depth,
            range.MinX, range.MinY, range.MinZ, range.Width, range.Height, range.Depth,
            0, 0, 0, MaxSubdivisions,
            context.InitialValue, context.Grid.SampleSpacing, context.Settings.Precision.DistanceTolerance);
        RenderGraphFrame graph = context.Graph;
        int cellCount = (read.Width - 1) * (read.Height - 1) * (read.Depth - 1);
        int sampleCount = range.Width * range.Height * range.Depth;
        GraphBufferId cells = graph.CreateBuffer(new((ulong)cellCount * 36,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Terrain distance source cells"));
        GraphBufferId distances = graph.CreateBuffer(new((ulong)sampleCount * sizeof(float),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Terrain rebuilt distances"));
        GraphBufferId errors = graph.CreateBuffer(new((ulong)sampleCount * sizeof(float),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Terrain distance bounds"));
        GraphParameterResourceBinding source = GraphParameterResourceBinding.ReadOnlyBuffer(
            graph.CreateBufferSrv(context.SourceSamples, structureStride: sizeof(float)), PipelineSync.ComputeShading);
        GraphParameterResourceBinding offsets = GraphParameterResourceBinding.ReadOnlyBuffer(
            graph.CreateBufferSrv(context.SourcePageOffsets, structureStride: sizeof(int)), PipelineSync.ComputeShading);
        GraphBufferUavId cellView = graph.CreateBufferUav(cells, structureStride: 36);
        GraphBufferUavId distanceView = graph.CreateBufferUav(distances, structureStride: sizeof(float));
        GraphBufferUavId errorView = graph.CreateBufferUav(errors, structureStride: sizeof(float));
        GraphBufferUavId accuracy = graph.CreateBufferUav(context.Accuracy, structureStride: sizeof(float));
        GraphParameterResourceBinding unused = GraphParameterResourceBinding.Null(ResourceBindingType.BufferUav);
        context.AddCompute(Shader, "PrepareDistanceCells", parameters, new((uint)(cellCount + 63) / 64, 1, 1),
        [
            source, offsets,
            GraphParameterResourceBinding.WritableBuffer(cellView, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            unused, unused,
            GraphParameterResourceBinding.WritableBuffer(accuracy, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            unused,
        ]);
        context.AddCompute(Shader, "RebuildDistances", parameters, new((uint)(sampleCount + 63) / 64, 1, 1),
        [
            source, offsets,
            GraphParameterResourceBinding.WritableBuffer(cellView, PipelineSync.ComputeShading, GraphAccessMode.Read),
            GraphParameterResourceBinding.WritableBuffer(distanceView, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            GraphParameterResourceBinding.WritableBuffer(errorView, PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            unused, unused,
        ]);
        context.AddCompute(Shader, "CheckDistanceAccuracy", parameters, new((uint)(sampleCount + 63) / 64, 1, 1),
        [
            source, offsets, unused,
            GraphParameterResourceBinding.WritableBuffer(distanceView, PipelineSync.ComputeShading, GraphAccessMode.Read),
            GraphParameterResourceBinding.WritableBuffer(errorView, PipelineSync.ComputeShading, GraphAccessMode.Read),
            GraphParameterResourceBinding.WritableBuffer(accuracy, PipelineSync.ComputeShading, GraphAccessMode.ReadWrite),
            unused,
        ]);
        uint groups = (uint)(context.Grid.PageSize + 3) / 4;
        foreach (VoxelDistanceGpuPage page in context.Pages)
        {
            DistanceParameters output = parameters with { PageX = page.Range.MinX, PageY = page.Range.MinY, PageZ = page.Range.MinZ };
            context.AddCompute(Shader, "WriteDistancePage", output, new(groups, groups, groups),
            [
                source, offsets, unused,
                GraphParameterResourceBinding.WritableBuffer(distanceView, PipelineSync.ComputeShading, GraphAccessMode.Read),
                unused, unused,
                GraphParameterResourceBinding.WritableBuffer(graph.CreateBufferUav(page.Output, structureStride: sizeof(float)),
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete),
            ]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DistanceParameters(int PageSize,
        int SourcePageX, int SourcePageY, int SourcePageZ, int SourcePageWidth, int SourcePageHeight,
        int ReadMinX, int ReadMinY, int ReadMinZ, int ReadWidth, int ReadHeight, int ReadDepth,
        int MinX, int MinY, int MinZ, int Width, int Height, int Depth,
        int PageX, int PageY, int PageZ, int MaxSubdivisions,
        float InitialValue, float SampleSpacing, float DistanceTolerance);
}
