namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;

public sealed class VulkanAdvancedStateTests
{
    [Fact]
    public void Extended_dynamic_conditional_mesh_and_vrs_capabilities_are_enabled()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.MeshShaders | DeviceFeatures.VariableRateShading));
        Assert.True(backend.TryGetCapability(device, out MeshShaders? mesh));
        Assert.NotNull(mesh);
        Assert.True(mesh.MaximumOutputVertices > 0);
        Assert.True(backend.TryGetCapability(device, out VariableRateShading? shading));
        Assert.NotNull(shading);
        Assert.Contains(ShadingRate.Rate1x1, shading.Rates.ToArray());
        Assert.NotEqual(
            DynamicStates.None,
            device.Capabilities.SupportedDynamicStates & DynamicStates.PrimitiveTopology);
        Assert.NotEqual(
            DynamicStates.None,
            device.Capabilities.SupportedDynamicStates & DynamicStates.StripCut);

        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Buffer predicate = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopySource | BufferUsages.Predication),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(predicate, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapped.Bytes, 1u);
            mapped.Flush(mapped.Range);
        }
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.SetPrimitiveTopology(context, PrimitiveTopology.TriangleList);
        backend.SetStripCut(context, StripCut.Disabled);
        backend.SetPredication(context, predicate, 0, PredicationOperation.NotEqualZero);
        backend.SetPredication(context, null);
        backend.SetShadingRate(
            context,
            ShadingRate.Rate1x1,
            ShadingRateCombiner.Passthrough,
            ShadingRateCombiner.Passthrough);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Mesh_pipeline_direct_and_indirect_dispatch_render_pixels()
    {
        const string source = """
            struct MeshVertex { float4 Position : SV_Position; };
            [shader("mesh")]
            [outputtopology("triangle")]
            [numthreads(1, 1, 1)]
            void meshMain(
                out vertices MeshVertex outputVertices[3],
                out indices uint3 outputTriangles[1])
            {
                SetMeshOutputCounts(3, 1);
                outputVertices[0].Position = float4(-1, -1, 0, 1);
                outputVertices[1].Position = float4(0, 1, 0, 1);
                outputVertices[2].Position = float4(1, -1, 0, 1);
                outputTriangles[0] = uint3(0, 1, 2);
            }
            [shader("fragment")]
            float4 pixelMain() : SV_Target0 { return float4(0.25, 0.5, 0.75, 1); }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("meshMain", SlangStage.Mesh),
            ("pixelMain", SlangStage.Fragment));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.MeshShaders));
        Format[] formats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blend = [new()];
        using Pipeline pipeline = backend.CreateMeshPipeline(
            device,
            new MeshPipelineDesc(
                shader.Program,
                shader.Entries[0],
                EntryPointReflection.Null,
                shader.Entries[1],
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(),
                new DepthStencilState(),
                new BlendState(blend),
                new AttachmentFormatSignature(formats, null),
                DynamicStates.Viewport | DynamicStates.Scissor));
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                8,
                8,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView view = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(target, range, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(8 * 8 * 4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Buffer indirect = backend.CreateBuffer(
            device,
            new BufferDesc(12, BufferUsages.CopySource | BufferUsages.Indirect),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(indirect, MapType.Write, BufferRange.Whole))
        {
            BitConverter.TryWriteBytes(mapped.Bytes[0..4], 1u);
            BitConverter.TryWriteBytes(mapped.Bytes[4..8], 1u);
            BitConverter.TryWriteBytes(mapped.Bytes[8..12], 1u);
            mapped.Flush(mapped.Range);
        }
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            target, range, PipelineSync.None, PipelineSync.RenderTarget,
            ResourceAccess.NoAccess, ResourceAccess.RenderTarget,
            TextureLayout.Undefined, TextureLayout.RenderTarget));
        ColorAttachmentDesc[] colors = [new(view, LoadType.Clear, StoreType.Store, default)];
        backend.BeginRendering(context, new RenderingDesc(colors, null, 8, 8));
        backend.SetPipeline(context, pipeline);
        backend.SetViewports(context, [new Viewport(0, 0, 8, 8)]);
        backend.SetScissors(context, [new ScissorRect(0, 0, 8, 8)]);
        backend.DispatchMesh(context, new DispatchArguments(1, 1, 1));
        backend.DispatchMeshIndirect(context, new BufferRegion(indirect, BufferRange.Whole));
        backend.EndRendering(context);
        backend.Barrier(context, new TextureBarrier(
            target, range, PipelineSync.RenderTarget, PipelineSync.Copy,
            ResourceAccess.RenderTarget, ResourceAccess.CopySource,
            TextureLayout.RenderTarget, TextureLayout.CopySource));
        backend.CopyTextureToBuffer(context, new BufferTextureCopy(
            readback, 0, 8 * 4, 8, target, 0, 0, TextureAspects.Color,
            0, 0, 0, 8, 8, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(2)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        int center = 4 * 8 * 4 + 4 * 4;
        Assert.InRange(result.Bytes[center], (byte)50, (byte)80);
        Assert.InRange(result.Bytes[center + 1], (byte)110, (byte)145);
        Assert.InRange(result.Bytes[center + 2], (byte)175, (byte)210);
    }

    [Fact]
    public void Khr_ray_tracing_capability_reports_native_limits()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.RayTracing));
        Assert.True(backend.TryGetCapability(device, out RayTracing? rayTracing));
        Assert.NotNull(rayTracing);
        Assert.True(rayTracing.PipelineRayTracing);
        Assert.True(rayTracing.MaximumRecursionDepth > 0);
        Assert.True(rayTracing.MaximumShaderRecordStride > 0);
        Assert.True(rayTracing.ScratchAlignment > 0);
    }

    [Fact]
    public void Khr_bottom_level_acceleration_structure_sizes_builds_and_clones()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.RayTracing));
        using Buffer vertices = backend.CreateBuffer(
            device,
            new BufferDesc(
                9 * sizeof(float),
                BufferUsages.CopySource | BufferUsages.AccelerationStructureInput),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(vertices, MapType.Write, BufferRange.Whole))
        {
            float[] values = [-1, -1, 0, 0, 1, 0, 1, -1, 0];
            for (int index = 0; index < values.Length; index++)
                BitConverter.TryWriteBytes(mapped.Bytes.Slice(index * sizeof(float), sizeof(float)), values[index]);
            mapped.Flush(mapped.Range);
        }
        AccelerationStructureGeometry[] geometries =
        [
            new(
                AccelerationStructureGeometryType.Triangles,
                new BufferRegion(vertices, BufferRange.Whole),
                Format.R32G32B32Float,
                3 * sizeof(float),
                3,
                default,
                default,
                AccelerationStructureGeometryOptions.Opaque),
        ];
        AccelerationStructureBuildInfo sizing = backend.GetAccelerationStructureBuildInfo(
            device,
            AccelerationStructureType.BottomLevel,
            AccelerationStructureBuildOptions.AllowCompaction,
            geometries);
        Assert.True(sizing.ResultSize > 0);
        Assert.True(sizing.BuildScratchSize > 0);
        ulong storageSize = Align(sizing.ResultSize, sizing.ResultAlignment);
        ulong scratchSize = Align(sizing.BuildScratchSize, sizing.BuildScratchAlignment);
        using Buffer storage = backend.CreateBuffer(
            device,
            new BufferDesc(storageSize, BufferUsages.AccelerationStructure));
        using Buffer cloneStorage = backend.CreateBuffer(
            device,
            new BufferDesc(storageSize, BufferUsages.AccelerationStructure));
        using Buffer scratch = backend.CreateBuffer(
            device,
            new BufferDesc(scratchSize, BufferUsages.ShaderWrite));
        using AccelerationStructure structure = backend.CreateAccelerationStructure(
            device,
            storage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using AccelerationStructure clone = backend.CreateAccelerationStructure(
            device,
            cloneStorage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.BuildAccelerationStructure(
            context,
            new AccelerationStructureBuildDesc(
                AccelerationStructureType.BottomLevel,
                AccelerationStructureBuildOptions.AllowCompaction,
                geometries,
                structure,
                scratch,
                BufferRange.Whole));
        backend.Barrier(context, new MemoryBarrier(
            PipelineSync.BuildRayTracingAccelerationStructure,
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.RayTracingAccelerationStructureWrite,
            ResourceAccess.RayTracingAccelerationStructureRead));
        backend.CopyAccelerationStructure(
            context,
            clone,
            structure,
            AccelerationStructureCopyType.Clone);
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));

        static ulong Align(ulong value, ulong alignment) =>
            checked((value + alignment - 1) & ~(alignment - 1));
    }

    [Fact]
    public void Khr_ray_pipeline_shader_table_and_trace_rays_execute()
    {
        const string source = """
            RWStructuredBuffer<uint> outputValues;
            [shader("raygeneration")]
            void rayGenerationMain() { outputValues[0] = 11; }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("rayGenerationMain", SlangStage.RayGeneration));
        EntryPointReflection rayGeneration = shader.Entries[0];
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.RayTracing));
        using Pipeline pipeline = backend.CreateRayTracingPipeline(
            device,
            new RayTracingPipelineDesc(
                shader.Program,
                [rayGeneration],
                [],
                [],
                [],
                1,
                4,
                8));
        using RayTracingShaderTable table = backend.CreateRayTracingShaderTable(
            device,
            new RayTracingShaderTableDesc(pipeline, 1, 0, 0, 0, 32));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(8, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(8, BufferUsages.CopyDestination),
            MemoryType.Readback);
        RayTracingShaderRecord record = RayTracingShaderRecord.Entry(rayGeneration, 0, 1);
        RayTracingLocalParameterBlock local = new(rayGeneration.VarLayout, 0, 0, 0, 0);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                globals,
                [ResourceBinding.WritableBuffer(outputUav)],
                []));
        backend.UpdateRayTracingShaderTable(
            context,
            table,
            new RayTracingShaderTableUpdate(
                [record],
                [],
                [],
                [],
                [local],
                [],
                []));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.RayTracing,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.DispatchRays(context, new DispatchRaysDesc(table, 1));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.RayTracing,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 8));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(queue, new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(11u, BitConverter.ToUInt32(mapped.Bytes));
    }
}
