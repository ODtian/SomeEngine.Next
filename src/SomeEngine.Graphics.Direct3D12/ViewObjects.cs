namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private sealed class D3D12BufferCbv : BufferCbv, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BufferCbv(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferCbvDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, buffer: buffer);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BufferSrv : BufferSrv, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BufferSrv(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferSrvDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, buffer: buffer);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BufferUav : BufferUav, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BufferUav(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferUavDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, buffer: buffer);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12TextureSrv : TextureSrv, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12TextureSrv(
            D3D12Device device,
            D3D12TextureResource texture,
            in TextureSrvDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, texture: texture);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12TextureUav : TextureUav, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12TextureUav(
            D3D12Device device,
            D3D12TextureResource texture,
            in TextureUavDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, texture: texture);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12ColorAttachmentView : ColorAttachmentView, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12ColorAttachmentView(
            D3D12Device device,
            D3D12TextureResource texture,
            in ColorAttachmentViewDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, texture: texture);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12DepthStencilView : DepthStencilView, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12DepthStencilView(
            D3D12Device device,
            D3D12TextureResource texture,
            in DepthStencilViewDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, texture: texture);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12Sampler : Sampler, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12Sampler(
            D3D12Device device,
            in SamplerDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BindlessBufferCbv : BindlessBufferCbv, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BindlessBufferCbv(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferCbvDesc description,
            DescriptorLease descriptor,
            uint descriptorIndex)
            : base(device, description, descriptorIndex)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, buffer: buffer);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BindlessBufferSrv : BindlessBufferSrv, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BindlessBufferSrv(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferSrvDesc description,
            DescriptorLease descriptor,
            uint descriptorIndex)
            : base(device, description, descriptorIndex)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, buffer: buffer);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BindlessBufferUav : BindlessBufferUav, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BindlessBufferUav(
            D3D12Device device,
            D3D12Buffer buffer,
            in BufferUavDesc description,
            DescriptorLease descriptor,
            uint descriptorIndex)
            : base(device, description, descriptorIndex)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, buffer: buffer);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BindlessTextureSrv : BindlessTextureSrv, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BindlessTextureSrv(
            D3D12Device device,
            D3D12TextureResource texture,
            in TextureSrvDesc description,
            DescriptorLease descriptor,
            uint descriptorIndex)
            : base(device, description, descriptorIndex)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, texture: texture);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BindlessTextureUav : BindlessTextureUav, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BindlessTextureUav(
            D3D12Device device,
            D3D12TextureResource texture,
            in TextureUavDesc description,
            DescriptorLease descriptor,
            uint descriptorIndex)
            : base(device, description, descriptorIndex)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor, texture: texture);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }

    private sealed class D3D12BindlessSampler : BindlessSampler, INativeDescriptor
    {
        private readonly ViewLifetime _lifetime;

        internal D3D12BindlessSampler(
            D3D12Device device,
            in SamplerDesc description,
            DescriptorLease descriptor,
            uint descriptorIndex)
            : base(device, description, descriptorIndex)
        {
            NativeDescriptor = descriptor;
            _lifetime = new ViewLifetime(device, descriptor);
        }

        public DescriptorLease NativeDescriptor { get; }
        internal override void Release(bool fromParent) => _lifetime.Release(this);
    }
}
