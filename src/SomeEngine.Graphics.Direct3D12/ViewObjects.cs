namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private sealed class D3D12BufferCbv : BufferCbv, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12BufferCbv(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferCbvDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(device, descriptor, buffer.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed class D3D12BufferSrv : BufferSrv, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12BufferSrv(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferSrvDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(device, descriptor, buffer.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed class D3D12BufferUav : BufferUav, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12BufferUav(
            D3D12Device device,
            D3D12Buffer buffer,
            D3D12Buffer? counter,
            in BufferUavDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(
                device,
                descriptor,
                buffer.NativeData,
                counter?.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed class D3D12TextureSrv : TextureSrv, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12TextureSrv(
            D3D12Device device,
            D3D12TextureResource texture,
            in TextureSrvDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(device, descriptor, texture.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed class D3D12TextureUav : TextureUav, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12TextureUav(
            D3D12Device device,
            D3D12TextureResource texture,
            in TextureUavDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(device, descriptor, texture.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed class D3D12ColorAttachmentView : ColorAttachmentView, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12ColorAttachmentView(
            D3D12Device device,
            D3D12TextureResource texture,
            in ColorAttachmentViewDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            NativeResource = texture;
            _references = new ViewReferences(device, descriptor, texture.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal D3D12TextureResource NativeResource { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed class D3D12DepthStencilView : DepthStencilView, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12DepthStencilView(
            D3D12Device device,
            D3D12TextureResource texture,
            in DepthStencilViewDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            NativeResource = texture;
            _references = new ViewReferences(device, descriptor, texture.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal D3D12TextureResource NativeResource { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed class D3D12Sampler : Sampler, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12Sampler(
            D3D12Device device,
            in SamplerDesc description,
            DescriptorAllocation descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(device, descriptor);
        }

        public DescriptorAllocation NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _references.Release(this);
    }

}
