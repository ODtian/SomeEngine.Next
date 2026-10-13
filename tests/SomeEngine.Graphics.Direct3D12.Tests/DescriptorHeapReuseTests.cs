using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class DescriptorHeapReuseTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public unsafe void Mixed_bindings_keep_published_values_across_reuse_growth_and_in_flight_recordings()
    {
        using var data = new DescriptorResources(shaderCompiler);
        IGraphicsBackend backend = data.Backend;
        using CommandContext context = backend.CreateCommandContext(data.Device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(data.Device, QueueType.Compute);
        using ExternalTimeline gate = backend.CreateExternalTimeline(data.Device, 0);
        DescriptorTable? moreTextures = null;
        DescriptorTable? moreSamplers = null;

        try
        {
            for (int frame = 0; frame < 5; frame++)
            {
                backend.Begin(context, new CommandRecordingDesc(8, 4, 16));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.None,
                    PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(context, data.Ordinary);
                backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
                    data.OrdinaryResources, UInts(0, (uint)frame * 10 + 1)));
                backend.Dispatch(context, new(1, 1, 1));

                // Publish only after the ordinary dispatch was recorded. Earlier bindings
                // must keep their values, and the first indexed binding must see this update.
                if (frame == 2)
                {
                    moreTextures = backend.CreateDescriptorTable(data.Device,
                        Enumerable.Repeat(DescriptorResources.TextureSlot, 32).ToArray());
                    moreSamplers = backend.CreateDescriptorTable(data.Device,
                        Enumerable.Repeat(new DescriptorSlotDesc(ResourceBindingType.Sampler), 32).ToArray());
                    for (uint index = 0; index < 32; index++)
                    {
                        backend.WriteDescriptor(moreTextures, index, ResourceBinding.SampledTexture(data.FirstView));
                        backend.WriteDescriptor(moreSamplers, index, ResourceBinding.SampledWith(data.Edge));
                    }
                }
                if (frame == 4)
                {
                    moreTextures!.Dispose();
                    moreSamplers!.Dispose();
                    moreTextures = null;
                    moreSamplers = null;
                }
                DescriptorTable textures = moreTextures ?? data.Textures;
                DescriptorTable samplers = moreSamplers ?? data.Samplers;
                uint tableElement = moreTextures is null ? 0u : 31u;
                TextureSrv texture = frame is 1 or 3 or 4 ? data.SecondView : data.FirstView;
                Sampler sampler = frame is 1 or 3 ? data.Border : data.Edge;
                backend.WriteDescriptor(textures, tableElement, ResourceBinding.SampledTexture(texture));
                backend.WriteDescriptor(samplers, tableElement, ResourceBinding.SampledWith(sampler));
                backend.PublishDescriptors(data.Device);

                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(context, data.Indexed);
                backend.SetTransientParameterBindings(context, new(data.IndexedLayout,
                    [ResourceBinding.WritableBuffer(data.OutputView)],
                    UInts(backend.GetDescriptorIndex(textures, tableElement).Value,
                        backend.GetDescriptorIndex(samplers, tableElement).Value)));
                backend.Dispatch(context, new(1, 1, 1));

                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(context, data.Ordinary);
                backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
                    data.OrdinaryResources, UInts(3, (uint)frame * 10 + 3)));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new(data.Output, 0, data.Readback, (ulong)frame * 16, 16));
                using RecordedCommands commands = backend.End(context);
                QueueCompletion completion = frame == 2
                    ? backend.Submit(queue, new([], [new(gate, 1)], [commands], [], []))
                    : backend.Submit(queue, new([], [], [commands], [], []));

                if (frame == 2)
                {
                    Assert.False(backend.IsComplete(completion));
                    continue;
                }
                if (frame == 3)
                    Assert.True(data.Backend.GetNativeTimeline(gate)->Signal(1) >= 0);
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(data.Device);
            }

            using MappedBuffer mapped = backend.Map(data.Readback, MapType.Read, new(0, 80));
            mapped.Invalidate(new(0, 80));
            Assert.Equal(new uint[]
            {
                26, 25, 25, 28,
                36, 75, 0, 38,
                46, 25, 25, 48,
                56, 75, 0, 58,
                66, 75, 75, 68,
            }, MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
        }
        finally
        {
            _ = data.Backend.GetNativeTimeline(gate)->Signal(1);
            moreTextures?.Dispose();
            moreSamplers?.Dispose();
        }
    }

    [Fact]
    public void Ordinary_bindings_keep_full_heap_capacity_after_indexed_recordings_are_discarded()
    {
        using var data = new DescriptorResources(shaderCompiler);
        IGraphicsBackend backend = data.Backend;
        using CommandContext context = backend.CreateCommandContext(data.Device, new(QueueType.Compute, 0, 1));

        // Create and reuse the mixed heaps without submitting either recording.
        for (int recording = 0; recording < 2; recording++)
        {
            backend.Begin(context, new CommandRecordingDesc(8, 4, 16));
            backend.SetPipeline(context, data.Ordinary);
            backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
                data.OrdinaryResources, UInts(0, 1)));
            backend.Dispatch(context, new(1, 1, 1));
            backend.SetPipeline(context, data.Indexed);
            backend.SetTransientParameterBindings(context, new(data.IndexedLayout,
                [ResourceBinding.WritableBuffer(data.OutputView)],
                UInts(backend.GetDescriptorIndex(data.Textures, 0).Value,
                    backend.GetDescriptorIndex(data.Samplers, 0).Value)));
            backend.Dispatch(context, new(1, 1, 1));
            backend.Discard(context);
        }

        data.Textures.Dispose();
        data.Samplers.Dispose();
        backend.PublishDescriptors(data.Device);
        DeviceLimits limits = data.Device.Capabilities.Limits;
        backend.Begin(context, new CommandRecordingDesc(
            limits.ResourceDescriptorCapacity, limits.SamplerDescriptorCapacity, 16));
        backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.None,
            PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, data.Ordinary);
        backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
            data.OrdinaryResources, UInts(0, 17)));
        backend.Dispatch(context, new(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
            PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new(data.Output, 0, data.Readback, 0, 4));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(data.Device, QueueType.Compute),
            new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(data.Device);
        using MappedBuffer mapped = backend.Map(data.Readback, MapType.Read, new(0, 4));
        mapped.Invalidate(new(0, 4));
        Assert.Equal(42u, MemoryMarshal.Read<uint>(mapped.Bytes));
    }

    [Fact]
    public unsafe void Mixed_bindings_repeat_across_retirement_but_reject_changed_descriptor_generations()
    {
        using var data = new DescriptorResources(shaderCompiler);
        // ValidationLayer may decline the optional repetition API. Exercise the
        // public D3D12 receiver directly; the fixture still enables native validation.
        IGraphicsBackend backend = data.NativeBackend;
        using CommandContext context = backend.CreateCommandContext(data.Device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(data.Device, QueueType.Compute);
        using ExternalTimeline gate = data.Backend.CreateExternalTimeline(data.Device, 0);
        var nativeGate = data.Backend.GetNativeTimeline(gate);
        try
        {
            for (int recording = 0; recording < 3; recording++)
            {
                if (recording != 0)
                {
                    backend.WriteDescriptor(data.Textures, 0,
                        ResourceBinding.SampledTexture(recording == 1 ? data.SecondView : data.FirstView));
                    backend.WriteDescriptor(data.Samplers, 0,
                        ResourceBinding.SampledWith(recording == 1 ? data.Border : data.Edge));
                    backend.PublishDescriptors(data.Device);
                    Assert.False(backend.TryRepeatCommands(context, out _));
                }

                // The larger middle recording replaces the previous spare while older
                // native caches still retain the heaps referenced by their command data.
                uint capacity = recording == 1 ? 64u : 8u;
                backend.Begin(context, new CommandRecordingDesc(capacity, capacity, 16));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.None,
                    PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(context, data.Ordinary);
                backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
                    data.OrdinaryResources, UInts(0, (uint)recording * 10 + 1)));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(context, data.Indexed);
                backend.SetTransientParameterBindings(context, new(data.IndexedLayout,
                    [ResourceBinding.WritableBuffer(data.OutputView)],
                    UInts(backend.GetDescriptorIndex(data.Textures, 0).Value,
                        backend.GetDescriptorIndex(data.Samplers, 0).Value)));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(context, data.Ordinary);
                backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
                    data.OrdinaryResources, UInts(3, (uint)recording * 10 + 3)));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new(data.Output, 0, data.Readback, (ulong)recording * 16, 16));
                using (RecordedCommands commands = backend.End(context))
                {
                    QueueCompletion completion = recording == 0
                        ? backend.Submit(queue, new([], [new(gate, 1)], [commands], [], []))
                        : backend.Submit(queue, new([], [], [commands], [], []));
                    if (recording == 0)
                    {
                        Assert.False(backend.IsComplete(completion));
                        Assert.False(backend.TryRepeatCommands(context, out _));
                        Assert.True(nativeGate->Signal(1) >= 0);
                    }
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                }
                backend.CollectCompleted(data.Device);

                backend.PublishDescriptors(data.Device);
                Assert.True(backend.TryRepeatCommands(context, out RecordedCommands discarded));
                discarded.Dispose();
                Assert.True(backend.TryRepeatCommands(context, out RecordedCommands repeated));
                using (repeated)
                {
                    // Executable command storage must retain all four mixed heaps even
                    // when the public context is disposed before its final submission.
                    if (recording == 2) context.Dispose();
                    QueueCompletion completion = backend.Submit(queue, new([], [], [repeated], [], []));
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                }
                backend.CollectCompleted(data.Device);
            }

            using MappedBuffer mapped = backend.Map(data.Readback, MapType.Read, new(0, 48));
            mapped.Invalidate(new(0, 48));
            Assert.Equal(new uint[] { 26, 25, 25, 28, 36, 75, 0, 38, 46, 25, 25, 48 },
                MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
        }
        finally
        {
            _ = nativeGate->Signal(1);
        }
    }

    [Fact]
    public void Multiple_heap_replacements_preserve_every_dispatch_and_decline_repetition()
    {
        using var data = new DescriptorResources(shaderCompiler);
        IGraphicsBackend backend = data.NativeBackend;
        using CommandContext context = backend.CreateCommandContext(data.Device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(data.Device, QueueType.Compute);

        // Alternating both resource and sampler bindings consumes new descriptor
        // positions. The first recording grows both heaps several times.
        for (int recording = 0; recording < 2; recording++)
        {
            backend.Begin(context, recording == 0
                ? new CommandRecordingDesc(2, 1, 16)
                : new CommandRecordingDesc(64, 32, 16));
            backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.None,
                PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, data.Ordinary);
            for (uint dispatch = 0; dispatch < 8; dispatch++)
            {
                ResourceBinding[] bindings = dispatch % 2 == 0 ? data.OrdinaryResources :
                    [ResourceBinding.SampledTexture(data.SecondView), ResourceBinding.SampledWith(data.Border),
                        ResourceBinding.WritableBuffer(data.OutputView)];
                backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
                    bindings, UInts(0, dispatch)));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new(data.Output, 0, data.Readback, dispatch * 4, 4));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.Copy,
                    PipelineSync.ComputeShading, ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            }
            using (RecordedCommands commands = backend.End(context))
            {
                QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            }
            backend.CollectCompleted(data.Device);
            using (MappedBuffer mapped = backend.Map(data.Readback, MapType.Read, new(0, 32)))
            {
                mapped.Invalidate(new(0, 32));
                Assert.Equal(new uint[] { 25, 1, 27, 3, 29, 5, 31, 7 },
                    MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
            }

            if (recording == 0)
                Assert.False(backend.TryRepeatCommands(context, out _));
            else
            {
                Assert.True(backend.TryRepeatCommands(context, out RecordedCommands repeated));
                using (repeated)
                {
                    QueueCompletion completion = backend.Submit(queue, new([], [], [repeated], [], []));
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                }
                backend.CollectCompleted(data.Device);
                using MappedBuffer mapped = backend.Map(data.Readback, MapType.Read, new(0, 32));
                mapped.Invalidate(new(0, 32));
                Assert.Equal(new uint[] { 25, 1, 27, 3, 29, 5, 31, 7 },
                    MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
            }
        }
    }

    [Fact]
    public void Ordinary_updates_observe_resource_replacements_and_binding_state_changes()
    {
        using var data = new DescriptorResources(shaderCompiler);
        IGraphicsBackend backend = data.NativeBackend;
        using CommandContext context = backend.CreateCommandContext(data.Device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(data.Device, QueueType.Compute);
        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            data.Device, data.Ordinary, new(data.OrdinaryLayout, data.OrdinaryResources, UInts(0, 777)));

        for (uint recording = 0; recording < 2; recording++)
        {
            ResourceBinding[] packet = data.OrdinaryResources.ToArray();
            backend.Begin(context, new CommandRecordingDesc(64, 32, 16));
            backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.None,
                PipelineSync.ComputeShading, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, data.Ordinary);
            for (uint dispatch = 0; dispatch < 6; dispatch++)
            {
                // Mutating the caller packet must not mutate a previously retained snapshot.
                if (dispatch == 2) packet[0] = ResourceBinding.SampledTexture(data.SecondView);
                if (dispatch == 3)
                {
                    backend.SetPipeline(context, data.Indexed);
                    backend.SetPipeline(context, data.Ordinary);
                }
                if (dispatch == 4) backend.SetPersistentParameterBindings(context, persistent);
                if (dispatch == 5) packet[0] = ResourceBinding.SampledTexture(data.FirstView);
                backend.SetTransientParameterBindings(context, new(data.OrdinaryLayout,
                    packet, UInts(0, recording * 10 + dispatch)));
                backend.Dispatch(context, new(1, 1, 1));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.ComputeShading,
                    PipelineSync.Copy, ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(context, new(data.Output, 0, data.Readback,
                    (recording * 6 + dispatch) * sizeof(uint), sizeof(uint)));
                backend.Barrier(context, new BufferBarrier(data.Output, PipelineSync.Copy,
                    PipelineSync.ComputeShading, ResourceAccess.CopySource, ResourceAccess.UnorderedAccess));
            }
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(data.Device);
        }

        using MappedBuffer mapped = backend.Map(data.Readback, MapType.Read, new(0, 48));
        mapped.Invalidate(new(0, 48));
        Assert.Equal(new uint[] { 25, 26, 77, 78, 79, 30, 35, 36, 87, 88, 89, 40 },
            MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
    }

    private static byte[] UInts(uint first, uint second)
    {
        byte[] values = new byte[8];
        MemoryMarshal.Write(values, first);
        MemoryMarshal.Write(values.AsSpan(4), second);
        return values;
    }

    private sealed class DescriptorResources : IDisposable
    {
        internal static DescriptorSlotDesc TextureSlot => new(ResourceBindingType.TextureSrv,
            Format.R32Float, TextureDimension: TextureViewDimension.Texture2D);
        internal ValidationLayer Backend { get; }
        internal IGraphicsBackend NativeBackend { get; }
        internal Device Device { get; }
        internal Pipeline Ordinary { get; }
        internal Pipeline Indexed { get; }
        internal VariableLayoutReflection OrdinaryLayout { get; }
        internal VariableLayoutReflection IndexedLayout { get; }
        internal Texture First { get; }
        internal Texture Second { get; }
        internal TextureSrv FirstView { get; }
        internal TextureSrv SecondView { get; }
        internal Sampler Edge { get; }
        internal Sampler Border { get; }
        internal DescriptorTable Textures { get; }
        internal DescriptorTable Samplers { get; }
        internal Buffer Output { get; }
        internal BufferUav OutputView { get; }
        internal Buffer Readback { get; }
        internal ResourceBinding[] OrdinaryResources { get; }

        internal DescriptorResources(D3D12TestCompiler compiler)
        {
            D3D12TestShader ordinary = compiler.Compile("reused_heap_ordinary", """
                Texture2D<float> inputTexture;
                SamplerState inputSampler;
                RWStructuredBuffer<uint> outputValues;
                uint outputIndex;
                uint addend;
                [shader("compute")]
                [numthreads(1, 1, 1)]
                void computeMain()
                {
                    outputValues[outputIndex] =
                        uint(inputTexture.SampleLevel(inputSampler, float2(1.5), 0) * 100.0) + addend;
                }
                """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
            D3D12TestShader indexed = compiler.Compile("reused_heap_indexed", """
                Texture2D<float> textures[];
                SamplerState samplers[];
                RWStructuredBuffer<uint> outputValues;
                uint textureIndex;
                uint samplerIndex;
                [shader("compute")]
                [numthreads(1, 1, 1)]
                void computeMain()
                {
                    outputValues[1] = uint(textures[textureIndex].SampleLevel(
                        samplers[samplerIndex], float2(0.5), 0) * 100.0);
                    outputValues[2] = uint(textures[textureIndex].SampleLevel(
                        samplers[samplerIndex], float2(1.5), 0) * 100.0);
                }
                """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
            OrdinaryLayout = ordinary.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            IndexedLayout = indexed.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            NativeBackend = D3D12GraphicsBackend.Create();
            Backend = new ValidationLayer(NativeBackend);
            Device = D3D12TestSupport.CreateWarpDevice(Backend);
            Ordinary = Backend.CreateComputePipeline(Device, new(ordinary.SlangCode, ordinary.Reflection,
                ordinary.GetEntryPoint(0), ordinary.GetEntryCode(0)));
            Indexed = Backend.CreateComputePipeline(Device, new(indexed.SlangCode, indexed.Reflection,
                indexed.GetEntryPoint(0), indexed.GetEntryCode(0)));
            First = CreateTexture(0.25f);
            Second = CreateTexture(0.75f);
            TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
            FirstView = Backend.CreateTextureSrv(Device, new(First, range, Format.R32Float, TextureViewDimension.Texture2D));
            SecondView = Backend.CreateTextureSrv(Device, new(Second, range, Format.R32Float, TextureViewDimension.Texture2D));
            Edge = Backend.CreateSampler(Device, new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
            Border = Backend.CreateSampler(Device, new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
            Textures = Backend.CreateDescriptorTable(Device, [TextureSlot]);
            Samplers = Backend.CreateDescriptorTable(Device, [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
            Backend.WriteDescriptor(Textures, 0, ResourceBinding.SampledTexture(FirstView));
            Backend.WriteDescriptor(Samplers, 0, ResourceBinding.SampledWith(Edge));
            Backend.PublishDescriptors(Device);
            Output = Backend.CreateBuffer(Device, new(16, BufferUsages.ShaderWrite | BufferUsages.CopySource));
            OutputView = Backend.CreateBufferUav(Device, new(Output, BufferRange.Whole, StructureStride: 4));
            Readback = Backend.CreateBuffer(Device, new(80, BufferUsages.CopyDestination), MemoryType.Readback);
            OrdinaryResources = [ResourceBinding.SampledTexture(FirstView), ResourceBinding.SampledWith(Edge),
                ResourceBinding.WritableBuffer(OutputView)];
        }

        private Texture CreateTexture(float value)
        {
            Texture texture = Backend.CreateTexture(Device, new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
                Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination));
            D3D12TestSupport.UploadSinglePixelR32Float(Backend, Device, texture, value);
            return texture;
        }

        public void Dispose()
        {
            Readback.Dispose();
            OutputView.Dispose();
            Output.Dispose();
            Textures.Dispose();
            Samplers.Dispose();
            FirstView.Dispose();
            SecondView.Dispose();
            First.Dispose();
            Second.Dispose();
            Edge.Dispose();
            Border.Dispose();
            Ordinary.Dispose();
            Indexed.Dispose();
            Device.Dispose();
            Backend.Dispose();
        }
    }
}
