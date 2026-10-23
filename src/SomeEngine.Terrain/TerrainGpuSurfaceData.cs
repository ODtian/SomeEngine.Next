using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Render.Terrain;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>The fixed surface graph and field pages shared by every chunk in one geometry batch.</summary>
internal sealed record TerrainGpuSurfaceData(TerrainGpuSurfaceNode[] Nodes, TerrainGpuFieldData[] Fields, JobHandle Completion)
{
    internal static TerrainGpuSurfaceData Capture(TerrainSurface surface, TerrainBounds bounds)
    {
        var nodes = new List<TerrainGpuSurfaceNode>();
        var fields = new List<TerrainGpuFieldData>();
        var fieldIndices = new Dictionary<object, int>();
        AddNode(surface, -1);
        JobHandle completion = surface.Terrain.Editing.Scheduler.CombineDependencies(
            fields.Select(static field => field.Completion).ToArray());
        return new(nodes.ToArray(), fields.ToArray(), completion);

        int AddNode(TerrainSurface current, int parent)
        {
            int index = nodes.Count;
            nodes.Add(default);
            if (current is BlendSurface blend)
            {
                AddNode(blend.First, index);
                int second = AddNode(blend.Second, index);
                nodes[index] = new(parent, -1, second, blend.Weight, blend.Region?.Min ?? default,
                    blend.FadeDistance, blend.Region?.Max ?? default, blend.Region.HasValue ? 1 : 0);
            }
            else
            {
                object field = current switch
                {
                    HeightSurface height => height.Field,
                    VoxelSurface voxel => voxel.Field,
                    _ => throw new NotSupportedException($"GPU geometry requires a supported scalar source ({current.GetType().Name})."),
                };
                if (!fieldIndices.TryGetValue(field, out int source))
                {
                    source = fields.Count;
                    fieldIndices.Add(field, source);
                    fields.Add(field is HeightField height ? CaptureHeight(height, bounds) : CaptureVoxels((VoxelField)field, bounds));
                }
                nodes[index] = new(parent, source, -1, 0, default, 0, default, 0);
            }
            return index;
        }
    }

    internal void AddBindings(RenderTerrainData fields, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension, Dictionary<string, GraphParameterResourceBinding> bindings)
    {
        var sources = new TerrainGpuScalarSource[Fields.Length];
        var samples = new GraphBufferSlice[Fields.Length];
        var tables = new GraphBufferSlice[Fields.Length];
        ulong sampleBytes = 0, tableBytes = 0;
        for (int index = 0; index < Fields.Length; index++)
        {
            TerrainGpuFieldInput input = Fields[index].CreateInput(fields, ref frame, ref extension);
            sources[index] = input.Parameters with
            {
                SamplesStart = (int)(sampleBytes / sizeof(float)),
                PageOffsetsStart = (int)(tableBytes / sizeof(int)),
            };
            samples[index] = input.Samples;
            tables[index] = input.PageOffsets;
            sampleBytes += input.Samples.Range.Size;
            tableBytes += input.PageOffsets.Range.Size;
        }
        bindings["TerrainSources"] = ReadBuffer(ref frame, frame.Upload<TerrainGpuScalarSource>(sources, BufferUsages.ShaderRead), 64);
        bindings["TerrainSurfaceNodes"] = ReadBuffer(ref frame, frame.Upload<TerrainGpuSurfaceNode>(Nodes, BufferUsages.ShaderRead), 48);
        bindings["TerrainSourceSamples"] = ReadBuffer(ref frame, Combine(ref frame, ref extension, samples, sampleBytes));
        bindings["TerrainSourcePageOffsets"] = ReadBuffer(ref frame, Combine(ref frame, ref extension, tables, tableBytes));
    }

    private static GraphBufferSlice Combine(ref RenderGraphFrame frame, ref RenderGraphFrameExtension extension,
        GraphBufferSlice[] inputs, ulong bytes)
    {
        if (inputs.Length == 1) return inputs[0];
        GraphBufferId combined = frame.CreateBuffer(new BufferDesc(bytes,
            BufferUsages.CopyDestination | BufferUsages.ShaderRead, "Terrain surface inputs"));
        ulong offset = 0;
        foreach (GraphBufferSlice input in inputs)
        {
            extension.AddCopyPass("Gather terrain surface source", PassQueueSelection.AnyCompatible(),
                new SourceCopy(input, combined, offset), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref SourceCopy data) =>
                {
                    access.Read(data.Source.Buffer, data.Source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                    access.Write(data.Destination, new(data.Offset, data.Source.Range.Size), PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in SourceCopy data) =>
                    commands.CopyBuffer(new(commands.GetBuffer(data.Source.Buffer), data.Source.Range.Offset,
                        commands.GetBuffer(data.Destination), data.Offset, data.Source.Range.Size)));
            offset += input.Range.Size;
        }
        return new(combined, new(0, bytes));
    }

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

    private static GraphParameterResourceBinding ReadBuffer(ref RenderGraphFrame frame, GraphBufferSlice buffer, uint stride = sizeof(float)) =>
        GraphParameterResourceBinding.ReadOnlyBuffer(frame.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading);

    private readonly record struct SourceCopy(GraphBufferSlice Source, GraphBufferId Destination, ulong Offset);
}

[StructLayout(LayoutKind.Sequential, Size = 48)]
internal readonly record struct TerrainGpuSurfaceNode(int Parent, int Source, int SecondChild, float Weight,
    Vector3 RegionMin, float FadeDistance, Vector3 RegionMax, int HasRegion);

[StructLayout(LayoutKind.Sequential, Size = 64)]
internal readonly record struct TerrainGpuScalarSource(int Kind, int PageSize, int Width, int Depth,
    int SourcePageX, int SourcePageY, int SourcePageZ, int SourcePageWidth, int SourcePageHeight,
    float Spacing, float InitialValue, int SamplesStart = 0, int PageOffsetsStart = 0);

internal readonly record struct TerrainGpuFieldInput(GraphBufferSlice Samples, GraphBufferSlice PageOffsets, TerrainGpuScalarSource Parameters);

internal abstract record TerrainGpuFieldData(JobHandle Completion)
{
    internal abstract TerrainGpuFieldInput CreateInput(RenderTerrainData fields, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension);
}

internal sealed record TerrainHeightGpuData(HeightGrid Grid, float InitialHeight, HeightFieldData Data,
    HeightRegion Range, JobHandle Completion) : TerrainGpuFieldData(Completion)
{
    internal override TerrainGpuFieldInput CreateInput(RenderTerrainData fields, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension)
    {
        HeightGpuInput input = fields.CreateHeightInput(ref frame, ref extension, Grid, Data, Range);
        return new(input.Samples, input.PageTable, new(0, Grid.PageSize, Grid.Width, Grid.Depth,
            input.MinPageX, 0, input.MinPageZ, input.PageWidth, 1, Grid.SampleSpacing, InitialHeight));
    }
}

internal sealed record TerrainVoxelGpuData(VoxelGrid Grid, float InitialValue, VoxelFieldData Data,
    VoxelRegion Range, JobHandle Completion) : TerrainGpuFieldData(Completion)
{
    internal override TerrainGpuFieldInput CreateInput(RenderTerrainData fields, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension)
    {
        VoxelGpuInput input = fields.CreateVoxelInput(ref frame, ref extension, Grid, Data, Range);
        return new(input.Samples, input.PageTable, new(1, Grid.PageSize, 0, 0, input.MinPageX, input.MinPageY,
            input.MinPageZ, input.PageWidth, input.PageHeight, Grid.SampleSpacing, InitialValue));
    }
}
