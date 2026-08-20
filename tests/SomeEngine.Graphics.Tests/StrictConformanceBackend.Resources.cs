namespace SomeEngine.Graphics.Tests;

internal sealed partial class StrictConformanceBackend
{
    public MemoryRequirements GetBufferMemoryRequirements(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        _ = RequireDevice(device);
        ValidateBufferDescription(desc, memoryType);
        return new MemoryRequirements(Align(desc.Size, 256), 256, HeapFlags.Buffers);
    }

    public MemoryRequirements GetTextureMemoryRequirements(Device device, in TextureDesc desc)
    {
        _ = RequireDevice(device);
        ulong size = GetTextureStorageSize(desc);
        HeapFlags flags = HeapFlags.Textures;
        if ((desc.Usages & (TextureUsages.ColorAttachment | TextureUsages.DepthStencilAttachment)) != 0)
            flags |= HeapFlags.Attachments;
        return new MemoryRequirements(Align(size, 256), 256, flags);
    }

    public TextureCopyFootprint GetTextureCopyFootprint(
        Device device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0)
    {
        _ = RequireDevice(device);
        _ = GetTextureStorageSize(desc);
        uint width = copy.Width == 0 ? desc.Width : copy.Width;
        uint height = copy.Height == 0 ? desc.Height : copy.Height;
        ulong rowSize = checked((ulong)width * BytesPerPixel(desc.Format));
        uint rowPitch = checked((uint)Align(rowSize, 1));
        ulong total = checked((ulong)rowPitch * height * Math.Max(copy.Depth, 1u));
        return new TextureCopyFootprint(
            requestedBufferOffset,
            rowPitch,
            height,
            rowSize,
            total);
    }

    public Heap CreateHeap(Device device, in HeapDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        if (desc.Size == 0 || desc.Size > int.MaxValue ||
            !Enum.IsDefined(desc.MemoryType) || desc.Flags == HeapFlags.None ||
            desc.CreationNodeMask != 1 || desc.VisibleNodeMask != 1)
        {
            throw new ArgumentException("The Heap description is invalid for the conformance backend.", nameof(desc));
        }
        var result = new ConformanceHeap(this, native, desc);
        native.Register(result);
        return result;
    }

    public Buffer CreateBuffer(
        Device device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        ConformanceDevice native = RequireDevice(device);
        ValidateBufferDescription(desc, memoryType);
        byte[] storage = new byte[checked((int)desc.Size)];
        return RegisterBuffer(native, null, storage, 0, desc, memoryType, 0, desc.Size);
    }

    public Buffer CreatePlacedBuffer(Device device, Heap heap, ulong offset, in BufferDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceHeap placement = RequireResource(heap) as ConformanceHeap
            ?? throw new ArgumentException("The Heap has the wrong backend type.", nameof(heap));
        RequireSameDevice(native, placement, nameof(heap));
        ValidateBufferDescription(desc, placement.Info.MemoryType);
        MemoryRequirements requirements = GetBufferMemoryRequirements(device, desc, placement.Info.MemoryType);
        if ((placement.Info.Flags & HeapFlags.Buffers) == 0 ||
            offset % requirements.Alignment != 0 ||
            offset > placement.Info.Size || requirements.Size > placement.Info.Size - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        return RegisterBuffer(
            native,
            placement,
            placement.Storage,
            checked((int)offset),
            desc,
            placement.Info.MemoryType,
            offset,
            requirements.Size);
    }

    public Texture CreateTexture(Device device, in TextureDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ulong size = GetTextureStorageSize(desc);
        byte[] storage = new byte[checked((int)size)];
        return RegisterTexture(native, null, storage, 0, desc, MemoryType.DeviceLocal, 0, size);
    }

    public Texture CreatePlacedTexture(Device device, Heap heap, ulong offset, in TextureDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceHeap placement = RequireResource(heap) as ConformanceHeap
            ?? throw new ArgumentException("The Heap has the wrong backend type.", nameof(heap));
        RequireSameDevice(native, placement, nameof(heap));
        MemoryRequirements requirements = GetTextureMemoryRequirements(device, desc);
        if ((placement.Info.Flags & (HeapFlags.Textures | HeapFlags.Attachments)) == 0 ||
            offset % requirements.Alignment != 0 ||
            offset > placement.Info.Size || requirements.Size > placement.Info.Size - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        return RegisterTexture(
            native,
            placement,
            placement.Storage,
            checked((int)offset),
            desc,
            placement.Info.MemoryType,
            offset,
            requirements.Size);
    }

    public BufferCbv CreateBufferCbv(Device device, in BufferCbvDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceBuffer buffer = RequireBuffer(native, desc.Buffer, nameof(desc));
        BufferRange range = desc.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.Constant) == 0 ||
            range.Offset % 256 != 0 || range.Size % 256 != 0 || range.Size > 65_536)
        {
            throw new ArgumentException("The constant-buffer view range is invalid.", nameof(desc));
        }
        var result = new ConformanceBufferCbv(this, native, desc);
        native.Register(result);
        return result;
    }

    public BufferSrv CreateBufferSrv(Device device, in BufferSrvDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceBuffer buffer = RequireBuffer(native, desc.Buffer, nameof(desc));
        _ = desc.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.ShaderRead) == 0)
            throw new ArgumentException("The Buffer has no ShaderRead usage.", nameof(desc));
        var result = new ConformanceBufferSrv(this, native, desc);
        native.Register(result);
        return result;
    }

    public BufferUav CreateBufferUav(Device device, in BufferUavDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceBuffer buffer = RequireBuffer(native, desc.Buffer, nameof(desc));
        _ = desc.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.ShaderWrite) == 0)
            throw new ArgumentException("The Buffer has no ShaderWrite usage.", nameof(desc));
        if (desc.CounterBuffer is not null)
            _ = RequireBuffer(native, desc.CounterBuffer, nameof(desc));
        var result = new ConformanceBufferUav(this, native, desc);
        native.Register(result);
        return result;
    }

    public TextureSrv CreateTextureSrv(Device device, in TextureSrvDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceTexture texture = RequireTexture(native, desc.Texture, nameof(desc));
        if ((texture.Info.Usages & TextureUsages.Sampled) == 0)
            throw new ArgumentException("The Texture has no Sampled usage.", nameof(desc));
        var result = new ConformanceTextureSrv(this, native, desc);
        native.Register(result);
        return result;
    }

    public TextureUav CreateTextureUav(Device device, in TextureUavDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceTexture texture = RequireTexture(native, desc.Texture, nameof(desc));
        if ((texture.Info.Usages & TextureUsages.Storage) == 0)
            throw new ArgumentException("The Texture has no Storage usage.", nameof(desc));
        var result = new ConformanceTextureUav(this, native, desc);
        native.Register(result);
        return result;
    }

    public ColorAttachmentView CreateColorAttachmentView(
        Device device,
        in ColorAttachmentViewDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceTexture texture = RequireTexture(native, desc.Texture, nameof(desc));
        if ((texture.Info.Usages & TextureUsages.ColorAttachment) == 0)
            throw new ArgumentException("The Texture has no ColorAttachment usage.", nameof(desc));
        var result = new ConformanceColorAttachmentView(this, native, desc);
        native.Register(result);
        return result;
    }

    public DepthStencilView CreateDepthStencilView(
        Device device,
        in DepthStencilViewDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        ConformanceTexture texture = RequireTexture(native, desc.Texture, nameof(desc));
        if ((texture.Info.Usages & TextureUsages.DepthStencilAttachment) == 0)
            throw new ArgumentException("The Texture has no DepthStencilAttachment usage.", nameof(desc));
        var result = new ConformanceDepthStencilView(this, native, desc);
        native.Register(result);
        return result;
    }

    public Sampler CreateSampler(Device device, in SamplerDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        var result = new ConformanceSampler(this, native, desc);
        native.Register(result);
        return result;
    }

    public MappedBuffer Map(Buffer buffer, MapType type, in BufferRange range)
    {
        ConformanceBuffer native = RequireResource(buffer) as ConformanceBuffer
            ?? throw new ArgumentException("The Buffer has the wrong backend type.", nameof(buffer));
        if (native.Info.MemoryType == MemoryType.DeviceLocal ||
            native.Info.MemoryType == MemoryType.Upload && type == MapType.Read ||
            native.Info.MemoryType == MemoryType.Readback && type != MapType.Read)
        {
            throw new ArgumentException("The Map type is incompatible with the Buffer memory type.", nameof(type));
        }
        BufferRange resolved = range.Resolve(native.Info.Size);
        return native.Map(resolved);
    }

    private ConformanceBuffer RegisterBuffer(
        ConformanceDevice device,
        ConformanceHeap? heap,
        byte[] storage,
        int storageOffset,
        in BufferDesc desc,
        MemoryType memoryType,
        ulong allocationOffset,
        ulong allocationSize)
    {
        PipelineSync sync = PipelineSync.None;
        ResourceAccess access = ResourceAccess.NoAccess;
        BufferInfo info = new(
            desc.Size,
            desc.Usages,
            memoryType,
            allocationOffset,
            allocationSize,
            1,
            1);
        var result = new ConformanceBuffer(
            this,
            device,
            heap,
            storage,
            storageOffset,
            info,
            sync,
            access,
            desc.Label);
        device.Register(result);
        return result;
    }

    private ConformanceTexture RegisterTexture(
        ConformanceDevice device,
        ConformanceHeap? heap,
        byte[] storage,
        int storageOffset,
        in TextureDesc desc,
        MemoryType memoryType,
        ulong allocationOffset,
        ulong allocationSize)
    {
        TextureInfo info = new(
            desc.Dimension,
            desc.Width,
            desc.Height,
            desc.Depth,
            desc.MipLevelCount,
            desc.ArrayLayerCount,
            desc.SampleCount,
            desc.Format,
            desc.Usages,
            memoryType,
            desc.PermittedViewFormats,
            allocationOffset,
            allocationSize,
            1,
            1);
        var result = new ConformanceTexture(
            this,
            device,
            heap,
            storage,
            storageOffset,
            info,
            desc.Label);
        device.Register(result);
        return result;
    }

    private static void ValidateBufferDescription(in BufferDesc desc, MemoryType memoryType)
    {
        if (desc.Size == 0 || desc.Size > int.MaxValue || !Enum.IsDefined(memoryType) ||
            desc.NodePlacement.CreationNodeMask is not (0 or 1) ||
            desc.NodePlacement.VisibleNodeMask is not (0 or 1))
        {
            throw new ArgumentException("The Buffer description is invalid.", nameof(desc));
        }
    }

    private static ulong GetTextureStorageSize(in TextureDesc desc)
    {
        if (!Enum.IsDefined(desc.Dimension) || !Enum.IsDefined(desc.Format) ||
            desc.Width == 0 || desc.Height == 0 || desc.Depth == 0 ||
            desc.MipLevelCount == 0 || desc.ArrayLayerCount == 0 || desc.SampleCount == 0 ||
            desc.NodePlacement.CreationNodeMask is not (0 or 1) ||
            desc.NodePlacement.VisibleNodeMask is not (0 or 1))
        {
            throw new ArgumentException("The Texture description is invalid.", nameof(desc));
        }
        ulong total = 0;
        uint width = desc.Width;
        uint height = desc.Height;
        uint depth = desc.Depth;
        for (uint mip = 0; mip < desc.MipLevelCount; mip++)
        {
            total = checked(total +
                (ulong)Math.Max(width, 1u) * Math.Max(height, 1u) * Math.Max(depth, 1u) *
                desc.ArrayLayerCount * desc.SampleCount * BytesPerPixel(desc.Format));
            width >>= 1;
            height >>= 1;
            depth >>= 1;
        }
        if (total > int.MaxValue)
            throw new NotSupportedException("The conformance backend limits Texture storage to 2 GiB.");
        return total;
    }

    private static uint BytesPerPixel(Format format) => format switch
    {
        Format.R8UNorm or Format.R8SNorm or Format.R8UInt or Format.R8SInt => 1,
        Format.R8G8UNorm or Format.R8G8SNorm or Format.R8G8UInt or Format.R8G8SInt or
        Format.R16UNorm or Format.R16SNorm or Format.R16UInt or Format.R16SInt or
        Format.R16Float or Format.D16UNorm => 2,
        Format.R16G16B16A16UNorm or Format.R16G16B16A16SNorm or
        Format.R16G16B16A16UInt or Format.R16G16B16A16SInt or
        Format.R16G16B16A16Float or Format.R32G32UInt or Format.R32G32SInt or
        Format.R32G32Float or Format.D32FloatS8UInt => 8,
        Format.R32G32B32Float => 12,
        Format.R32G32B32A32UInt or Format.R32G32B32A32SInt or
        Format.R32G32B32A32Float => 16,
        Format.BC1UNorm or Format.BC1UNormSrgb or Format.BC4UNorm or Format.BC4SNorm => 1,
        _ => 4,
    };

    private static ulong Align(ulong value, ulong alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private static void RequireSameDevice(
        ConformanceDevice device,
        DeviceResource resource,
        string parameterName)
    {
        if (!ReferenceEquals(device, resource.Device))
            throw new ArgumentException("The object belongs to another Device.", parameterName);
    }

    private ConformanceBuffer RequireBuffer(
        ConformanceDevice device,
        Buffer value,
        string parameterName)
    {
        ConformanceBuffer result = RequireResource(value) as ConformanceBuffer
            ?? throw new ArgumentException("The Buffer has the wrong backend type.", parameterName);
        RequireSameDevice(device, result, parameterName);
        return result;
    }

    private ConformanceTexture RequireTexture(
        ConformanceDevice device,
        Texture value,
        string parameterName)
    {
        ConformanceTexture result = RequireResource(value) as ConformanceTexture
            ?? throw new ArgumentException("The Texture has the wrong backend type.", parameterName);
        RequireSameDevice(device, result, parameterName);
        return result;
    }

    private sealed class ConformanceHeap : Heap, IConformanceObject
    {
        internal ConformanceHeap(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            in HeapDesc desc)
            : base(device, new HeapInfo(
                desc.Size,
                desc.Alignment == 0 ? 256 : desc.Alignment,
                desc.MemoryType,
                desc.Flags,
                desc.CreationNodeMask,
                desc.VisibleNodeMask), desc.Label)
        {
            Owner = owner;
            Storage = new byte[checked((int)desc.Size)];
        }

        public StrictConformanceBackend Owner { get; }
        internal byte[] Storage { get; }

        internal override void Release(bool fromParent) =>
            ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceBuffer : Buffer, IConformanceObject
    {
        private readonly ConformanceMappingLease _mapping;

        internal ConformanceBuffer(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            ConformanceHeap? heap,
            byte[] storage,
            int storageOffset,
            in BufferInfo info,
            PipelineSync initialSync,
            ResourceAccess initialAccess,
            string? label)
            : base(device, heap, info, initialSync, initialAccess, label)
        {
            Owner = owner;
            Storage = storage;
            StorageOffset = storageOffset;
            _mapping = new ConformanceMappingLease(this);
        }

        public StrictConformanceBackend Owner { get; }
        internal byte[] Storage { get; }
        internal int StorageOffset { get; }
        internal Span<byte> Bytes => Storage.AsSpan(StorageOffset, checked((int)Info.Size));

        internal MappedBuffer Map(BufferRange range)
        {
            ulong sequence = _mapping.PrepareNextSequence();
            _mapping.PublishMapping(sequence, range);
            return new MappedBuffer(
                _mapping,
                Bytes.Slice(checked((int)range.Offset), checked((int)range.Size)),
                sequence);
        }

        internal override void Release(bool fromParent)
        {
            _mapping.DisposeCurrent();
            ((ConformanceDevice)Device).Unregister(this);
        }
    }

    private sealed class ConformanceMappingLease(ConformanceBuffer buffer) : MappingLease(buffer)
    {
        internal void PublishMapping(ulong sequence, in BufferRange range) => Publish(sequence, range);
        protected override void FlushCore(in BufferRange range) { }
        protected override void InvalidateCore(in BufferRange range) { }
        protected override void UnmapCore() { }
    }

    private sealed class ConformanceTexture : Texture, IConformanceObject
    {
        internal ConformanceTexture(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            ConformanceHeap? heap,
            byte[] storage,
            int storageOffset,
            TextureInfo info,
            string? label)
            : base(
                device,
                heap,
                info,
                PipelineSync.None,
                ResourceAccess.NoAccess,
                TextureLayout.Undefined,
                label)
        {
            Owner = owner;
            Storage = storage;
            StorageOffset = storageOffset;
        }

        public StrictConformanceBackend Owner { get; }
        internal byte[] Storage { get; }
        internal int StorageOffset { get; }
        internal Span<byte> Bytes => Storage.AsSpan(StorageOffset, checked((int)Info.AllocationSize));
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private abstract class ConformanceViewBase : IConformanceObject
    {
        protected ConformanceViewBase(StrictConformanceBackend owner) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
    }

    private sealed class ConformanceBufferCbv : BufferCbv, IConformanceObject
    {
        internal ConformanceBufferCbv(StrictConformanceBackend owner, Device device, in BufferCbvDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceBufferSrv : BufferSrv, IConformanceObject
    {
        internal ConformanceBufferSrv(StrictConformanceBackend owner, Device device, in BufferSrvDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceBufferUav : BufferUav, IConformanceObject
    {
        internal ConformanceBufferUav(StrictConformanceBackend owner, Device device, in BufferUavDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceTextureSrv : TextureSrv, IConformanceObject
    {
        internal ConformanceTextureSrv(StrictConformanceBackend owner, Device device, in TextureSrvDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceTextureUav : TextureUav, IConformanceObject
    {
        internal ConformanceTextureUav(StrictConformanceBackend owner, Device device, in TextureUavDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceColorAttachmentView : ColorAttachmentView, IConformanceObject
    {
        internal ConformanceColorAttachmentView(
            StrictConformanceBackend owner,
            Device device,
            in ColorAttachmentViewDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceDepthStencilView : DepthStencilView, IConformanceObject
    {
        internal ConformanceDepthStencilView(
            StrictConformanceBackend owner,
            Device device,
            in DepthStencilViewDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }

    private sealed class ConformanceSampler : Sampler, IConformanceObject
    {
        internal ConformanceSampler(StrictConformanceBackend owner, Device device, in SamplerDesc desc)
            : base(device, desc) => Owner = owner;
        public StrictConformanceBackend Owner { get; }
        internal override void Release(bool fromParent) => ((ConformanceDevice)Device).Unregister(this);
    }
}
