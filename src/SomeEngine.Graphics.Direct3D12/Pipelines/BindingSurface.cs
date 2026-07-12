using Vortice.Direct3D12;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;
using GraphicsFormat = SomeEngine.Graphics.Format;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    private TextureViewHandle CreateTextureViewCore(in TextureViewDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeTexture texture = GetTexture(desc.Texture);
        ValidatedTextureViewDescription validated = TextureViewValidation.Validate(
            texture.Desc,
            desc.Range,
            desc.Usage,
            desc.Format,
            desc.Dimension);
        GraphicsFormat format = validated.Format;
        TextureViewDimension dimension = validated.Dimension;
        ValidatedTextureViewRange range = new(
            validated.Range.FirstMip,
            validated.Range.MipCount,
            validated.Range.FirstLayer,
            validated.Range.LayerCount,
            validated.Range.Aspect);

        NativeCpuDescriptor? renderTarget = null;
        NativeCpuDescriptor? shaderResource = null;
        NativeCpuDescriptor? storage = null;
        NativeCpuDescriptor?[]? depthStencil = null;
        bool childAdded = false;
        try
        {
            if ((desc.Usage & TextureViewUsage.ColorAttachment) != 0)
            {
                renderTarget = CreateCpuDescriptor(DescriptorHeapType.RenderTargetView, destination =>
                {
                    RenderTargetViewDescription nativeDesc = CreateRenderTargetViewDescription(texture.Desc, format, range, dimension);
                    _native.Device.CreateRenderTargetView(texture.Resource, nativeDesc, destination);
                });
            }
            if ((desc.Usage & TextureViewUsage.ShaderResource) != 0)
            {
                shaderResource = CreateCpuDescriptor(
                    DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    destination =>
                    {
                        ShaderResourceViewDescription nativeDesc = CreateTextureShaderResourceView(texture.Desc, format, range, dimension);
                        _native.Device.CreateShaderResourceView(texture.Resource, nativeDesc, destination);
                    });
            }
            if ((desc.Usage & TextureViewUsage.Storage) != 0)
            {
                storage = CreateCpuDescriptor(
                    DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    destination =>
                    {
                        UnorderedAccessViewDescription nativeDesc = CreateTextureUnorderedAccessView(texture.Desc, format, range, dimension);
                        _native.Device.CreateUnorderedAccessView(texture.Resource, null, nativeDesc, destination);
                    });
            }
            if ((desc.Usage & TextureViewUsage.DepthStencilAttachment) != 0)
            {
                depthStencil = new NativeCpuDescriptor?[4];
                int descriptorCount = format == GraphicsFormat.D24UNormS8UInt ? 4 : 2;
                for (int flagsIndex = 0; flagsIndex < descriptorCount; flagsIndex++)
                {
                    if (format == GraphicsFormat.D32Float && (flagsIndex & 2) != 0) continue;
                    DepthStencilViewFlags flags = DepthStencilViewFlags.None;
                    if ((flagsIndex & 1) != 0) flags |= DepthStencilViewFlags.ReadOnlyDepth;
                    if ((flagsIndex & 2) != 0) flags |= DepthStencilViewFlags.ReadOnlyStencil;
                    DepthStencilViewFlags frozenFlags = flags;
                    depthStencil[flagsIndex] = CreateCpuDescriptor(DescriptorHeapType.DepthStencilView, destination =>
                    {
                        DepthStencilViewDescription nativeDesc = CreateDepthStencilViewDescription(
                            texture.Desc,
                            format,
                            range,
                            dimension,
                            frozenFlags);
                        _native.Device.CreateDepthStencilView(texture.Resource, nativeDesc, destination);
                    });
                }
            }

            texture.AddView();
            childAdded = true;
            uint[] attachmentSubresources = (desc.Usage &
                (TextureViewUsage.ColorAttachment | TextureViewUsage.DepthStencilAttachment)) != 0
                ? BuildSubresourceList(texture.Desc, range)
                : [];
            NativeTextureView native = new(
                texture,
                format,
                range,
                dimension,
                desc.Usage,
                renderTarget,
                shaderResource,
                storage,
                depthStencil,
                attachmentSubresources);
            ApplyLogicalName(native, desc.Name);
            HandleKey key = _textureViews.Add(native);
            renderTarget = null;
            shaderResource = null;
            storage = null;
            depthStencil = null;
            return new TextureViewHandle(_domain, key.Slot, key.Generation);
        }
        catch
        {
            if (childAdded) texture.RemoveView();
            if (depthStencil is not null)
            {
                foreach (NativeCpuDescriptor? descriptor in depthStencil) descriptor?.Dispose();
            }
            storage?.Dispose();
            shaderResource?.Dispose();
            renderTarget?.Dispose();
            throw;
        }
    }

    private BufferViewHandle CreateBufferViewCore(in BufferViewDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBuffer buffer = GetBuffer(desc.Buffer);
        ResolveBufferViewRange(buffer.Desc, desc.Range, out ulong offset, out ulong size);
        ValidateBufferView(buffer.Desc, desc, offset, size);
        BufferViewDesc frozenDesc = desc;

        NativeCpuDescriptor? descriptor = null;
        bool childAdded = false;
        try
        {
            descriptor = CreateCpuDescriptor(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                destination => CreateBufferDescriptor(buffer, frozenDesc, offset, size, destination));
            buffer.AddView();
            childAdded = true;
            NativeBufferView native = new(buffer, frozenDesc, offset, size, descriptor);
            ApplyLogicalName(native, desc.Name);
            HandleKey key = _bufferViews.Add(native);
            descriptor = null;
            return new BufferViewHandle(_domain, key.Slot, key.Generation);
        }
        catch
        {
            if (childAdded) buffer.RemoveView();
            descriptor?.Dispose();
            throw;
        }
    }

    private SamplerHandle CreateSamplerCore(in SamplerDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        SamplerDesc frozenDesc = desc;
        NativeCpuDescriptor descriptor = CreateCpuDescriptor(DescriptorHeapType.Sampler, destination =>
        {
            SamplerDescription nativeDesc = new(
                SamplerFilter(frozenDesc),
                Address(frozenDesc.AddressU),
                Address(frozenDesc.AddressV),
                Address(frozenDesc.AddressW),
                0f,
                1,
                ComparisonFunction.Always,
                new Color4(0f, 0f, 0f, 0f),
                0f,
                float.MaxValue);
            _native.Device.CreateSampler(ref nativeDesc, destination);
        });
        NativeSampler native = new(frozenDesc, descriptor);
        ApplyLogicalName(native, desc.Name);
        HandleKey key = _samplers.Add(native);
        return new SamplerHandle(_domain, key.Slot, key.Generation);
    }

    private void DestroyBufferViewCore(BufferViewHandle view)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBufferView native = GetBufferView(view);
        if (native.BindingCount != 0) throw new InvalidOperationException("A buffer view cannot be destroyed while bind groups reference it.");
        RetirementPoint point = BeginRetirement(native);
        _ = _bufferViews.Remove(view.Domain, view.Slot, view.Generation, "buffer view");
        native.ReleaseBuffer();
        ScheduleRetirement(native, point);
    }

    private void DestroySamplerCore(SamplerHandle sampler)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeSampler native = GetSampler(sampler);
        if (native.BindingCount != 0) throw new InvalidOperationException("A sampler cannot be destroyed while bind groups reference it.");
        RetirementPoint point = BeginRetirement(native);
        _ = _samplers.Remove(sampler.Domain, sampler.Slot, sampler.Generation, "sampler");
        ScheduleRetirement(native, point);
    }

    private BindGroupLayoutHandle CreateBindGroupLayoutCore(ReadOnlySpan<BindingDesc> bindings)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        BindingDesc[] frozen = bindings.ToArray();
        ValidateBindingLayout(frozen);
        NativeBindGroupLayout native = new(frozen);
        HandleKey key = _bindGroupLayouts.Add(native);
        return new BindGroupLayoutHandle(_domain, key.Slot, key.Generation);
    }

    private BindGroupHandle CreateBindGroupCore(
        BindGroupLayoutHandle layoutHandle,
        ReadOnlySpan<BindingWrite> writes,
        string? name)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBindGroupLayout layout = GetBindGroupLayout(layoutHandle);
        FrozenBinding[] frozen = ValidateAndFreezeBindingWrites(layout, writes);
        bool dependenciesAdded = false;
        try
        {
            layout.AddChild();
            foreach (FrozenBinding binding in frozen) binding.Dependency.AddBinding();
            dependenciesAdded = true;
            NativeBindGroup native = new(layout, frozen, name);
            ApplyLogicalName(native, name);
            HandleKey key = _bindGroups.Add(native);
            return new BindGroupHandle(_domain, key.Slot, key.Generation);
        }
        catch
        {
            if (dependenciesAdded)
            {
                foreach (FrozenBinding binding in frozen) binding.Dependency.RemoveBinding();
                layout.RemoveChild();
            }
            throw;
        }
    }

    private void DestroyBindGroupLayoutCore(BindGroupLayoutHandle layout)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBindGroupLayout native = GetBindGroupLayout(layout);
        if (native.ChildCount != 0)
            throw new InvalidOperationException("A bind-group layout cannot be destroyed while bind groups or pipeline layouts reference it.");
        RetirementPoint point = BeginRetirement(native);
        _ = _bindGroupLayouts.Remove(layout.Domain, layout.Slot, layout.Generation, "bind-group layout");
        ScheduleRetirement(native, point);
    }

    private void DestroyBindGroupCore(BindGroupHandle group)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBindGroup native = GetBindGroup(group);
        RetirementPoint point = BeginRetirement(native);
        _ = _bindGroups.Remove(group.Domain, group.Slot, group.Generation, "bind group");
        native.ReleaseDependencies();
        ScheduleRetirement(native, point);
    }

    internal NativeBufferView GetBufferView(BufferViewHandle handle) =>
        _bufferViews.Get(handle.Domain, handle.Slot, handle.Generation, "buffer view");

    internal NativeSampler GetSampler(SamplerHandle handle) =>
        _samplers.Get(handle.Domain, handle.Slot, handle.Generation, "sampler");

    internal NativeBindGroupLayout GetBindGroupLayout(BindGroupLayoutHandle handle) =>
        _bindGroupLayouts.Get(handle.Domain, handle.Slot, handle.Generation, "bind-group layout");

    internal NativeBindGroup GetBindGroup(BindGroupHandle handle) =>
        _bindGroups.Get(handle.Domain, handle.Slot, handle.Generation, "bind group");

    internal FrozenBinding[] ValidateAndFreezeBindings(
        BindGroupLayoutHandle layout,
        ReadOnlySpan<BindingWrite> writes) =>
        ValidateAndFreezeBindingWrites(GetBindGroupLayout(layout), writes);

    private FrozenBinding[] ValidateAndFreezeBindingWrites(
        NativeBindGroupLayout layout,
        ReadOnlySpan<BindingWrite> writes)
    {
        if (writes.Length != layout.DescriptorCount)
            throw new ArgumentException("Every bind-group descriptor array element must be supplied exactly once.", nameof(writes));
        FrozenBinding[] frozen = new FrozenBinding[writes.Length];
        HashSet<(uint Binding, uint Element)> seen = [];
        for (int index = 0; index < writes.Length; index++)
        {
            BindingWrite write = writes[index];
            BindingSlot slot = layout.Find(write.Binding);
            if (write.Element >= slot.Description.Count) throw new ArgumentOutOfRangeException(nameof(writes));
            if (!seen.Add((write.Binding, write.Element))) throw new ArgumentException("Duplicate binding write.", nameof(writes));
            frozen[index] = FreezeBinding(slot, write);
        }
        return frozen;
    }

    private FrozenBinding FreezeBinding(in BindingSlot slot, in BindingWrite write)
    {
        NativeDescriptorDependency dependency;
        NativeCpuDescriptor descriptor;
        switch (slot.Description.Kind)
        {
            case BindingKind.SampledTexture:
            case BindingKind.StorageTexture:
            {
                if (write.ValueKind != BindingValueKind.TextureView)
                    throw new ArgumentException("A texture binding requires a texture view.");
                NativeTextureView view = GetTextureView(write.TextureView);
                descriptor = view.GetBindingDescriptor(slot.Description.Kind);
                dependency = view;
                break;
            }
            case BindingKind.ConstantBuffer:
            case BindingKind.ReadOnlyBuffer:
            case BindingKind.StorageBuffer:
            {
                if (write.ValueKind != BindingValueKind.BufferView)
                    throw new ArgumentException("A buffer binding requires a buffer view.");
                NativeBufferView view = GetBufferView(write.BufferView);
                if (view.Description.Kind != slot.Description.Kind)
                    throw new ArgumentException($"Buffer-view kind {view.Description.Kind} does not match binding kind {slot.Description.Kind}.");
                descriptor = view.Descriptor;
                dependency = view;
                break;
            }
            case BindingKind.Sampler:
            {
                if (write.ValueKind != BindingValueKind.Sampler)
                    throw new ArgumentException("A sampler binding requires a sampler.");
                NativeSampler sampler = GetSampler(write.Sampler);
                descriptor = sampler.Descriptor;
                dependency = sampler;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(slot));
        }
        return new FrozenBinding(
            write.Binding,
            write.Element,
            slot.Description.Kind,
            slot.DescriptorOffset + checked((int)write.Element),
            descriptor,
            dependency);
    }

    private NativeCpuDescriptor CreateCpuDescriptor(
        DescriptorHeapType type,
        Action<CpuDescriptorHandle> create)
    {
        NativeCpuDescriptor descriptor = _cpuDescriptors.Allocate(type);
        try
        {
            create(descriptor.Handle);
            return descriptor;
        }
        catch
        {
            descriptor.Dispose();
            throw;
        }
    }

    private void CreateBufferDescriptor(
        NativeBuffer buffer,
        in BufferViewDesc desc,
        ulong offset,
        ulong size,
        CpuDescriptorHandle destination)
    {
        if (desc.Kind == BindingKind.ConstantBuffer)
        {
            ConstantBufferViewDescription cbv = new(buffer.Resource.GPUVirtualAddress + offset, checked((uint)size));
            _native.Device.CreateConstantBufferView(cbv, destination);
            return;
        }

        bool raw = desc.Format == GraphicsFormat.Unknown && desc.Stride == 0;
        uint elementSize = raw ? 4u : desc.Stride != 0 ? desc.Stride : FormatSize(desc.Format);
        ulong firstElement = offset / elementSize;
        uint elementCount = checked((uint)(size / elementSize));
        DxgiFormat nativeFormat = raw ? DxgiFormat.R32_Typeless : desc.Stride != 0 ? DxgiFormat.Unknown : Mappings.Format(desc.Format);
        if (desc.Kind == BindingKind.ReadOnlyBuffer)
        {
            ShaderResourceViewDescription srv = new()
            {
                Format = nativeFormat,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping = 5768,
                Buffer = new BufferShaderResourceView
                {
                    FirstElement = firstElement,
                    NumElements = elementCount,
                    StructureByteStride = desc.Stride,
                    Flags = raw ? BufferShaderResourceViewFlags.Raw : BufferShaderResourceViewFlags.None,
                },
            };
            _native.Device.CreateShaderResourceView(buffer.Resource, srv, destination);
            return;
        }

        UnorderedAccessViewDescription uav = new()
        {
            Format = nativeFormat,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView
            {
                FirstElement = firstElement,
                NumElements = elementCount,
                StructureByteStride = desc.Stride,
                CounterOffsetInBytes = 0,
                Flags = raw ? BufferUnorderedAccessViewFlags.Raw : BufferUnorderedAccessViewFlags.None,
            },
        };
        _native.Device.CreateUnorderedAccessView(buffer.Resource, null, uav, destination);
    }

    private static void ValidateBufferView(
        in BufferDesc buffer,
        in BufferViewDesc view,
        ulong offset,
        ulong size)
    {
        BufferUsage required = view.Kind switch
        {
            BindingKind.ConstantBuffer => BufferUsage.Constant,
            BindingKind.ReadOnlyBuffer => BufferUsage.ShaderRead,
            BindingKind.StorageBuffer => BufferUsage.ShaderWrite,
            _ => throw new ArgumentException($"Binding kind {view.Kind} cannot describe a buffer view.", nameof(view)),
        };
        if ((buffer.Usage & required) == 0) throw new ArgumentException($"The buffer is missing {required} usage.", nameof(view));
        if (view.Kind == BindingKind.ConstantBuffer)
        {
            if (view.Format != GraphicsFormat.Unknown || view.Stride != 0 || (offset & 255) != 0 || (size & 255) != 0 || size > 65_536)
                throw new ArgumentException("A D3D12 constant-buffer view requires an unknown format, zero stride, 256-byte aligned range, and at most 64 KiB.", nameof(view));
            return;
        }
        if (view.Format != GraphicsFormat.Unknown && view.Stride != 0)
            throw new ArgumentException("A buffer view cannot be both typed and structured.", nameof(view));
        uint elementSize = view.Format != GraphicsFormat.Unknown ? FormatSize(view.Format) : view.Stride != 0 ? view.Stride : 4u;
        if (IsDepthFormat(view.Format) || offset % elementSize != 0 || size % elementSize != 0)
            throw new ArgumentException("The buffer range must contain whole elements of a supported non-depth format or stride.", nameof(view));
        if (size / elementSize > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(view), "D3D12 buffer views cannot exceed uint.MaxValue elements.");
    }

    private static void ResolveBufferViewRange(
        in BufferDesc buffer,
        in BufferRange range,
        out ulong offset,
        out ulong size)
    {
        offset = range.Offset;
        if (offset >= buffer.Size) throw new ArgumentOutOfRangeException(nameof(range));
        size = range.Size is 0 or ulong.MaxValue ? buffer.Size - offset : range.Size;
        ValidateRange(buffer.Size, offset, size);
    }

    private static void ValidateBindingLayout(BindingDesc[] bindings)
    {
        HashSet<uint> numbers = [];
        foreach (BindingDesc binding in bindings)
        {
            if (!Enum.IsDefined(binding.Kind) || binding.Count == 0 || binding.Visibility == 0 ||
                (binding.Visibility & ~(ShaderStage.Vertex | ShaderStage.Pixel | ShaderStage.Compute)) != 0)
                throw new ArgumentException("A bind-group layout entry is invalid.", nameof(bindings));
            if (!numbers.Add(binding.Binding)) throw new ArgumentException($"Binding {binding.Binding} is duplicated.", nameof(bindings));
        }
    }

    private static ShaderResourceViewDescription CreateTextureShaderResourceView(
        in TextureDesc texture,
        GraphicsFormat format,
        in ValidatedTextureViewRange range,
        TextureViewDimension dimension)
    {
        ShaderResourceViewDescription result = new()
        {
            Format = Mappings.ShaderViewFormat(format, range.Aspect),
            Shader4ComponentMapping = 5768,
        };
        uint planeSlice = range.Aspect == TextureAspect.Stencil ? 1u : 0u;
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                result.ViewDimension = ShaderResourceViewDimension.Texture1D;
                result.Texture1D = new Texture1DShaderResourceView
                {
                    MostDetailedMip = checked((uint)range.FirstMip),
                    MipLevels = checked((uint)range.MipCount),
                    ResourceMinLODClamp = 0f,
                };
                break;
            case TextureViewDimension.Texture1DArray:
                result.ViewDimension = ShaderResourceViewDimension.Texture1DArray;
                result.Texture1DArray = new Texture1DArrayShaderResourceView
                {
                    MostDetailedMip = checked((uint)range.FirstMip),
                    MipLevels = checked((uint)range.MipCount),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                    ResourceMinLODClamp = 0f,
                };
                break;
            case TextureViewDimension.Texture2D:
                result.ViewDimension = ShaderResourceViewDimension.Texture2D;
                result.Texture2D = new Texture2DShaderResourceView
                {
                    MostDetailedMip = checked((uint)range.FirstMip),
                    MipLevels = checked((uint)range.MipCount),
                    PlaneSlice = planeSlice,
                    ResourceMinLODClamp = 0f,
                };
                break;
            case TextureViewDimension.Texture2DArray:
                result.ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                result.Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = checked((uint)range.FirstMip),
                    MipLevels = checked((uint)range.MipCount),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                    PlaneSlice = planeSlice,
                    ResourceMinLODClamp = 0f,
                };
                break;
            case TextureViewDimension.Texture2DMS:
                result.ViewDimension = ShaderResourceViewDimension.Texture2DMultisampled;
                result.Texture2DMS = new Texture2DMultisampledShaderResourceView();
                break;
            case TextureViewDimension.Texture2DMSArray:
                result.ViewDimension = ShaderResourceViewDimension.Texture2DMultisampledArray;
                result.Texture2DMSArray = new Texture2DMultisampledArrayShaderResourceView
                {
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                };
                break;
            case TextureViewDimension.Cube:
                result.ViewDimension = ShaderResourceViewDimension.TextureCube;
                result.TextureCube = new TextureCubeShaderResourceView
                {
                    MostDetailedMip = checked((uint)range.FirstMip),
                    MipLevels = checked((uint)range.MipCount),
                    ResourceMinLODClamp = 0f,
                };
                break;
            case TextureViewDimension.CubeArray:
                result.ViewDimension = ShaderResourceViewDimension.TextureCubeArray;
                result.TextureCubeArray = new TextureCubeArrayShaderResourceView
                {
                    MostDetailedMip = checked((uint)range.FirstMip),
                    MipLevels = checked((uint)range.MipCount),
                    First2DArrayFace = checked((uint)range.FirstLayer),
                    NumCubes = checked((uint)(range.LayerCount / 6)),
                    ResourceMinLODClamp = 0f,
                };
                break;
            case TextureViewDimension.Texture3D:
                result.ViewDimension = ShaderResourceViewDimension.Texture3D;
                result.Texture3D = new Texture3DShaderResourceView
                {
                    MostDetailedMip = checked((uint)range.FirstMip),
                    MipLevels = checked((uint)range.MipCount),
                    ResourceMinLODClamp = 0f,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(dimension));
        }
        return result;
    }

    private static UnorderedAccessViewDescription CreateTextureUnorderedAccessView(
        in TextureDesc texture,
        GraphicsFormat format,
        in ValidatedTextureViewRange range,
        TextureViewDimension dimension)
    {
        UnorderedAccessViewDescription result = new() { Format = Mappings.Format(format) };
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                result.ViewDimension = UnorderedAccessViewDimension.Texture1D;
                result.Texture1D = new Texture1DUnorderedAccessView
                {
                    MipSlice = checked((uint)range.FirstMip),
                };
                break;
            case TextureViewDimension.Texture1DArray:
                result.ViewDimension = UnorderedAccessViewDimension.Texture1DArray;
                result.Texture1DArray = new Texture1DArrayUnorderedAccessView
                {
                    MipSlice = checked((uint)range.FirstMip),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                };
                break;
            case TextureViewDimension.Texture2D:
                result.ViewDimension = UnorderedAccessViewDimension.Texture2D;
                result.Texture2D = new Texture2DUnorderedAccessView
                {
                    MipSlice = checked((uint)range.FirstMip),
                    PlaneSlice = 0,
                };
                break;
            case TextureViewDimension.Texture2DArray:
                result.ViewDimension = UnorderedAccessViewDimension.Texture2DArray;
                result.Texture2DArray = new Texture2DArrayUnorderedAccessView
                {
                    MipSlice = checked((uint)range.FirstMip),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                    PlaneSlice = 0,
                };
                break;
            case TextureViewDimension.Texture3D:
                result.ViewDimension = UnorderedAccessViewDimension.Texture3D;
                result.Texture3D = new Texture3DUnorderedAccessView
                {
                    MipSlice = checked((uint)range.FirstMip),
                    FirstWSlice = 0,
                    WSize = checked((uint)Math.Max(1, texture.Depth >> range.FirstMip)),
                };
                break;
            default:
                throw new ArgumentException($"View dimension {dimension} cannot describe a D3D12 unordered-access view.", nameof(dimension));
        }
        return result;
    }

    private static Filter SamplerFilter(in SamplerDesc desc) => (desc.MinFilter, desc.MagFilter, desc.MipFilter) switch
    {
        (FilterMode.Nearest, FilterMode.Nearest, FilterMode.Nearest) => Filter.MinMagMipPoint,
        (FilterMode.Nearest, FilterMode.Nearest, FilterMode.Linear) => Filter.MinMagPointMipLinear,
        (FilterMode.Nearest, FilterMode.Linear, FilterMode.Nearest) => Filter.MinPointMagLinearMipPoint,
        (FilterMode.Nearest, FilterMode.Linear, FilterMode.Linear) => Filter.MinPointMagMipLinear,
        (FilterMode.Linear, FilterMode.Nearest, FilterMode.Nearest) => Filter.MinLinearMagMipPoint,
        (FilterMode.Linear, FilterMode.Nearest, FilterMode.Linear) => Filter.MinLinearMagPointMipLinear,
        (FilterMode.Linear, FilterMode.Linear, FilterMode.Nearest) => Filter.MinMagLinearMipPoint,
        _ => Filter.MinMagMipLinear,
    };

    private static TextureAddressMode Address(AddressMode mode) => mode switch
    {
        AddressMode.Repeat => TextureAddressMode.Wrap,
        AddressMode.Mirror => TextureAddressMode.Mirror,
        AddressMode.Clamp => TextureAddressMode.Clamp,
        AddressMode.Border => TextureAddressMode.Border,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}

internal abstract class NativeDescriptorDependency : NativeLifetime
{
    private int _bindings;
    public int BindingCount => Volatile.Read(ref _bindings);
    public void AddBinding() => Interlocked.Increment(ref _bindings);
    public void RemoveBinding()
    {
        if (Interlocked.Decrement(ref _bindings) < 0) throw new InvalidOperationException("Descriptor binding count underflow.");
    }
}

internal sealed class NativeBufferView : NativeDescriptorDependency
{
    private int _bufferReleased;
    public NativeBufferView(NativeBuffer buffer, BufferViewDesc description, ulong offset, ulong size, NativeCpuDescriptor descriptor)
    {
        Buffer = buffer;
        Description = description;
        Offset = offset;
        Size = size;
        Descriptor = descriptor;
    }
    public NativeBuffer Buffer { get; }
    public BufferViewDesc Description { get; }
    public ulong Offset { get; }
    public ulong Size { get; }
    public NativeCpuDescriptor Descriptor { get; }
    public void ReleaseBuffer()
    {
        if (Interlocked.Exchange(ref _bufferReleased, 1) == 0) Buffer.RemoveView();
    }
    protected override void DisposeNative() => Descriptor.Dispose();
}

internal sealed class NativeSampler : NativeDescriptorDependency
{
    public NativeSampler(SamplerDesc description, NativeCpuDescriptor descriptor)
    {
        Description = description;
        Descriptor = descriptor;
    }
    public SamplerDesc Description { get; }
    public NativeCpuDescriptor Descriptor { get; }
    protected override void DisposeNative() => Descriptor.Dispose();
}

internal readonly record struct BindingSlot(BindingDesc Description, int DescriptorOffset, DescriptorHeapType HeapType);

internal sealed class NativeBindGroupLayout : NativeLifetime
{
    private readonly Dictionary<uint, BindingSlot> _slots;
    private int _children;

    public NativeBindGroupLayout(BindingDesc[] bindings)
    {
        Bindings = bindings;
        _slots = new(bindings.Length);
        int resources = 0;
        int samplers = 0;
        foreach (BindingDesc binding in bindings.OrderBy(static value => value.Binding))
        {
            bool sampler = binding.Kind == BindingKind.Sampler;
            int offset = sampler ? samplers : resources;
            _slots.Add(binding.Binding, new BindingSlot(
                binding,
                offset,
                sampler ? DescriptorHeapType.Sampler : DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
            if (sampler) samplers = checked(samplers + (int)binding.Count);
            else resources = checked(resources + (int)binding.Count);
        }
        ResourceDescriptorCount = resources;
        SamplerDescriptorCount = samplers;
        DescriptorCount = checked(resources + samplers);
    }

    public BindingDesc[] Bindings { get; }
    public int ResourceDescriptorCount { get; }
    public int SamplerDescriptorCount { get; }
    public int DescriptorCount { get; }
    public int ChildCount => Volatile.Read(ref _children);
    public BindingSlot Find(uint binding) => _slots.TryGetValue(binding, out BindingSlot value)
        ? value
        : throw new ArgumentException($"Binding {binding} is absent from the bind-group layout.");
    public void AddChild() => Interlocked.Increment(ref _children);
    public void RemoveChild()
    {
        if (Interlocked.Decrement(ref _children) < 0) throw new InvalidOperationException("Bind-group-layout child count underflow.");
    }
    protected override void DisposeNative() { }
}

internal readonly record struct FrozenBinding(
    uint Binding,
    uint Element,
    BindingKind Kind,
    int DescriptorOffset,
    NativeCpuDescriptor Descriptor,
    NativeDescriptorDependency Dependency);

internal sealed class NativeBindGroup : NativeLifetime
{
    private int _dependenciesReleased;
    public NativeBindGroup(NativeBindGroupLayout layout, FrozenBinding[] bindings, string? name)
    {
        Layout = layout;
        Bindings = bindings;
        Name = name;
    }
    public NativeBindGroupLayout Layout { get; }
    public FrozenBinding[] Bindings { get; }
    public string? Name { get; }
    public void ReleaseDependencies()
    {
        if (Interlocked.Exchange(ref _dependenciesReleased, 1) != 0) return;
        foreach (FrozenBinding binding in Bindings) binding.Dependency.RemoveBinding();
        Layout.RemoveChild();
    }
    protected override void DisposeNative() { }
}
