using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using System.Collections;
using System.Reflection;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpRayTracingTests
{
    [Fact]
    public void Shader_table_update_is_snapshotted_and_dispatches_on_WARP()
    {
        const string source = """
            [shader("raygeneration")]
            void rayGenerationMain()
            {
            }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("rayGenerationMain", SlangStage.RayGeneration),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_ray_generation",
            source,
            entries);
        using IGraphicsBackend backend = CreateValidatedBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability));
        Assert.NotNull(capability);
        Assert.Equal(4_096U, capability.MaximumShaderRecordStride);
        Assert.Equal(1_073_741_824U, capability.MaximumRayGenerationShaderThreads);

        EntryPointReflection rayGeneration = shader.GetEntryPoint(0);
        EntryPointReflection[] rayGenerationEntries = [rayGeneration];
        using Pipeline pipeline = backend.CreateRayTracingPipeline(
            device,
            new RayTracingPipelineDesc(
                shader.Program,
                rayGenerationEntries,
                [],
                [],
                [],
                1,
                0,
                8));
        using RayTracingShaderTable table = backend.CreateRayTracingShaderTable(
            device,
            new RayTracingShaderTableDesc(pipeline, 1, 0, 0, 0, 32));
        using RayTracingShaderTable maximumStrideTable = backend.CreateRayTracingShaderTable(
            device,
            new RayTracingShaderTableDesc(
                pipeline,
                1,
                0,
                0,
                0,
                capability.MaximumShaderRecordStride));
        Assert.Throws<InvalidOperationException>(() =>
            backend.CreateRayTracingShaderTable(
                device,
                new RayTracingShaderTableDesc(
                    pipeline,
                    1,
                    0,
                    0,
                    0,
                    checked(capability.MaximumShaderRecordStride + 32))));
        RayTracingShaderRecord[] records =
        [
            RayTracingShaderRecord.Entry(
                rayGeneration,
                rayGeneration.VarLayout,
                0,
                0,
                0,
                0),
        ];

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.UpdateRayTracingShaderTable(
            context,
            table,
            new RayTracingShaderTableUpdate(records, [], [], [], [], []));
        Assert.Throws<InvalidOperationException>(() =>
            backend.DispatchRays(
                context,
                new DispatchRaysDesc(table, 32_768, 32_769)));
        backend.DispatchRays(context, new DispatchRaysDesc(table, 1));
        using RecordedCommands commands = backend.End(context);

        IList slots = (IList)GetRequiredField(context, "_slots").GetValue(context)!;
        object slot = Assert.Single(slots.Cast<object>());
        IDictionary activeSnapshots =
            (IDictionary)GetRequiredField(slot, "_rayTracingSnapshots").GetValue(slot)!;
        Assert.Empty(activeSnapshots);
        Assert.Equal(0, GetRequiredField(slot, "_rayTracingSnapshotCount").GetValue(slot));
        IList snapshotPool =
            (IList)GetRequiredField(slot, "_rayTracingSnapshotPool").GetValue(slot)!;
        object pooledSnapshot = Assert.Single(snapshotPool.Cast<object>());
        Assert.Equal(0, GetRequiredField(pooledSnapshot, "_rayGenerationCount").GetValue(pooledSnapshot));
        Array pooledRayGeneration =
            (Array)GetRequiredField(pooledSnapshot, "_rayGeneration").GetValue(pooledSnapshot)!;
        object clearedRecord = pooledRayGeneration.GetValue(0)!;
        Assert.Null(GetRequiredProperty(clearedRecord, "Export").GetValue(clearedRecord));

        records[0] = default;
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Compute),
            commands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    private static ValidationLayer<D3D12Backend> CreateValidatedBackend()
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        return new ValidationLayer<D3D12Backend>(
            new D3D12Backend(new D3D12BackendOptions(validation)));
    }

    private static FieldInfo GetRequiredField(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"{instance.GetType().FullName} has no non-public field named {name}.");

    private static PropertyInfo GetRequiredProperty(object instance, string name) =>
        instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"{instance.GetType().FullName} has no property named {name}.");

    [Fact]
    public void Acceleration_structure_build_update_clone_compact_serialize_and_deserialize_run_on_WARP()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability));
        Assert.NotNull(capability);
        Assert.True(capability.AccelerationStructureUpdate);
        Assert.True(capability.Compaction);
        Assert.True(capability.Serialization);

        const ulong vertexBytes = 9 * sizeof(float);
        using Buffer vertices = backend.CreateBuffer(
            device,
            new BufferDesc(vertexBytes, BufferUsages.AccelerationStructureInput),
            MemoryType.Upload);
        BufferRange vertexRange = new(0, vertexBytes);
        using (MappedBuffer mapping = backend.Map(vertices, MapType.Write, vertexRange))
        {
            float[] values =
            [
                -1, -1, 0,
                 0,  1, 0,
                 1, -1, 0,
            ];
            for (int index = 0; index < values.Length; index++)
            {
                BitConverter.TryWriteBytes(
                    mapping.Bytes.Slice(index * sizeof(float), sizeof(float)),
                    values[index]);
            }
            mapping.Flush(vertexRange);
        }

        AccelerationStructureGeometry[] geometries =
        [
            new(
                AccelerationStructureGeometryType.Triangles,
                new BufferRegion(vertices, vertexRange),
                Format.R32G32B32Float,
                3 * sizeof(float),
                3,
                default,
                default,
                AccelerationStructureGeometryOptions.Opaque),
        ];
        const AccelerationStructureBuildOptions buildOptions =
            AccelerationStructureBuildOptions.AllowUpdate |
            AccelerationStructureBuildOptions.AllowCompaction;
        AccelerationStructureBuildInfo buildInfo = backend.GetAccelerationStructureBuildInfo(
            device,
            AccelerationStructureType.BottomLevel,
            buildOptions,
            geometries);
        Assert.True(buildInfo.ResultSize > 0);
        Assert.True(buildInfo.BuildScratchSize > 0);

        ulong resultSize = AlignUp(buildInfo.ResultSize, buildInfo.ResultAlignment);
        ulong scratchSize = AlignUp(
            Math.Max(buildInfo.BuildScratchSize, buildInfo.UpdateScratchSize),
            Math.Max(buildInfo.BuildScratchAlignment, buildInfo.UpdateScratchAlignment));
        using Buffer storage = CreateAccelerationStructureStorage(backend, device, resultSize);
        using AccelerationStructure structure = backend.CreateAccelerationStructure(
            device,
            storage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using Buffer scratch = backend.CreateBuffer(
            device,
            new BufferDesc(scratchSize, BufferUsages.ShaderWrite),
            MemoryType.DeviceLocal);
        using Buffer cloneStorage = CreateAccelerationStructureStorage(
            backend,
            device,
            resultSize);
        using AccelerationStructure clone = backend.CreateAccelerationStructure(
            device,
            cloneStorage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using Buffer postBuild = backend.CreateBuffer(
            device,
            new BufferDesc(24, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using Buffer postReadback = backend.CreateBuffer(
            device,
            new BufferDesc(24, BufferUsages.CopyDestination),
            MemoryType.Readback);

        using CommandContext buildContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(buildContext);
        backend.Barrier(buildContext, new BufferBarrier(
            storage,
            PipelineSync.None,
            PipelineSync.BuildRayTracingAccelerationStructure,
            ResourceAccess.NoAccess,
            ResourceAccess.RayTracingAccelerationStructureWrite));
        backend.Barrier(buildContext, new BufferBarrier(
            scratch,
            PipelineSync.None,
            PipelineSync.BuildRayTracingAccelerationStructure,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.Barrier(buildContext, new BufferBarrier(
            cloneStorage,
            PipelineSync.None,
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.NoAccess,
            ResourceAccess.RayTracingAccelerationStructureWrite));
        backend.BuildAccelerationStructure(
            buildContext,
            new AccelerationStructureBuildDesc(
                AccelerationStructureType.BottomLevel,
                buildOptions,
                geometries,
                structure,
                scratch,
                BufferRange.Whole));
        backend.Barrier(buildContext, new MemoryBarrier(
            PipelineSync.BuildRayTracingAccelerationStructure,
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.RayTracingAccelerationStructureWrite,
            ResourceAccess.RayTracingAccelerationStructureRead));
        backend.CopyAccelerationStructure(
            buildContext,
            clone,
            structure,
            AccelerationStructureCopyType.Clone);
        using RecordedCommands buildCommands = backend.End(buildContext);
        Queue queue = backend.GetQueue(device, QueueType.Compute);
        QueueCompletion built = Submit(backend, queue, buildCommands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(built, TimeSpan.FromSeconds(10)));

        using CommandContext updateContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(updateContext);
        backend.Barrier(updateContext, new BufferBarrier(
            storage,
            PipelineSync.CopyRayTracingAccelerationStructure,
            PipelineSync.BuildRayTracingAccelerationStructure,
            ResourceAccess.RayTracingAccelerationStructureRead,
            ResourceAccess.RayTracingAccelerationStructureRead |
            ResourceAccess.RayTracingAccelerationStructureWrite));
        backend.Barrier(updateContext, new BufferBarrier(
            scratch,
            PipelineSync.BuildRayTracingAccelerationStructure,
            PipelineSync.BuildRayTracingAccelerationStructure,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.UnorderedAccess));
        backend.Barrier(updateContext, new BufferBarrier(
            postBuild,
            PipelineSync.None,
            PipelineSync.EmitAccelerationStructurePostBuildInfo,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.BuildAccelerationStructure(
            updateContext,
            new AccelerationStructureBuildDesc(
                AccelerationStructureType.BottomLevel,
                buildOptions | AccelerationStructureBuildOptions.PerformUpdate,
                geometries,
                structure,
                scratch,
                BufferRange.Whole,
                structure));
        backend.Barrier(updateContext, new MemoryBarrier(
            PipelineSync.BuildRayTracingAccelerationStructure,
            PipelineSync.EmitAccelerationStructurePostBuildInfo |
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.RayTracingAccelerationStructureWrite,
            ResourceAccess.RayTracingAccelerationStructureRead));
        backend.EmitAccelerationStructurePostBuildInfo(
            updateContext,
            structure,
            AccelerationStructurePostBuildInfoType.CompactedSize,
            postBuild,
            0);
        backend.EmitAccelerationStructurePostBuildInfo(
            updateContext,
            structure,
            AccelerationStructurePostBuildInfoType.SerializationSize,
            postBuild,
            8);
        backend.Barrier(updateContext, new BufferBarrier(
            postBuild,
            PipelineSync.EmitAccelerationStructurePostBuildInfo,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(updateContext, new BufferCopy(postBuild, 0, postReadback, 0, 24));
        using RecordedCommands updateCommands = backend.End(updateContext);
        QueueCompletion updated = Submit(backend, queue, updateCommands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(updated, TimeSpan.FromSeconds(10)));

        ulong compactedSize;
        ulong serializedSize;
        using (MappedBuffer mapping = backend.Map(postReadback, MapType.Read, new BufferRange(0, 24)))
        {
            mapping.Invalidate(new BufferRange(0, 24));
            compactedSize = BitConverter.ToUInt64(mapping.Bytes[0..8]);
            serializedSize = BitConverter.ToUInt64(mapping.Bytes[8..16]);
            Assert.Equal(0UL, BitConverter.ToUInt64(mapping.Bytes[16..24]));
        }
        Assert.InRange(compactedSize, 1UL, resultSize);
        Assert.True(serializedSize > 0);

        ulong compactStorageSize = AlignUp(
            compactedSize,
            capability.AccelerationStructureAlignment);
        using Buffer compactStorage = CreateAccelerationStructureStorage(
            backend,
            device,
            compactStorageSize);
        using AccelerationStructure compact = backend.CreateAccelerationStructure(
            device,
            compactStorage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using CommandContext compactContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(compactContext);
        backend.Barrier(compactContext, new BufferBarrier(
            compactStorage,
            PipelineSync.None,
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.NoAccess,
            ResourceAccess.RayTracingAccelerationStructureWrite));
        backend.CopyAccelerationStructure(
            compactContext,
            compact,
            structure,
            AccelerationStructureCopyType.Compact);
        using RecordedCommands compactCommands = backend.End(compactContext);
        QueueCompletion compacted = Submit(backend, queue, compactCommands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(compacted, TimeSpan.FromSeconds(10)));

        ulong serializedStorageSize = AlignUp(
            serializedSize,
            capability.AccelerationStructureAlignment);
        using Buffer serialized = backend.CreateBuffer(
            device,
            new BufferDesc(
                serializedStorageSize,
                BufferUsages.ShaderWrite | BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);
        using Buffer restoredStorage = CreateAccelerationStructureStorage(
            backend,
            device,
            resultSize);
        using AccelerationStructure restored = backend.CreateAccelerationStructure(
            device,
            restoredStorage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using CommandContext serializationContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(serializationContext);
        backend.Barrier(serializationContext, new BufferBarrier(
            compactStorage,
            PipelineSync.CopyRayTracingAccelerationStructure,
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.RayTracingAccelerationStructureWrite,
            ResourceAccess.RayTracingAccelerationStructureRead));
        backend.Barrier(serializationContext, new BufferBarrier(
            serialized,
            PipelineSync.None,
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.Barrier(serializationContext, new BufferBarrier(
            restoredStorage,
            PipelineSync.None,
            PipelineSync.CopyRayTracingAccelerationStructure,
            ResourceAccess.NoAccess,
            ResourceAccess.RayTracingAccelerationStructureWrite));
        backend.Barrier(serializationContext, new BufferBarrier(
            postBuild,
            PipelineSync.Copy,
            PipelineSync.EmitAccelerationStructurePostBuildInfo,
            ResourceAccess.CopySource,
            ResourceAccess.UnorderedAccess));
        backend.SerializeAccelerationStructure(
            serializationContext,
            new BufferRegion(serialized, new BufferRange(0, serializedSize)),
            compact);
        backend.Barrier(serializationContext, new BufferBarrier(
            serialized,
            PipelineSync.CopyRayTracingAccelerationStructure,
            PipelineSync.All,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.ShaderResource));
        backend.DeserializeAccelerationStructure(
            serializationContext,
            restored,
            new BufferRegion(serialized, new BufferRange(0, serializedSize)));
        backend.Barrier(serializationContext, new MemoryBarrier(
            PipelineSync.CopyRayTracingAccelerationStructure,
            PipelineSync.EmitAccelerationStructurePostBuildInfo,
            ResourceAccess.RayTracingAccelerationStructureWrite,
            ResourceAccess.RayTracingAccelerationStructureRead));
        backend.EmitAccelerationStructurePostBuildInfo(
            serializationContext,
            restored,
            AccelerationStructurePostBuildInfoType.CurrentSize,
            postBuild,
            0);
        backend.Barrier(serializationContext, new BufferBarrier(
            postBuild,
            PipelineSync.EmitAccelerationStructurePostBuildInfo,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(
            serializationContext,
            new BufferCopy(postBuild, 0, postReadback, 0, sizeof(ulong)));
        using RecordedCommands serializationCommands = backend.End(serializationContext);
        QueueCompletion serializedCompletion = Submit(backend, queue, serializationCommands);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(serializedCompletion, TimeSpan.FromSeconds(10)));
        using (MappedBuffer mapping = backend.Map(
            postReadback,
            MapType.Read,
            new BufferRange(0, sizeof(ulong))))
        {
            mapping.Invalidate(new BufferRange(0, sizeof(ulong)));
            Assert.InRange(BitConverter.ToUInt64(mapping.Bytes), 1UL, resultSize);
        }
        backend.CollectCompleted(device);
    }

    private static QueueCompletion Submit(
        IGraphicsBackend backend,
        Queue queue,
        RecordedCommands commands)
    {
        RecordedCommands[] batch = [commands];
        return backend.Submit(queue, new QueueSubmitDesc([], [], batch, [], []));
    }

    private static Buffer CreateAccelerationStructureStorage(
        IGraphicsBackend backend,
        Device device,
        ulong size) =>
        backend.CreateBuffer(
            device,
            new BufferDesc(size, BufferUsages.AccelerationStructure),
            MemoryType.DeviceLocal);

    private static ulong AlignUp(ulong value, ulong alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));
}
