using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using System.Collections;
using System.Reflection;
using Xunit;
using NativePrebuildInfo = Silk.NET.Direct3D12.RaytracingAccelerationStructurePrebuildInfo;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpRayTracingTests
{
    [Fact]
    public void Dxr_local_parameter_groups_execute_two_exports_with_descriptors_and_ordinary_data()
    {
        const string source = """
            struct CallablePayload { uint value; };
            [shader("raygeneration")]
            void rayGenerationMain()
            {
                CallablePayload payload = { 0 };
                CallShader(0, payload);
                CallShader(1, payload);
            }
            struct CallableParameters
            {
                RWStructuredBuffer<uint> outputValues;
                SamplerState localSampler;
                uint outputIndex;
                uint recordValue;
            };
            [shader("callable")]
            void callableA(
                inout CallablePayload payload,
                uniform CallableParameters parameters)
            {
                parameters.outputValues[parameters.outputIndex] = parameters.recordValue;
            }
            [shader("callable")]
            void callableB(
                inout CallablePayload payload,
                uniform CallableParameters parameters)
            {
                parameters.outputValues[parameters.outputIndex] = parameters.recordValue;
            }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("rayGenerationMain", SlangStage.RayGeneration),
            new("callableA", SlangStage.Callable),
            new("callableB", SlangStage.Callable),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "dxr_local_callable_bindings", source, entries, "lib_6_5");
        ShaderEntryPoint rayGeneration = shader.GetEntryPoint(0);
        ShaderEntryPoint callableA = shader.GetEntryPoint(1);
        ShaderEntryPoint callableB = shader.GetEntryPoint(2);
        Assert.Equal((nuint)0,
            callableA.VarLayout.GetOffset(SlangParameterCategory.UnorderedAccess));
        Assert.Equal((nuint)1,
            callableB.VarLayout.GetOffset(SlangParameterCategory.UnorderedAccess));
        Assert.Equal((nuint)0,
            callableA.VarLayout.GetOffset(SlangParameterCategory.SamplerState));
        Assert.Equal((nuint)1,
            callableB.VarLayout.GetOffset(SlangParameterCategory.SamplerState));
        Assert.Equal((nuint)0,
            callableA.VarLayout.GetOffset(SlangParameterCategory.ConstantBuffer));
        Assert.Equal((nuint)1,
            callableB.VarLayout.GetOffset(SlangParameterCategory.ConstantBuffer));
        using IGraphicsBackend backend = CreateValidatedBackend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability));
        Assert.NotNull(capability);
        using Pipeline pipeline = backend.CreateRayTracingPipeline(device,
            new RayTracingPipelineDesc(shader.Program, [rayGeneration], [],
                [callableA, callableB], [], 1, 4, 8));
        using RayTracingShaderTable table = backend.CreateRayTracingShaderTable(device,
            new RayTracingShaderTableDesc(pipeline, 1, 0, 0, 2, 64));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Sampler sampler = backend.CreateSampler(device,
            new SamplerDesc(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        RayTracingShaderRecord rayRecord = RayTracingShaderRecord.Entry(
            rayGeneration, 0, 1);
        RayTracingShaderRecord firstCallableRecord = RayTracingShaderRecord.Entry(
            callableA, 1, 1);
        RayTracingShaderRecord secondCallableRecord = RayTracingShaderRecord.Entry(
            callableB, 2, 1);
        RayTracingLocalParameterBlock[] parameterBlocks =
        [
            new(rayGeneration.VarLayout, 0, 0, 0, 0),
            new(callableA.VarLayout, 0, 2, 0, 2 * sizeof(uint)),
            new(callableB.VarLayout, 2, 2, 2 * sizeof(uint), 2 * sizeof(uint)),
        ];
        ResourceBinding[] resources =
        [
            ResourceBinding.WritableBuffer(outputUav),
            ResourceBinding.SampledWith(sampler),
            ResourceBinding.WritableBuffer(outputUav),
            ResourceBinding.SampledWith(sampler),
        ];
        byte[] ordinary = System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(
            [0u, 37u, 1u, 53u]).ToArray();

        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 8, 128));
        backend.SetPipeline(context, pipeline);
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.RayTracing, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        backend.UpdateRayTracingShaderTable(context, table,
            new RayTracingShaderTableUpdate(
                [rayRecord], [], [], [firstCallableRecord, secondCallableRecord],
                parameterBlocks, resources, ordinary));
        backend.DispatchRays(context, new DispatchRaysDesc(table, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.RayTracing,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 8));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend, backend.GetQueue(device, QueueType.Compute), commands);
        Assert.Equal(WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 8));
        mapped.Invalidate(new BufferRange(0, 8));
        Assert.Equal([37u, 53u], System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            mapped.Bytes).ToArray());
    }

    [Fact]
    public void Dxr_ordinary_only_and_descriptor_only_entry_local_parameters_create()
    {
        const string ordinarySource = """
            struct Payload { uint value; };
            [shader("raygeneration")] void rayGenerationMain()
            { Payload payload = { 0 }; CallShader(0, payload); }
            [shader("callable")]
            void callableMain(inout Payload payload, uniform uint recordValue)
            { payload.value = recordValue; }
            """;
        const string descriptorSource = """
            struct Payload { uint value; };
            [shader("raygeneration")] void rayGenerationMain()
            { Payload payload = { 0 }; CallShader(0, payload); }
            [shader("callable")]
            void callableMain(inout Payload payload,
                uniform RWStructuredBuffer<uint> outputValues,
                uniform SamplerState localSampler)
            { outputValues[0] = payload.value; }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("rayGenerationMain", SlangStage.RayGeneration),
            new("callableMain", SlangStage.Callable),
        ];
        using D3D12TestShaderProgram ordinary = D3D12TestShaderProgram.Compile(
            "dxr_local_ordinary_codegen_limit", ordinarySource, entries, "lib_6_5");
        using D3D12TestShaderProgram descriptors = D3D12TestShaderProgram.Compile(
            "dxr_local_descriptor_codegen_limit", descriptorSource, entries, "lib_6_5");
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline ordinaryPipeline = backend.CreateRayTracingPipeline(device,
            new RayTracingPipelineDesc(ordinary.Program, [ordinary.GetEntryPoint(0)], [],
                [ordinary.GetEntryPoint(1)], [], 1, 4, 8));
        using Pipeline descriptorPipeline = backend.CreateRayTracingPipeline(device,
            new RayTracingPipelineDesc(descriptors.Program, [descriptors.GetEntryPoint(0)], [],
                [descriptors.GetEntryPoint(1)], [], 1, 4, 8));
    }

    [Fact]
    public void Empty_local_records_require_exact_raw_layouts_and_execute_on_WARP()
    {
        const string source = """
            RWStructuredBuffer<uint> outputValues;
            [shader("raygeneration")]
            void rayGenerationA()
            {
                outputValues[0] = 11;
            }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("rayGenerationA", SlangStage.RayGeneration),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "dxr_local_ordinary_records", source, entries, "sm_6_5");
        ShaderEntryPoint rayGenerationA = shader.GetEntryPoint(0);
        ShaderVariableLayout globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? ShaderVariableLayout.Null;
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateRayTracingPipeline(device,
            new RayTracingPipelineDesc(shader.Program, [rayGenerationA], [],
                [], [], 1, 4, 8));
        using RayTracingShaderTable table = backend.CreateRayTracingShaderTable(device,
            new RayTracingShaderTableDesc(pipeline, 1, 0, 0, 0, 32));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(8, BufferUsages.CopyDestination), MemoryType.Readback);
        RayTracingShaderRecord[] rayGenerationRecords =
        [
            RayTracingShaderRecord.Entry(rayGenerationA, 0, 1),
        ];
        RayTracingLocalParameterBlock[] validLocalBlocks =
        [
            new(rayGenerationA.VarLayout, 0, 0, 0, 0),
        ];
        using CommandContext context = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(4, 0, 16));
        backend.SetPipeline(context, pipeline);
        Assert.Throws<ArgumentException>(() => backend.UpdateRayTracingShaderTable(context, table,
            new RayTracingShaderTableUpdate(
                [RayTracingShaderRecord.Entry(rayGenerationA, 0, 1)],
                [], [], [],
                [new RayTracingLocalParameterBlock(ShaderVariableLayout.Null, 0, 0, 0, 0)],
                [], [])));
        Assert.Throws<ArgumentException>(() => backend.UpdateRayTracingShaderTable(context, table,
            new RayTracingShaderTableUpdate(
                [RayTracingShaderRecord.Entry(rayGenerationA, 0, 1)],
                [], [], [],
                [new RayTracingLocalParameterBlock(globals, 0, 0, 0, 0)],
                [], [])));
        Assert.Throws<ArgumentException>(() => backend.UpdateRayTracingShaderTable(context, table,
            new RayTracingShaderTableUpdate(
                [RayTracingShaderRecord.Entry(rayGenerationA, 0, 1)],
                [], [], [],
                [new RayTracingLocalParameterBlock(rayGenerationA.VarLayout, 0, 0, 0, 4)],
                [], new byte[4])));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None,
            PipelineSync.RayTracing, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        backend.SetTransientParameterBindings(context,
            new ParameterBlockBindings(globals, [ResourceBinding.WritableBuffer(outputUav)], []));
        backend.UpdateRayTracingShaderTable(context, table,
            new RayTracingShaderTableUpdate(
                rayGenerationRecords,
                [], [], [],
                validLocalBlocks,
                [], []));
        backend.DispatchRays(context, new DispatchRaysDesc(table, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.RayTracing,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 8));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(backend, backend.GetQueue(device, QueueType.Compute), commands);
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, new BufferRange(0, 8));
        mapped.Invalidate(new BufferRange(0, 8));
        Assert.Equal([11u, 0u], System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            mapped.Bytes).ToArray());
    }

    [Fact]
    public void Hit_group_members_with_different_raw_layout_identities_materialize_as_distinct_blocks()
    {
        const string source = """
            struct Payload { uint value; };
            [shader("raygeneration")] void rayGenerationMain() { }
            [shader("closesthit")]
            void closestMain(inout Payload payload, BuiltInTriangleIntersectionAttributes attributes) { }
            [shader("anyhit")]
            void anyMain(inout Payload payload, BuiltInTriangleIntersectionAttributes attributes) { }
            """;
        D3D12TestShaderEntry[] entries =
        [
            new("rayGenerationMain", SlangStage.RayGeneration),
            new("closestMain", SlangStage.ClosestHit),
            new("anyMain", SlangStage.AnyHit),
        ];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "dxr_hit_group_layout_identity", source, entries, "sm_6_5");
        Assert.NotEqual(shader.GetEntryPoint(1).VarLayout, shader.GetEntryPoint(2).VarLayout);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        RayTracingHitGroup[] hitGroups =
        [
            new("hitGroup", shader.GetEntryPoint(1), shader.GetEntryPoint(2),
                ShaderEntryPoint.Null),
        ];
        using Pipeline pipeline = backend.CreateRayTracingPipeline(device,
            new RayTracingPipelineDesc(shader.Program, [shader.GetEntryPoint(0)], [], [],
                hitGroups, 1, 4, 8));
        using RayTracingShaderTable table = backend.CreateRayTracingShaderTable(
            device,
            new RayTracingShaderTableDesc(pipeline, 1, 0, 1, 0, 64));
        RayTracingLocalParameterBlock[] blocks =
        [
            new(shader.GetEntryPoint(0).VarLayout, 0, 0, 0, 0),
            new(shader.GetEntryPoint(1).VarLayout, 0, 0, 0, 0),
            new(shader.GetEntryPoint(2).VarLayout, 0, 0, 0, 0),
        ];
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.UpdateRayTracingShaderTable(
            context,
            table,
            new RayTracingShaderTableUpdate(
                [RayTracingShaderRecord.Entry(shader.GetEntryPoint(0), 0, 1)],
                [],
                [RayTracingShaderRecord.HitGroup("hitGroup", 1, 2)],
                [],
                blocks,
                [],
                []));
        backend.DispatchRays(context, new DispatchRaysDesc(table, 1));
        using RecordedCommands recorded = backend.End(context);
    }


    private static ValidationLayer CreateValidatedBackend()
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        return new ValidationLayer(
            D3D12GraphicsBackend.Create(new D3D12BackendOptions(validation)));
    }



    [Fact]
    public void Acceleration_structure_build_update_clone_compact_serialize_and_deserialize_run_on_WARP()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        // D3D12 guarantees zero when ALLOW_UPDATE is absent, but does not require a non-zero
        // update scratch requirement when ALLOW_UPDATE is present. WARP can legitimately return 0.
        Assert.Equal(0UL, buildInfo.UpdateScratchSize % buildInfo.UpdateScratchAlignment);

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

    [Fact]
    public void Later_invalid_DXR_export_rolls_back_pre_native_construction_and_device_remains_usable()
    {
        const string source = """
            [shader("raygeneration")]
            void rayGenerationMain() { }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "dxr_pipeline_ownership_failure",
            source,
            [new D3D12TestShaderEntry("rayGenerationMain", SlangStage.RayGeneration)]);
        ShaderEntryPoint rayGeneration = shader.GetEntryPoint(0);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Throws<ArgumentException>(() => backend.CreateRayTracingPipeline(
            device,
            new RayTracingPipelineDesc(
                shader.Program,
                [rayGeneration],
                [rayGeneration],
                [],
                [],
                1,
                0,
                8)));

        using Pipeline pipeline = backend.CreateRayTracingPipeline(
            device,
            new RayTracingPipelineDesc(
                shader.Program,
                [rayGeneration],
                [],
                [],
                [],
                1,
                0,
                8));
        using RayTracingShaderTable table = backend.CreateRayTracingShaderTable(
            device,
            new RayTracingShaderTableDesc(pipeline, 1, 0, 0, 0, 32));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.SetPipeline(context, pipeline);
        backend.UpdateRayTracingShaderTable(
            context,
            table,
            new RayTracingShaderTableUpdate(
                [RayTracingShaderRecord.Entry(rayGeneration, 0, 1)],
                [], [], [],
                [new RayTracingLocalParameterBlock(rayGeneration.VarLayout, 0, 0, 0, 0)],
                [], []));
        backend.DispatchRays(context, new DispatchRaysDesc(table, 1));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = Submit(
            backend,
            backend.GetQueue(device, QueueType.Compute),
            commands);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }







    [Fact]
    public void Stable_BLAS_build_encoding_reuses_grown_command_slot_geometry_scratch_without_allocating()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability));
        Assert.NotNull(capability);

        const ulong vertexBytes = 9 * sizeof(float);
        using Buffer vertices = backend.CreateBuffer(
            device,
            new BufferDesc(vertexBytes, BufferUsages.AccelerationStructureInput),
            MemoryType.Upload);
        AccelerationStructureGeometry geometry = new(
            AccelerationStructureGeometryType.Triangles,
            new BufferRegion(vertices, new BufferRange(0, vertexBytes)),
            Format.R32G32B32Float,
            3 * sizeof(float),
            3,
            default,
            default,
            AccelerationStructureGeometryOptions.Opaque);
        AccelerationStructureGeometry[] oneGeometry = [geometry];
        AccelerationStructureGeometry[] grownGeometrySet =
            [geometry, geometry, geometry, geometry, geometry, geometry, geometry, geometry, geometry];
        AccelerationStructureBuildInfo buildInfo = backend.GetAccelerationStructureBuildInfo(
            device,
            AccelerationStructureType.BottomLevel,
            AccelerationStructureBuildOptions.None,
            grownGeometrySet);
        ulong resultSize = AlignUp(buildInfo.ResultSize, buildInfo.ResultAlignment);
        ulong scratchSize = AlignUp(buildInfo.BuildScratchSize, buildInfo.BuildScratchAlignment);
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
        using Buffer instances = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.AccelerationStructureInput),
            MemoryType.Upload);
        AccelerationStructureGeometry[] instanceGeometry =
        [
            new(
                AccelerationStructureGeometryType.Instances,
                new BufferRegion(instances, BufferRange.Whole),
                default,
                0,
                1,
                default,
                default,
                AccelerationStructureGeometryOptions.None),
        ];
        AccelerationStructureBuildInfo topLevelBuildInfo = backend.GetAccelerationStructureBuildInfo(
            device,
            AccelerationStructureType.TopLevel,
            AccelerationStructureBuildOptions.None,
            instanceGeometry);
        using Buffer topLevelStorage = CreateAccelerationStructureStorage(
            backend,
            device,
            AlignUp(topLevelBuildInfo.ResultSize, topLevelBuildInfo.ResultAlignment));
        using AccelerationStructure topLevelStructure = backend.CreateAccelerationStructure(
            device,
            topLevelStorage,
            BufferRange.Whole,
            AccelerationStructureType.TopLevel);
        using Buffer topLevelScratch = backend.CreateBuffer(
            device,
            new BufferDesc(
                AlignUp(topLevelBuildInfo.BuildScratchSize, topLevelBuildInfo.BuildScratchAlignment),
                BufferUsages.ShaderWrite),
            MemoryType.DeviceLocal);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        backend.Begin(context);
        backend.BuildAccelerationStructure(
            context,
            new AccelerationStructureBuildDesc(
                AccelerationStructureType.BottomLevel,
                AccelerationStructureBuildOptions.None,
                oneGeometry,
                structure,
                scratch,
                BufferRange.Whole));
        using (RecordedCommands initialCapacity = backend.End(context))
        {
        }

        backend.Begin(context);
        AccelerationStructureGeometry[] invalidGrownGeometrySet =
            [geometry, geometry, geometry, geometry, geometry, geometry, geometry, geometry, default];
        Assert.Throws<ArgumentException>(() => backend.BuildAccelerationStructure(
            context,
            new AccelerationStructureBuildDesc(
                AccelerationStructureType.BottomLevel,
                AccelerationStructureBuildOptions.None,
                invalidGrownGeometrySet,
                structure,
                scratch,
                BufferRange.Whole)));
        backend.Discard(context);

        backend.Begin(context);
        backend.BuildAccelerationStructure(
            context,
            new AccelerationStructureBuildDesc(
                AccelerationStructureType.BottomLevel,
                AccelerationStructureBuildOptions.None,
                grownGeometrySet,
                structure,
                scratch,
                BufferRange.Whole));
        backend.Discard(context);

        backend.Begin(context);
        AccelerationStructureBuildDesc stableBuild = new(
            AccelerationStructureType.BottomLevel,
            AccelerationStructureBuildOptions.None,
            grownGeometrySet,
            structure,
            scratch,
            BufferRange.Whole);
        backend.BuildAccelerationStructure(context, stableBuild);
        AccelerationStructureBuildDesc stableTopLevelBuild = new(
            AccelerationStructureType.TopLevel,
            AccelerationStructureBuildOptions.None,
            instanceGeometry,
            topLevelStructure,
            topLevelScratch,
            BufferRange.Whole);
        backend.BuildAccelerationStructure(context, stableTopLevelBuild);
        for (int iteration = 0; iteration < 32; iteration++)
        {
            backend.BuildAccelerationStructure(context, stableBuild);
            backend.BuildAccelerationStructure(context, stableTopLevelBuild);
        }
        using RecordedCommands measured = backend.End(context);

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
