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
    public void Every_typed_null_descriptor_publishes_as_a_valid_native_descriptor()
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
        Assert.Equal(types, resources.SlotTypes.ToArray());
        Assert.Equal(ResourceBindingType.Sampler, samplers.GetSlotType(0));

        uint resourceFirst = backend.GetDescriptorIndex(resources, 0);
        uint samplerFirst = backend.GetDescriptorIndex(samplers, 0);
        AssertDescriptorRecordTypes(device, "_pendingResources", resourceFirst, types);
        AssertDescriptorRecordTypes(
            device,
            "_pendingSamplers",
            samplerFirst,
            [ResourceBindingType.Sampler]);
        backend.PublishDescriptors(device);
        AssertDescriptorRecordTypes(device, "_resources", resourceFirst, types);
        AssertDescriptorRecordTypes(
            device,
            "_samplers",
            samplerFirst,
            [ResourceBindingType.Sampler]);

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
        AssertDescriptorRecordTypes(device, "_resources", resourceFirst, types, hasOwner: true);
        AssertDescriptorRecordTypes(
            device,
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
        backend.WriteDescriptor(
            samplers,
            0,
            ResourceBinding.Null(ResourceBindingType.Sampler));
        backend.PublishDescriptors(device);
        AssertDescriptorRecordTypes(device, "_resources", resourceFirst, types);
        AssertDescriptorRecordTypes(
            device,
            "_samplers",
            samplerFirst,
            [ResourceBindingType.Sampler]);

        for (int slot = 0; slot < actual.Length; slot++)
            backend.WriteDescriptor(resources, checked((uint)slot), actual[slot]);
        backend.WriteDescriptor(samplers, 0, ResourceBinding.SampledWith(sampler));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands heldGeneration = backend.End(context);

        resources.Dispose();
        samplers.Dispose();
        backend.PublishDescriptors(device);
        AssertDescriptorRecordTypes(device, "_resources", resourceFirst, types);
        AssertDescriptorRecordTypes(
            device,
            "_samplers",
            samplerFirst,
            [ResourceBindingType.Sampler]);
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
            new CommandContextDesc(QueueType.Graphics, 0, 1));
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
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        ResourceBindingType[] slotTypes =
        [
            ResourceBindingType.ConstantBuffer,
            ResourceBindingType.BufferSrv,
            ResourceBindingType.TextureSrv,
        ];
        DescriptorTable first = backend.CreateDescriptorTable(device, slotTypes);
        uint firstIndex = backend.GetDescriptorIndex(first, 0);
        Assert.Equal(firstIndex + 1, backend.GetDescriptorIndex(first, 1));
        Assert.Equal(firstIndex + 2, backend.GetDescriptorIndex(first, 2));
        backend.PublishDescriptors(device);

        backend.Begin(context);
        RecordedCommands heldGeneration = backend.End(context);
        first.Dispose();
        backend.PublishDescriptors(device);

        using DescriptorTable whileHeld = backend.CreateDescriptorTable(device, slotTypes);
        Assert.NotEqual(firstIndex, backend.GetDescriptorIndex(whileHeld, 0));

        heldGeneration.Dispose();
        using DescriptorTable afterRetirement = backend.CreateDescriptorTable(device, slotTypes);
        Assert.Equal(firstIndex, backend.GetDescriptorIndex(afterRetirement, 0));
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
        backend.WriteDescriptor(
            samplers,
            0,
            ResourceBinding.Null(ResourceBindingType.Sampler));
        backend.PublishDescriptors(device);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
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
            [ResourceBindingType.BufferSrv]);
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
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        using RecordedCommands recorded = backend.End(context);
    }

    private static void AssertDescriptorRecordTypes(
        Device device,
        string storageField,
        uint firstIndex,
        ReadOnlySpan<ResourceBindingType> expected,
        bool hasOwner = false)
    {
        object publisher = GetRequiredProperty(device, "Descriptors");
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
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static FieldInfo GetRequiredField(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
}
