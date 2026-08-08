using System.Collections;
using System.Reflection;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpDescriptorTests
{
    [Fact]
    public void Initial_descriptor_capacity_exhaustion_is_atomic_and_context_remains_reusable()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

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
    public void Every_typed_null_descriptor_publishes_as_a_valid_native_descriptor()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using DescriptorTable resources = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Resource,
            7);
        ResourceBindingType[] types =
        [
            ResourceBindingType.None,
            ResourceBindingType.ConstantBuffer,
            ResourceBindingType.BufferSrv,
            ResourceBindingType.BufferUav,
            ResourceBindingType.TextureSrv,
            ResourceBindingType.TextureUav,
            ResourceBindingType.AccelerationStructure,
        ];
        for (int slot = 0; slot < types.Length; slot++)
            backend.WriteDescriptor(resources, checked((uint)slot), ResourceBinding.Null(types[slot]));

        using DescriptorTable samplers = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Sampler,
            1);
        backend.WriteDescriptor(
            samplers,
            0,
            ResourceBinding.Null(ResourceBindingType.Sampler));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
    }

    [Fact]
    public void Unpublished_bindless_disposal_cancels_and_immediately_reuses_index()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead));
        BufferSrvDesc description = new(buffer, BufferRange.Whole, Format.R32UInt);

        BindlessBufferSrv first = backend.CreateBindlessBufferSrv(device, description);
        uint firstIndex = first.DescriptorIndex;
        first.Dispose();
        using BindlessBufferSrv replacement =
            backend.CreateBindlessBufferSrv(device, description);

        Assert.Equal(firstIndex, replacement.DescriptorIndex);
    }

    [Fact]
    public void Published_bindless_index_retires_after_every_older_generation()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.ShaderRead));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        BufferSrvDesc description = new(buffer, BufferRange.Whole, Format.R32UInt);

        BindlessBufferSrv first = backend.CreateBindlessBufferSrv(device, description);
        uint retiredIndex = first.DescriptorIndex;
        backend.PublishDescriptors(device);

        backend.Begin(context);
        RecordedCommands heldGeneration = backend.End(context);
        first.Dispose();
        backend.PublishDescriptors(device);

        using BindlessBufferSrv whileHeld =
            backend.CreateBindlessBufferSrv(device, description);
        Assert.NotEqual(retiredIndex, whileHeld.DescriptorIndex);

        heldGeneration.Dispose();
        using BindlessBufferSrv afterRetirement =
            backend.CreateBindlessBufferSrv(device, description);
        Assert.Equal(retiredIndex, afterRetirement.DescriptorIndex);
    }

    [Fact]
    public void Descriptor_table_contiguous_range_uses_generation_safe_reuse()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

        DescriptorTable first = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Resource,
            3);
        uint firstIndex = backend.GetDescriptorIndex(first, 0);
        Assert.Equal(firstIndex + 1, backend.GetDescriptorIndex(first, 1));
        Assert.Equal(firstIndex + 2, backend.GetDescriptorIndex(first, 2));
        backend.PublishDescriptors(device);

        backend.Begin(context);
        RecordedCommands heldGeneration = backend.End(context);
        first.Dispose();
        backend.PublishDescriptors(device);

        using DescriptorTable whileHeld = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Resource,
            3);
        Assert.NotEqual(firstIndex, backend.GetDescriptorIndex(whileHeld, 0));

        heldGeneration.Dispose();
        using DescriptorTable afterRetirement = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Resource,
            3);
        Assert.Equal(firstIndex, backend.GetDescriptorIndex(afterRetirement, 0));
    }

    [Fact]
    public void Invalid_table_type_and_slot_writes_preserve_pending_publication()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using DescriptorTable resources = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Resource,
            1);
        using DescriptorTable samplers = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Sampler,
            1);

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
        backend.WriteDescriptor(
            samplers,
            0,
            ResourceBinding.Null(ResourceBindingType.Sampler));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
    }

    [Fact]
    public void Generation_identity_exhaustion_is_atomic_permanent_and_keeps_current_generation_usable()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using DescriptorTable table = backend.CreateDescriptorTable(
            device,
            DescriptorTableType.Resource,
            1);
        backend.WriteDescriptor(
            table,
            0,
            ResourceBinding.Null(ResourceBindingType.BufferSrv));

        object publisher = GetRequiredProperty(device, "Descriptors");
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
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
    }

    private static object GetRequiredProperty(object instance, string name) =>
        instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static FieldInfo GetRequiredField(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
}
