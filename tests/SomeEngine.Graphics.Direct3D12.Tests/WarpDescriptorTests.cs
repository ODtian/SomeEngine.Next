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
    [Theory]
    [InlineData(17)]
    [InlineData(5000)]
    public void Recorded_commands_retain_persistent_data_after_wrapper_disposal(int replacementCount)
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
        ShaderVariableLayout layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? ShaderVariableLayout.Null;
        int ordinarySize = checked((int)GetDataLayout(layout).GetSize(
            SlangParameterCategory.Uniform));

        using var backend = D3D12GraphicsBackend.Create();
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
        var replacements = new List<PersistentParameterBindings>(replacementCount);
        try
        {
            for (int index = 0; index < replacementCount; index++)
                replacements.Add(backend.CreatePersistentParameterBindings(device, pipeline,
                    new ParameterBlockBindings(layout, resources, CreateOrdinaryData(ordinarySize, (uint)index + 100))));
            for (int index = 0; index < replacements.Count; index += 2) replacements[index].Dispose();
            for (int index = 0; index < replacementCount; index++)
            {
                using PersistentParameterBindings replacement = backend.CreatePersistentParameterBindings(device, pipeline,
                    new ParameterBlockBindings(layout, resources, CreateOrdinaryData(ordinarySize, 9001)));
            }
        }
        finally
        {
            foreach (PersistentParameterBindings replacement in replacements) replacement.Dispose();
        }
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
        ShaderVariableLayout layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? ShaderVariableLayout.Null;
        int ordinarySize = checked((int)GetDataLayout(layout).GetSize(
            SlangParameterCategory.Uniform));

        using var backend = D3D12GraphicsBackend.Create();
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
        ShaderVariableLayout layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? ShaderVariableLayout.Null;
        Assert.Equal((nint)0, GetDataLayout(layout).BindingRangeCount);

        using var backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using PersistentParameterBindings persistent =
            backend.CreatePersistentParameterBindings(
                device,
                pipeline,
                new ParameterBlockBindings(layout, [], new byte[16]));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context);

        backend.SetPipeline(context, pipeline);
        backend.SetPersistentParameterBindings(context, persistent);

        RecordedCommands commands = backend.End(context);
        try
        {
            Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);
        }
        finally
        {
            commands.Dispose();
        }
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
        ShaderVariableLayout layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? ShaderVariableLayout.Null;
        ShaderTypeLayout reflected = GetDataLayout(layout);
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, bindings, []));
        backend.Discard(context);
    }

    private static ShaderTypeLayout GetDataLayout(
        ShaderVariableLayout layout)
    {
        ShaderTypeLayout result = layout.TypeLayout.UnwrapArray();
        if (result.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            result = result.ElementTypeLayout.UnwrapArray();
        return result;
    }

    [Fact]
    public void Initial_descriptor_capacity_exhaustion_is_atomic_and_context_remains_reusable()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        Assert.Equal(ResourceBindingType.Sampler, samplers.Slots[0].Type);

        uint resourceFirst = backend.GetDescriptorIndex(resources, 0).Value;
        uint samplerFirst = backend.GetDescriptorIndex(samplers, 0).Value;
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
    }

    [Fact]
    public void Unpublished_descriptor_table_disposal_immediately_reuses_index()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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





    private static byte[] CreateOrdinaryData(int size, uint value)
    {
        byte[] result = new byte[size];
        Assert.True(BitConverter.TryWriteBytes(result.AsSpan(), value));
        return result;
    }

    private static void ExecutePersistentBinding(
        IGraphicsBackend backend,
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










}
