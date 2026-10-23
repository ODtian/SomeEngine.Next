using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>Captured scalar sources and their readiness, shared by every chunk in one geometry batch.</summary>
internal abstract record TerrainGpuSurfaceData(JobHandle Completion)
{
    internal static TerrainGpuSurfaceData Capture(TerrainSurface surface, TerrainBounds bounds) => surface switch
    {
        HeightSurface height => CaptureHeight(height.Field, bounds),
        VoxelSurface voxel => CaptureVoxels(voxel.Field, bounds),
        _ => throw new NotSupportedException($"GPU geometry requires a supported scalar source ({surface.GetType().Name})."),
    };

    internal abstract void AddBindings(RenderTerrainData fields, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension, Dictionary<string, GraphParameterResourceBinding> bindings);

    private static TerrainHeightGpuData CaptureHeight(HeightField field, TerrainBounds bounds)
    {
        float spacing = field.Grid.SampleSpacing;
        int minX = Math.Clamp((int)MathF.Floor(bounds.Min.X / spacing), 0, field.Grid.Width - 1);
        int minZ = Math.Clamp((int)MathF.Floor(bounds.Min.Z / spacing), 0, field.Grid.Depth - 1);
        int maxX = Math.Clamp((int)MathF.Floor(bounds.Max.X / spacing) + 2, 1, field.Grid.Width);
        int maxZ = Math.Clamp((int)MathF.Floor(bounds.Max.Z / spacing) + 2, 1, field.Grid.Depth);
        var range = new HeightRegion(minX, minZ, maxX - minX, maxZ - minZ);
        return new(field.Grid, field.InitialHeight, field.Data, range, field.PrepareGpuRead(range));
    }

    private static TerrainVoxelGpuData CaptureVoxels(VoxelField field, TerrainBounds bounds)
    {
        float spacing = field.Grid.SampleSpacing;
        int minX = (int)MathF.Floor(bounds.Min.X / spacing);
        int minY = (int)MathF.Floor(bounds.Min.Y / spacing);
        int minZ = (int)MathF.Floor(bounds.Min.Z / spacing);
        int maxX = (int)MathF.Floor(bounds.Max.X / spacing) + 2;
        int maxY = (int)MathF.Floor(bounds.Max.Y / spacing) + 2;
        int maxZ = (int)MathF.Floor(bounds.Max.Z / spacing) + 2;
        var range = new VoxelRegion(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ);
        return new(field.Grid, field.InitialValue, field.Data, range, field.PrepareGpuRead(range));
    }

    protected static GraphParameterResourceBinding ReadBuffer(ref RenderGraphFrame frame, GraphBufferSlice buffer) =>
        GraphParameterResourceBinding.ReadOnlyBuffer(frame.CreateBufferSrv(buffer, structureStride: sizeof(float)), PipelineSync.ComputeShading);
}

internal sealed record TerrainHeightGpuData(HeightGrid Grid, float InitialHeight, HeightFieldData Data,
    HeightRegion Range, JobHandle Completion) : TerrainGpuSurfaceData(Completion)
{
    internal override void AddBindings(RenderTerrainData fields, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension, Dictionary<string, GraphParameterResourceBinding> bindings)
    {
        HeightGpuInput input = fields.CreateHeightInput(ref frame, ref extension, Grid, Data, Range);
        var parameters = new HeightParameters(Grid.Width, Grid.Depth, Grid.SampleSpacing, InitialHeight,
            Grid.PageSize, input.MinPageX, input.MinPageZ, input.PageWidth);
        bindings["TerrainHeightParameters"] = GraphParameterResourceBinding.ConstantBuffer(frame.Upload(parameters, BufferUsages.Constant), PipelineSync.ComputeShading);
        bindings["TerrainHeightSamples"] = ReadBuffer(ref frame, input.Samples);
        bindings["TerrainHeightPageOffsets"] = ReadBuffer(ref frame, input.PageTable);
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private readonly record struct HeightParameters(int Width, int Depth, float Spacing, float InitialHeight,
        int PageSize, int SourcePageX, int SourcePageZ, int SourcePageWidth);
}

internal sealed record TerrainVoxelGpuData(VoxelGrid Grid, float InitialValue, VoxelFieldData Data,
    VoxelRegion Range, JobHandle Completion) : TerrainGpuSurfaceData(Completion)
{
    internal override void AddBindings(RenderTerrainData fields, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension, Dictionary<string, GraphParameterResourceBinding> bindings)
    {
        VoxelGpuInput input = fields.CreateVoxelInput(ref frame, ref extension, Grid, Data, Range);
        var parameters = new VoxelParameters(Grid.PageSize, input.MinPageX, input.MinPageY, input.MinPageZ,
            input.PageWidth, input.PageHeight, Grid.SampleSpacing, InitialValue);
        bindings["TerrainVoxelParameters"] = GraphParameterResourceBinding.ConstantBuffer(frame.Upload(parameters, BufferUsages.Constant), PipelineSync.ComputeShading);
        bindings["TerrainVoxelSamples"] = ReadBuffer(ref frame, input.Samples);
        bindings["TerrainVoxelPageOffsets"] = ReadBuffer(ref frame, input.PageTable);
    }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private readonly record struct VoxelParameters(int PageSize, int SourcePageX, int SourcePageY, int SourcePageZ,
        int SourcePageWidth, int SourcePageHeight, float Spacing, float InitialValue);
}
