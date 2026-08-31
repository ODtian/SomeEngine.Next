using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpDescriptorTests
{
    [Fact]
    public void Recorded_commands_retain_persistent_data_after_wrapper_disposal()
    {
        const string source = """
            RWStructuredBuffer<uint> output;
            uint value;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                output[id.x] = value;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "persistent_recorded_lifetime",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)],
            "sm_6_2");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        int ordinarySize = checked((int)GetDataLayout(layout).GetSize(
            SlangParameterCategory.Uniform));

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        ResourceBinding[] resources = [ResourceBinding.WritableBuffer(outputUav)];
        PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device,
            pipeline,
            new ParameterBlockBindings(layout, resources, CreateOrdinaryData(ordinarySize, 73)));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.Barrier(
            context,
            new BufferBarrier(
                output,
                PipelineSync.None,
                PipelineSync.ComputeShading,
                ResourceAccess.NoAccess,
                ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, persistent);
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(
            context,
            new BufferBarrier(
                output,
                PipelineSync.ComputeShading,
                PipelineSync.Copy,
                ResourceAccess.UnorderedAccess,
                ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));

        using RecordedCommands recorded = backend.End(context);
        persistent.Dispose();
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Compute),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        using MappedBuffer mapped = backend.Map(
            readback,
            MapType.Read,
            new BufferRange(0, 4));
        mapped.Invalidate(new BufferRange(0, 4));
        Assert.Equal(73u, MemoryMarshal.Cast<byte, uint>(mapped.Bytes)[0]);
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Persistent_binding_generations_support_concurrent_recording_and_updates()
    {
        const string source = """
            RWStructuredBuffer<uint> output;
            uint value;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                output[id.x] = value;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "persistent_concurrent_generation_capture",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)],
            "sm_6_2");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        int ordinarySize = checked((int)GetDataLayout(layout).GetSize(
            SlangParameterCategory.Uniform));

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, Format.R32UInt));
        ResourceBinding[] resources = [ResourceBinding.WritableBuffer(outputUav)];
        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device,
            pipeline,
            new ParameterBlockBindings(layout, resources, CreateOrdinaryData(ordinarySize, 0)));
        const int recorderCount = 4;
        const int iterations = 256;
        CommandContext[] contexts = Enumerable.Range(0, recorderCount)
            .Select(index => backend.CreateCommandContext(
                device,
                new CommandContextDesc(
                    QueueType.Compute,
                    0,
                    1,
                    Label: $"persistent recorder {index}")))
            .ToArray();
        try
        {
            using var start = new ManualResetEventSlim(false);
            var failures = new ConcurrentQueue<Exception>();
            Thread[] recorders = contexts.Select(context => new Thread(() =>
            {
                start.Wait();
                try
                {
                    for (int iteration = 0; iteration < iterations; iteration++)
                    {
                        backend.Begin(context, new CommandRecordingDesc(4, 0, 4));
                        try
                        {
                            backend.SetPipeline(context, pipeline);
                            backend.SetPersistentParameterBindings(context, persistent);
                            backend.Dispatch(context, new DispatchArguments(1, 1, 1));
                        }
                        finally
                        {
                            backend.Discard(context);
                        }
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            })
            {
                IsBackground = true,
            }).ToArray();
            Thread updater = new(() =>
            {
                start.Wait();
                try
                {
                    for (uint value = 1; value <= iterations; value++)
                    {
                        backend.UpdatePersistentParameterBindings(
                            persistent,
                            new ParameterBlockBindings(
                                layout,
                                resources,
                                CreateOrdinaryData(ordinarySize, value)));
                    }
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            })
            {
                IsBackground = true,
            };

            foreach (Thread recorder in recorders)
                recorder.Start();
            updater.Start();
            start.Set();
            foreach (Thread recorder in recorders)
                Assert.True(recorder.Join(TimeSpan.FromSeconds(30)));
            Assert.True(updater.Join(TimeSpan.FromSeconds(30)));
            Assert.Empty(failures);
        }
        finally
        {
            foreach (CommandContext context in contexts)
                context.Dispose();
        }
    }

    [Fact]
    public void Root_constant_buffer_recording_does_not_capture_descriptor_generation_or_arena()
    {
        const string source = """
            float4 tint;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                if (tint.x == id.x) { }
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "root_cbv_single_descriptor_generation",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)],
            "sm_6_2");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        Assert.Equal((nint)0, GetDataLayout(layout).BindingRangeCount);

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using PersistentParameterBindings persistent =
            backend.CreatePersistentParameterBindings(
                device,
                pipeline,
                new ParameterBlockBindings(layout, [], new byte[16]));
        object publisher = D3D12PrivateState.Invoke(
            device,
            "GetDescriptorPublisher",
            0u)!;
        object generation = D3D12PrivateState.GetField(publisher, "_current")
            .GetValue(publisher)!;
        FieldInfo references = D3D12PrivateState.GetField(generation, "_references");
        Assert.Equal(1, references.GetValue(generation));

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);
        object slot = D3D12PrivateState.GetField(context, "_recording")
            .GetValue(context)!;
        Assert.Null(
            D3D12PrivateState.GetField(slot, "_descriptorGeneration").GetValue(slot));
        Assert.Equal(1, references.GetValue(generation));
        Assert.False((bool)D3D12PrivateState.GetField(slot, "_descriptorArenaReady")
            .GetValue(slot)!);

        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, persistent);
        Assert.Null(
            D3D12PrivateState.GetField(slot, "_descriptorGeneration").GetValue(slot));
        Assert.Equal(1, references.GetValue(generation));
        Assert.False((bool)D3D12PrivateState.GetField(slot, "_descriptorArenaReady")
            .GetValue(slot)!);

        RecordedCommands commands = backend.End(context);
        try
        {
            Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);
            Assert.Equal(1, references.GetValue(generation));
        }
        finally
        {
            commands.Dispose();
        }
        Assert.Equal(1, references.GetValue(generation));
    }

    [Fact]
    public void Transient_parameter_bindings_materialize_every_typed_null_descriptor()
    {
        const string source = """
            StructuredBuffer<uint> readBuffer;
            RWStructuredBuffer<uint> writeBuffer;
            Texture2D<float4> sampledTexture;
            RWTexture2D<float4> storageTexture;
            RaytracingAccelerationStructure scene;
            SamplerState samplerState;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                uint value = readBuffer[id.x];
                float4 sampled = sampledTexture.SampleLevel(samplerState, float2(0.5), 0);
                storageTexture[id.xy] = sampled;
                writeBuffer[id.x] = value;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "transient_typed_null_descriptors",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)],
            "sm_6_2");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection reflected = GetDataLayout(layout);
        (SlangBindingType Type, uint Register, uint Space)[] locations = Enumerable.Range(
                0,
                checked((int)reflected.BindingRangeCount))
            .Select(index =>
            {
                nint rangeIndex = index;
                Assert.Equal(
                    (nint)1,
                    reflected.GetBindingRangeDescriptorRangeCount(rangeIndex));
                nint setIndex =
                    reflected.GetBindingRangeDescriptorSetIndex(rangeIndex);
                nint descriptorRangeIndex =
                    reflected.GetBindingRangeFirstDescriptorRangeIndex(rangeIndex);
                return (
                    reflected.GetBindingRangeType(rangeIndex),
                    checked((uint)reflected.GetDescriptorSetDescriptorRangeIndexOffset(
                        setIndex,
                        descriptorRangeIndex)),
                    checked((uint)reflected.GetDescriptorSetSpaceOffset(setIndex)));
            })
            .ToArray();
        Assert.Equal(
            new (SlangBindingType Type, uint Register, uint Space)[]
            {
                (SlangBindingType.RawBuffer, 0, 0),
                (SlangBindingType.MutableRawBuffer, 0, 0),
                (SlangBindingType.Texture, 1, 0),
                (SlangBindingType.MutableTexture, 1, 0),
                (SlangBindingType.RayTracingAccelerationStructure, 2, 0),
                (SlangBindingType.Sampler, 0, 0),
            },
            locations);
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Sampler sampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Nearest,
                FilterType.Nearest,
                FilterType.Nearest,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));
        ResourceBinding[] bindings =
        [
            ResourceBinding.Null(ResourceBindingType.BufferSrv),
            ResourceBinding.Null(ResourceBindingType.BufferUav),
            ResourceBinding.Null(ResourceBindingType.TextureSrv),
            ResourceBinding.Null(ResourceBindingType.TextureUav),
            ResourceBinding.Null(ResourceBindingType.AccelerationStructure),
            ResourceBinding.SampledWith(sampler),
        ];
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        backend.Begin(context, new CommandRecordingDesc(8, 2, 8));
        object slot = D3D12PrivateState.GetField(context, "_recording")
            .GetValue(context)!;
        backend.SetPipeline(context, pipeline);
        Assert.Null(
            D3D12PrivateState.GetField(slot, "_descriptorGeneration").GetValue(slot));
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, bindings, []));
        Assert.True((bool)D3D12PrivateState.GetField(slot, "_descriptorArenaReady")
            .GetValue(slot)!);
        Assert.False((bool)D3D12PrivateState.GetField(
            slot,
            "_resourceArenaContainsGeneration").GetValue(slot)!);
        Assert.False((bool)D3D12PrivateState.GetField(
            slot,
            "_samplerArenaContainsGeneration").GetValue(slot)!);
        Assert.Null(
            D3D12PrivateState.GetField(slot, "_descriptorGeneration").GetValue(slot));
        backend.Discard(context);
    }

    private static TypeLayoutReflection GetDataLayout(
        VariableLayoutReflection layout)
    {
        TypeLayoutReflection result = layout.TypeLayout.UnwrapArray();
        if (result.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            result = result.ElementTypeLayout.UnwrapArray();
        return result;
    }

    [Fact]
    public void Initial_descriptor_capacity_exhaustion_is_atomic_and_context_remains_reusable()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        GraphicsException failure = Assert.Throws<GraphicsException>(() => backend.Begin(
            context,
            new CommandRecordingDesc(
                InitialResourceDescriptorCapacity: 64,
                InitialSamplerDescriptorCapacity:
                    checked(device.Capabilities.Limits.SamplerDescriptorCapacity + 1))));
        Assert.Equal(GraphicsError.OutOfDescriptors, failure.Error);

        backend.Begin(
            context,
            new CommandRecordingDesc(
                InitialResourceDescriptorCapacity: 64,
                InitialSamplerDescriptorCapacity: 64));
        using RecordedCommands recorded = backend.End(context);
    }

    [Fact]
    public void Resource_typed_null_descriptors_publish_and_sampler_null_is_rejected_on_write()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        ResourceBindingType[] types =
        [
            ResourceBindingType.ConstantBuffer,
            ResourceBindingType.BufferSrv,
            ResourceBindingType.BufferUav,
            ResourceBindingType.TextureSrv,
            ResourceBindingType.TextureUav,
            ResourceBindingType.AccelerationStructure,
        ];
        using DescriptorTable resources = backend.CreateDescriptorTable(device, types);
        using DescriptorTable samplers = backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.Sampler]);
        Assert.Equal(DescriptorTableType.Resource, resources.Type);
        Assert.Equal(DescriptorTableType.Sampler, samplers.Type);
        Assert.Equal(types, resources.Slots.ToArray().Select(static slot => slot.Type).ToArray());
        Assert.Equal(ResourceBindingType.Sampler, samplers.GetSlotType(0));

        uint resourceFirst = backend.GetDescriptorIndex(resources, 0).Value;
        uint samplerFirst = backend.GetDescriptorIndex(samplers, 0).Value;
        AssertDescriptorRecordTypes(resources, "_pendingResources", resourceFirst, types);
        AssertDescriptorRecordTypes(
            samplers,
            "_pendingSamplers",
            samplerFirst,
            [ResourceBindingType.Sampler]);
        Assert.Throws<InvalidOperationException>(() =>
            backend.PublishDescriptors(device));

        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                1_024,
                BufferUsages.Constant | BufferUsages.ShaderRead | BufferUsages.ShaderWrite),
            MemoryType.DeviceLocal);
        using BufferCbv cbv = backend.CreateBufferCbv(
            device,
            new BufferCbvDesc(buffer, new BufferRange(0, 256)));
        using BufferSrv bufferSrv = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));
        using BufferUav bufferUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(buffer, BufferRange.Whole, Format.R32UInt));
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                4,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.Storage));
        TextureSubresourceRange textureRange = new(
            0,
            1,
            0,
            1,
            TextureAspects.Color);
        using TextureSrv textureSrv = backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                texture,
                textureRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using TextureUav textureUav = backend.CreateTextureUav(
            device,
            new TextureUavDesc(
                texture,
                textureRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using Buffer accelerationStorage = backend.CreateBuffer(
            device,
            new BufferDesc(1_024, BufferUsages.AccelerationStructure),
            MemoryType.DeviceLocal);
        using AccelerationStructure accelerationStructure = backend.CreateAccelerationStructure(
            device,
            accelerationStorage,
            BufferRange.Whole,
            AccelerationStructureType.BottomLevel);
        using AccelerationStructureSrv accelerationStructureSrv =
            backend.CreateAccelerationStructureSrv(
                device,
                new AccelerationStructureSrvDesc(accelerationStructure));
        using Sampler sampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Nearest,
                FilterType.Nearest,
                FilterType.Nearest,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));

        ResourceBinding[] actual =
        [
            ResourceBinding.ConstantBuffer(cbv),
            ResourceBinding.ReadOnlyBuffer(bufferSrv),
            ResourceBinding.WritableBuffer(bufferUav),
            ResourceBinding.SampledTexture(textureSrv),
            ResourceBinding.StorageTexture(textureUav),
            ResourceBinding.AccelerationStructure(accelerationStructureSrv),
        ];
        for (int slot = 0; slot < actual.Length; slot++)
            backend.WriteDescriptor(resources, checked((uint)slot), actual[slot]);
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(sampler));
        backend.PublishDescriptors(device);
        AssertDescriptorRecordTypes(
            resources,
            "_resources",
            resourceFirst,
            types,
            hasOwner: true);
        AssertDescriptorRecordTypes(
            samplers,
            "_samplers",
            samplerFirst,
            [ResourceBindingType.Sampler],
            hasOwner: true);

        for (int slot = 0; slot < types.Length; slot++)
        {
            backend.WriteDescriptor(
                resources,
                checked((uint)slot),
                ResourceBinding.Null(types[slot]));
        }
        Assert.Throws<ArgumentException>(() => backend.WriteDescriptor(
            samplers,
            0,
            ResourceBinding.Null(ResourceBindingType.Sampler)));
        backend.PublishDescriptors(device);
        AssertDescriptorRecordTypes(resources, "_resources", resourceFirst, types);
        AssertDescriptorRecordTypes(
            samplers,
            "_samplers",
            samplerFirst,
            [ResourceBindingType.Sampler],
            hasOwner: true);

        for (int slot = 0; slot < actual.Length; slot++)
            backend.WriteDescriptor(resources, checked((uint)slot), actual[slot]);
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(sampler));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context, default);
        using RecordedCommands heldGeneration = backend.End(context);

        resources.Dispose();
        samplers.Dispose();
        backend.PublishDescriptors(device);
        AssertDescriptorRecordTypes(resources, "_resources", resourceFirst, types);
        AssertDescriptorRecordTypes(
            samplers,
            "_samplers",
            samplerFirst,
            [ResourceBindingType.Sampler]);
    }

    [Fact]
    public void Unpublished_descriptor_table_disposal_immediately_reuses_index()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead));
        BufferSrvDesc description = new(buffer, BufferRange.Whole, Format.R32UInt);
        using BufferSrv view = backend.CreateBufferSrv(device, description);
        DescriptorSlotDesc slot = new(ResourceBindingType.BufferSrv, Format.R32UInt);

        DescriptorTable first = backend.CreateSingleDescriptorTable(
            device,
            slot,
            ResourceBinding.ReadOnlyBuffer(view),
            out DescriptorIndex firstIndex);
        first.Dispose();
        using DescriptorTable replacement = backend.CreateSingleDescriptorTable(
            device,
            slot,
            ResourceBinding.ReadOnlyBuffer(view),
            out DescriptorIndex replacementIndex);

        Assert.Equal(firstIndex.Value, replacementIndex.Value);
        Assert.NotEqual(firstIndex, replacementIndex);
    }

    [Fact]
    public void Published_descriptor_table_numeric_index_reuses_without_reviving_identity()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        BufferSrvDesc description = new(buffer, BufferRange.Whole, Format.R32UInt);
        using BufferSrv view = backend.CreateBufferSrv(device, description);
        DescriptorSlotDesc slot = new(ResourceBindingType.BufferSrv, Format.R32UInt);

        DescriptorTable first = backend.CreateSingleDescriptorTable(
            device,
            slot,
            ResourceBinding.ReadOnlyBuffer(view),
            out DescriptorIndex retiredIndex);
        backend.PublishDescriptors(device);

        backend.Begin(context, default);
        RecordedCommands heldGeneration = backend.End(context);
        first.Dispose();
        backend.PublishDescriptors(device);

        using DescriptorTable whileHeld = backend.CreateSingleDescriptorTable(
            device,
            slot,
            ResourceBinding.ReadOnlyBuffer(view),
            out DescriptorIndex replacementIndex);
        Assert.Equal(retiredIndex.Value, replacementIndex.Value);
        Assert.NotEqual(retiredIndex, replacementIndex);

        heldGeneration.Dispose();
    }

    [Fact]
    public void Descriptor_table_contiguous_range_reuses_values_without_reusing_identity()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        ResourceBindingType[] slotTypes =
        [
            ResourceBindingType.ConstantBuffer,
            ResourceBindingType.BufferSrv,
            ResourceBindingType.TextureSrv,
        ];
        DescriptorTable first = backend.CreateDescriptorTable(device, slotTypes);
        DescriptorIndex firstIndex = backend.GetDescriptorIndex(first, 0);
        Assert.Equal(firstIndex.Value + 1, backend.GetDescriptorIndex(first, 1).Value);
        Assert.Equal(firstIndex.Value + 2, backend.GetDescriptorIndex(first, 2).Value);
        backend.PublishDescriptors(device);

        backend.Begin(context, default);
        RecordedCommands heldGeneration = backend.End(context);
        first.Dispose();
        backend.PublishDescriptors(device);

        using DescriptorTable whileHeld = backend.CreateDescriptorTable(device, slotTypes);
        DescriptorIndex reused = backend.GetDescriptorIndex(whileHeld, 0);
        Assert.Equal(firstIndex.Value, reused.Value);
        Assert.NotEqual(firstIndex, reused);

        heldGeneration.Dispose();
    }

    [Fact]
    public void Invalid_table_type_and_slot_writes_preserve_pending_publication()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using DescriptorTable resources = backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.TextureSrv]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.Sampler]);

        Assert.Throws<ArgumentException>(() => backend.WriteDescriptor(
            resources,
            0,
            ResourceBinding.Null(ResourceBindingType.Sampler)));
        Assert.Throws<ArgumentException>(() => backend.WriteDescriptor(
            samplers,
            0,
            ResourceBinding.Null(ResourceBindingType.BufferSrv)));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.WriteDescriptor(
            resources,
            1,
            ResourceBinding.Null(ResourceBindingType.BufferSrv)));

        backend.WriteDescriptor(
            resources,
            0,
            ResourceBinding.Null(ResourceBindingType.TextureSrv));
        using Sampler sampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Nearest,
                FilterType.Nearest,
                FilterType.Nearest,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(sampler));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
    }

    [Fact]
    public void First_native_descriptor_return_is_allocation_free_and_reuses_its_slot()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);
        BufferSrv? first = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));
        NativeDescriptorInfo firstStorage = GetNativeDescriptorInfo(first);
        BufferSrv?[] views =
            new BufferSrv?[checked((int)firstStorage.PageCapacity)];
        views[0] = first;
        BufferSrv? replacement = null;
        try
        {
            for (int index = 1; index < views.Length; index++)
            {
                views[index] = backend.CreateBufferSrv(
                    device,
                    new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));
            }
            NativeDescriptorInfo lastStorage =
                GetNativeDescriptorInfo(views[^1]!);
            Assert.Same(firstStorage.Page, lastStorage.Page);
            Assert.Equal(firstStorage.PageCapacity - 1, lastStorage.Slot);

            _ = GC.GetAllocatedBytesForCurrentThread();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            first.Dispose();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            views[0] = null;
            first = null;
            Assert.Equal(0, allocated);
            Assert.Equal(
                1,
                GetRequiredField(firstStorage.Page, "_freeCount")
                    .GetValue(firstStorage.Page));
            MethodInfo returnSlot = firstStorage.Page.GetType().GetMethod(
                "Return",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = returnSlot.Invoke(firstStorage.Page, [firstStorage.Slot]);
            _ = returnSlot.Invoke(firstStorage.Page, [firstStorage.PageCapacity]);
            Assert.Equal(
                1,
                GetRequiredField(firstStorage.Page, "_freeCount")
                    .GetValue(firstStorage.Page));

            replacement = backend.CreateBufferSrv(
                device,
                new BufferSrvDesc(buffer, BufferRange.Whole, Format.R32UInt));
            NativeDescriptorInfo replacementStorage =
                GetNativeDescriptorInfo(replacement);
            Assert.Same(firstStorage.Page, replacementStorage.Page);
            Assert.Equal(firstStorage.Slot, replacementStorage.Slot);
            Assert.Equal(
                0,
                GetRequiredField(firstStorage.Page, "_freeCount")
                    .GetValue(firstStorage.Page));

            replacement.Dispose();
            replacement = null;
            for (int index = 1; index < views.Length; index++)
            {
                views[index]!.Dispose();
                views[index] = null;
            }
            buffer.Dispose();
            Assert.True(buffer.IsDisposed);
        }
        finally
        {
            first?.Dispose();
            replacement?.Dispose();
            foreach (BufferSrv? view in views)
                view?.Dispose();
        }
    }

    [Fact]
    public void Persistent_replacement_failure_preserves_current_data_and_retry_executes()
    {
        const string source = """
            RWStructuredBuffer<uint> firstOutput;
            RWStructuredBuffer<uint> secondOutput;
            uint ordinaryValues[80];

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                firstOutput[id.x] = ordinaryValues[0];
                secondOutput[id.x] = ordinaryValues[0] + 1;
            }
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "persistent_descriptor_transaction_faults",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)],
            "sm_6_2");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        int ordinarySize = checked((int)GetDataLayout(layout).GetSize(
            SlangParameterCategory.Uniform));
        Assert.True(ordinarySize > 256);

        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using Buffer firstOutput = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using Buffer secondOutput = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource),
            MemoryType.DeviceLocal);
        using BufferUav firstUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(firstOutput, BufferRange.Whole, Format.R32UInt));
        using BufferUav secondUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(secondOutput, BufferRange.Whole, Format.R32UInt));
        Buffer failedBuffer = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite),
            MemoryType.DeviceLocal);
        BufferUav failedUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(failedBuffer, BufferRange.Whole, Format.R32UInt));
        object failedDescriptor = GetRequiredProperty(failedUav, "NativeDescriptor");
        failedUav.Dispose();
        failedBuffer.Dispose();
        Assert.Equal(0, GetRequiredField(failedDescriptor, "_references").GetValue(failedDescriptor));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(8, BufferUsages.CopyDestination),
            MemoryType.Readback);
        ResourceBinding[] resources =
        [
            ResourceBinding.WritableBuffer(firstUav),
            ResourceBinding.WritableBuffer(secondUav),
        ];
        byte[] initialOrdinary = CreateOrdinaryData(ordinarySize, 3);
        byte[] updatedOrdinary = CreateOrdinaryData(ordinarySize, 7);
        using PersistentParameterBindings persistent =
            backend.CreatePersistentParameterBindings(
                device,
                pipeline,
                new ParameterBlockBindings(layout, resources, initialOrdinary));

        PersistentPrivateInfo persistentBefore =
            GetPersistentPrivateInfo(persistent);
        int firstDescriptorReferences = GetDescriptorLeaseReferenceCount(firstUav);
        ResourceBinding[] invalidResources =
        [
            ResourceBinding.WritableBuffer(firstUav),
            ResourceBinding.WritableBuffer(failedUav),
        ];
        Assert.Throws<ObjectDisposedException>(() =>
            backend.UpdatePersistentParameterBindings(
                persistent,
                new ParameterBlockBindings(layout, invalidResources, updatedOrdinary)));
        Assert.Equal(persistentBefore, GetPersistentPrivateInfo(persistent));
        Assert.Equal(firstDescriptorReferences, GetDescriptorLeaseReferenceCount(firstUav));
        Assert.Equal(0, GetRequiredField(failedDescriptor, "_references").GetValue(failedDescriptor));

        ExecutePersistentBinding(
            backend,
            device,
            pipeline,
            persistent,
            firstOutput,
            secondOutput,
            readback,
            [3u, 4u],
            PipelineSync.None,
            ResourceAccess.NoAccess);

        backend.UpdatePersistentParameterBindings(
            persistent,
            new ParameterBlockBindings(layout, resources, updatedOrdinary));
        PersistentPrivateInfo replaced = GetPersistentPrivateInfo(persistent);
        Assert.NotSame(persistentBefore.Current, replaced.Current);
        Assert.Equal(3UL, replaced.NextVersion);

        ExecutePersistentBinding(
            backend,
            device,
            pipeline,
            persistent,
            firstOutput,
            secondOutput,
            readback,
            [7u, 8u],
            PipelineSync.Copy,
            ResourceAccess.CopySource);
    }

    [Fact]
    public void Generation_record_retain_failure_preserves_current_pending_and_retry_succeeds()
    {
        using var backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(8, BufferUsages.ShaderRead),
            MemoryType.DeviceLocal);
        using BufferSrv firstView = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, new BufferRange(0, 4), Format.R32UInt));
        using BufferSrv secondView = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(buffer, new BufferRange(4, 4), Format.R32UInt));
        using DescriptorTable table = backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.BufferSrv, ResourceBindingType.BufferSrv]);
        backend.WriteDescriptor(table, 0, ResourceBinding.ReadOnlyBuffer(firstView));
        backend.WriteDescriptor(table, 1, ResourceBinding.ReadOnlyBuffer(secondView));

        object publisher = GetRequiredProperty(table, "Publisher");
        IDictionary pending = (IDictionary)GetRequiredField(
            publisher,
            "_pendingResources").GetValue(publisher)!;
        uint firstIndex = backend.GetDescriptorIndex(table, 0).Value;
        object firstRecord = pending[firstIndex]!;
        object secondRecord = pending[checked(firstIndex + 1)]!;
        FieldInfo firstReferences = GetRequiredField(firstRecord, "_references");
        FieldInfo secondReferences = GetRequiredField(secondRecord, "_references");
        int firstReferenceCount = (int)firstReferences.GetValue(firstRecord)!;
        int secondReferenceCount = (int)secondReferences.GetValue(secondRecord)!;
        PublisherPrivateInfo beforeInfo = GetPublisherPrivateInfo(table);

        secondReferences.SetValue(secondRecord, int.MaxValue);
        try
        {
            Assert.Throws<OverflowException>(() => backend.PublishDescriptors(device));
            Assert.Equal(beforeInfo, GetPublisherPrivateInfo(table));
            Assert.Equal(firstReferenceCount, firstReferences.GetValue(firstRecord));
            Assert.Equal(int.MaxValue, secondReferences.GetValue(secondRecord));
        }
        finally
        {
            secondReferences.SetValue(secondRecord, secondReferenceCount);
        }

        backend.PublishDescriptors(device);
        PublisherPrivateInfo publishedInfo = GetPublisherPrivateInfo(table);
        Assert.NotSame(beforeInfo.CurrentGeneration, publishedInfo.CurrentGeneration);
        Assert.Equal(0, publishedInfo.PendingResourceCount);
        Assert.Equal(0, publishedInfo.PendingSamplerCount);
        Assert.Equal(0, publishedInfo.PendingBindingCount);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
        QueueCompletion completion = backend.Submit(
            backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    [Fact]
    public void Generation_identity_exhaustion_is_atomic_permanent_and_keeps_current_generation_usable()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using DescriptorTable table = backend.CreateDescriptorTable(
            device,
            [ResourceBindingType.BufferSrv]);
        backend.WriteDescriptor(
            table,
            0,
            ResourceBinding.Null(ResourceBindingType.BufferSrv));

        object publisher = GetRequiredProperty(table, "Publisher");
        FieldInfo nextGeneration = GetRequiredField(publisher, "_nextGeneration");
        FieldInfo currentGeneration = GetRequiredField(publisher, "_current");
        FieldInfo pendingResources = GetRequiredField(publisher, "_pendingResources");
        object currentBefore = currentGeneration.GetValue(publisher)!;
        int pendingBefore = ((IDictionary)pendingResources.GetValue(publisher)!).Count;
        nextGeneration.SetValue(publisher, ulong.MaxValue);

        GraphicsException first = Assert.Throws<GraphicsException>(() =>
            backend.PublishDescriptors(device));
        GraphicsException second = Assert.Throws<GraphicsException>(() =>
            backend.PublishDescriptors(device));
        Assert.Equal(GraphicsError.OutOfDescriptors, first.Error);
        Assert.Equal(GraphicsError.OutOfDescriptors, second.Error);
        Assert.Same(currentBefore, currentGeneration.GetValue(publisher));
        Assert.Equal(
            pendingBefore,
            ((IDictionary)pendingResources.GetValue(publisher)!).Count);
        Assert.Equal(DeviceStatus.Active, device.Status);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
    }

    private static byte[] CreateOrdinaryData(int size, uint value)
    {
        byte[] result = new byte[size];
        Assert.True(BitConverter.TryWriteBytes(result.AsSpan(), value));
        return result;
    }

    private static void ExecutePersistentBinding(
        D3D12Backend backend,
        Device device,
        Pipeline pipeline,
        PersistentParameterBindings persistent,
        Buffer firstOutput,
        Buffer secondOutput,
        Buffer readback,
        uint[] expected,
        PipelineSync beforeSync,
        ResourceAccess beforeAccess)
    {
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 0, 8));
        backend.Barrier(
            context,
            new BufferBarrier(
                firstOutput,
                beforeSync,
                PipelineSync.ComputeShading,
                beforeAccess,
                ResourceAccess.UnorderedAccess));
        backend.Barrier(
            context,
            new BufferBarrier(
                secondOutput,
                beforeSync,
                PipelineSync.ComputeShading,
                beforeAccess,
                ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, persistent);
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(
            context,
            new BufferBarrier(
                firstOutput,
                PipelineSync.ComputeShading,
                PipelineSync.Copy,
                ResourceAccess.UnorderedAccess,
                ResourceAccess.CopySource));
        backend.Barrier(
            context,
            new BufferBarrier(
                secondOutput,
                PipelineSync.ComputeShading,
                PipelineSync.Copy,
                ResourceAccess.UnorderedAccess,
                ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(firstOutput, 0, readback, 0, 4));
        backend.CopyBuffer(context, new BufferCopy(secondOutput, 0, readback, 4, 4));

        QueueCompletion completion;
        using (RecordedCommands recorded = backend.End(context))
        {
            completion = backend.Submit(
                backend.GetQueue(device, QueueType.Compute),
                new QueueSubmitDesc([], [], [recorded], [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        }
        using MappedBuffer mapped = backend.Map(
            readback,
            MapType.Read,
            new BufferRange(0, 8));
        mapped.Invalidate(new BufferRange(0, 8));
        Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
        backend.CollectCompleted(device);
    }

    private static NativeDescriptorInfo GetNativeDescriptorInfo(
        GraphicsObject view)
    {
        object descriptor = GetRequiredProperty(view, "NativeDescriptor");
        object page = GetRequiredField(descriptor, "_page").GetValue(descriptor)!;
        return new NativeDescriptorInfo(
            page,
            (uint)GetRequiredField(descriptor, "_slot").GetValue(descriptor)!,
            (uint)GetRequiredField(page, "_capacity").GetValue(page)!);
    }

    private static int GetDescriptorLeaseReferenceCount(GraphicsObject view)
    {
        object descriptor = GetRequiredProperty(view, "NativeDescriptor");
        return (int)GetRequiredField(descriptor, "_references").GetValue(descriptor)!;
    }

    private static PersistentPrivateInfo GetPersistentPrivateInfo(
        PersistentParameterBindings bindings) =>
        new(
            GetRequiredField(bindings, "_current").GetValue(bindings),
            (ulong)GetRequiredField(bindings, "_nextVersion").GetValue(bindings)!);

    private static PublisherPrivateInfo GetPublisherPrivateInfo(DescriptorTable table)
    {
        object publisher = GetRequiredProperty(table, "Publisher");
        return new PublisherPrivateInfo(
            GetRequiredField(publisher, "_current").GetValue(publisher)!,
            ((IDictionary)GetRequiredField(publisher, "_pendingResources")
                .GetValue(publisher)!).Count,
            ((IDictionary)GetRequiredField(publisher, "_pendingSamplers")
                .GetValue(publisher)!).Count,
            0);
    }

    private readonly record struct NativeDescriptorInfo(
        object Page,
        uint Slot,
        uint PageCapacity);

    private readonly record struct PersistentPrivateInfo(
        object? Current,
        ulong NextVersion);

    private readonly record struct PublisherPrivateInfo(
        object CurrentGeneration,
        int PendingResourceCount,
        int PendingSamplerCount,
        int PendingBindingCount);

    private static void AssertDescriptorRecordTypes(
        DescriptorTable table,
        string storageField,
        uint firstIndex,
        ReadOnlySpan<ResourceBindingType> expected,
        bool hasOwner = false)
    {
        object publisher = GetRequiredProperty(table, "Publisher");
        object storage = GetRequiredField(publisher, storageField).GetValue(publisher)!;
        for (int slot = 0; slot < expected.Length; slot++)
        {
            uint index = checked(firstIndex + (uint)slot);
            object? record = storage switch
            {
                IDictionary dictionary => dictionary[index],
                Array array => array.GetValue(checked((int)index)),
                _ => throw new Xunit.Sdk.XunitException(
                    $"Unexpected descriptor record storage {storage.GetType().FullName}."),
            };
            Assert.NotNull(record);
            Assert.Equal(
                expected[slot],
                (ResourceBindingType)GetRequiredProperty(record!, "Type"));
            Assert.Equal(hasOwner, GetRequiredProperty(record!, "Owner") is not null);
        }
    }

    private static object GetRequiredProperty(object instance, string name) =>
        instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static FieldInfo GetRequiredField(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
}
