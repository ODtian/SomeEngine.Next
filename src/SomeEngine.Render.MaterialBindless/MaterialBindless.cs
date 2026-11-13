using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render;

/// <summary>Material-local descriptor indices stored in one graph-managed value buffer.</summary>
public sealed class MaterialBindless : IDisposable
{
    private readonly RenderDevice _renderDevice;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly GraphBufferId _values;
    private readonly GraphPassId _upload;
    private readonly Dictionary<long, Dictionary<uint, TextureRequest>> _textureRequests = [];
    private MaterialData _data;
    private bool _disposed;

    public MaterialBindless(RenderDevice renderDevice, uint maximumValues)
    {
        _renderDevice = renderDevice;
        _backend = renderDevice.Backend;
        _device = renderDevice.Device;
        _data = new MaterialData(maximumValues);
        using RenderGraphEdit edit = renderDevice.Graph.BeginEdit();
        _values = edit.CreatePersistentBuffer(new((ulong)maximumValues * sizeof(uint),
            BufferUsages.CopyDestination | BufferUsages.ShaderRead, "Material descriptor values"));
        _upload = edit.AddBufferUploadPass(_values);
        edit.Commit();
    }

    public SomeEngine.Graphics.Buffer ValuesBuffer => _renderDevice.Graph.GetBuffer(_values);

    public void Prepare(Material material)
    {
        foreach (MaterialResourceBinding binding in ResolveMaterialBindings(material))
            if (binding.Kind is MaterialResourceKind.TextureSrv or MaterialResourceKind.TextureUav &&
                material.GetTexture(binding.MaterialProperty) is { } texture)
            {
                _renderDevice.PrepareTexture(texture);
                TextureRequest request = binding.TextureRequest;
                if (binding.Kind == MaterialResourceKind.TextureSrv)
                    _renderDevice.GetTextureSrv(texture, request.Range, request.Format, request.Dimension);
                else _renderDevice.GetTextureUav(texture, request.Range!.Value, request.Format, request.Dimension);
            }
    }

    public uint GetValueOffset(Material material, uint materialProperty)
    {
        foreach (MaterialResourceBinding binding in ResolveMaterialBindings(material))
            if (binding.MaterialProperty == materialProperty) return binding.ValueOffset;
        throw new ArgumentOutOfRangeException(nameof(materialProperty));
    }

    public void SetTextureSrv(Material material, uint materialProperty, in TextureSubresourceRange range,
        Format? format = null, TextureViewDimension? dimension = null) =>
        SetTextureRequest(material, materialProperty, new(MaterialResourceKind.TextureSrv, range, format, dimension));

    public void SetTextureUav(Material material, uint materialProperty, in TextureSubresourceRange range,
        Format? format = null, TextureViewDimension? dimension = null) =>
        SetTextureRequest(material, materialProperty, new(MaterialResourceKind.TextureUav, range, format, dimension));

    private void SetTextureRequest(Material material, uint materialProperty, in TextureRequest request)
    {
        if (!_textureRequests.TryGetValue(material.MaterialId, out var requests))
            _textureRequests.Add(material.MaterialId, requests = []);
        requests[materialProperty] = request;
    }

    public uint Use(ref RenderGraphFrame frame, ref PassDefinition pass, Material material, PipelineSync sync)
    {
        MaterialFrame data = GetFrame(ref frame);
        if (!data.Data.Materials.TryGetValue(material.MaterialId, out MaterialState? state))
        {
            MaterialResourceBinding[] bindings = ResolveMaterialBindings(material);
            uint start = data.Data.Ranges.Allocate((uint)bindings.Length);
            data.Data.Materials.Add(material.MaterialId, state = new(start, bindings));
        }
        for (int index = 0; index < state.Bindings.Length; index++)
        {
            MaterialResourceBinding binding = state.Bindings[index];
            DescriptorValue? current = state.Values[index];
            ResourceValue value = DescribeResource(ref frame, ref pass, material, binding, sync, current);
            if (current is not null && ReferenceEquals(current.Resource, value.Resource) && current.Revision == value.Revision)
                continue;
            DescriptorTable table = _backend.CreateDescriptorTable(_device, [value.Description], "Material descriptor");
            _backend.WriteDescriptor(table, 0, value.Binding);
            var descriptor = new DescriptorValue(value.Resource, value.Revision, table, value.Disposable);
            data.Created.Add(descriptor);
            if (current is not null) data.Retired.Add(current);
            state.Values[index] = descriptor;
            int offset = checked((int)(state.Base + binding.ValueOffset));
            data.Data.Values[offset] = _backend.GetDescriptorIndex(table, 0).Value;
            frame.WriteBuffer(_values, (ulong)offset * sizeof(uint), MemoryMarshal.AsBytes(data.Data.Values.AsSpan(offset, 1)));
        }
        if (data.Consumers.Add(pass.Id))
        {
            pass.Bind(data.View, sync);
            frame.OrderAfter(pass.Id, _upload);
        }
        return state.Base;
    }

    public void Upload(ref RenderGraphFrame frame) => GetFrame(ref frame);

    public void Remove(ref RenderGraphFrame frame, Material material)
    {
        MaterialFrame data = GetFrame(ref frame);
        if (!data.Data.Materials.Remove(material.MaterialId, out MaterialState? state)) return;
        data.Data.Ranges.Free(state.Base, (uint)state.Bindings.Length);
        foreach (DescriptorValue? value in state.Values) if (value is not null) data.Retired.Add(value);
    }

    private MaterialFrame GetFrame(ref RenderGraphFrame frame)
    {
        MaterialFrame data = frame.GetFrameData(this, () => new MaterialFrame(this, _data.Copy()));
        if (data.View == default)
        {
            data.View = frame.CreateBufferSrv(_values, structureStride: sizeof(uint));
            if (!_data.Initialized)
                frame.WriteBuffer(_values, 0, MemoryMarshal.AsBytes(data.Data.Values.AsSpan()));
            frame.ObserveExecution(data);
            frame.RetireAfterFrame(data);
        }
        return data;
    }

    private ResourceValue DescribeResource(ref RenderGraphFrame frame, ref PassDefinition pass,
        Material material, in MaterialResourceBinding binding, PipelineSync sync, DescriptorValue? current)
    {
        uint property = binding.MaterialProperty;
        ulong revision = material.GetPropertyRevision(property);
        if (binding.Kind is MaterialResourceKind.TextureSrv or MaterialResourceKind.TextureUav)
        {
            EngineTexture? asset = material.GetTexture(property);
            SomeEngine.Graphics.Texture texture = asset is null
                ? _renderDevice.PrepareMissingTexture(ref frame, pass.Id)
                : _renderDevice.PrepareTexture(ref frame, pass.Id, asset);
            GraphTextureId id = frame.GetImported(texture);
            TextureRequest request = binding.TextureRequest;
            if (binding.Kind == MaterialResourceKind.TextureSrv)
            {
                TextureSrv view = asset is null ? _renderDevice.MissingTextureSrv :
                    _renderDevice.GetTextureSrv(asset, request.Range, request.Format, request.Dimension);
                TextureSrvDesc desc = view.Description;
                pass.Read(frame.CreateTextureSrv(id, desc.Range, desc.Format, desc.Dimension), sync, TextureLayout.ShaderResource);
                return new(view, revision, new(ResourceBindingType.TextureSrv, desc.Format,
                    TextureDimension: desc.Dimension, Aspects: desc.Range.Aspects), ResourceBinding.SampledTexture(view), null);
            }
            TextureUav writable = _renderDevice.GetTextureUav(asset!, request.Range!.Value, request.Format, request.Dimension);
            TextureUavDesc output = writable.Description;
            pass.ReadWrite(frame.CreateTextureUav(id, output.Range, output.Format, output.Dimension), sync,
                layout: TextureLayout.UnorderedAccess);
            return new(writable, revision, new(ResourceBindingType.TextureUav, output.Format,
                TextureDimension: output.Dimension, Aspects: output.Range.Aspects), ResourceBinding.StorageTexture(writable), null);
        }
        if (binding.Kind == MaterialResourceKind.Sampler)
        {
            SamplerDesc desc = material.GetSampler(property)!;
            if (current is not null && current.Revision == revision)
                return new(current.Resource, revision, new(ResourceBindingType.Sampler), default, null);
            Sampler sampler = _backend.CreateSampler(_device, desc);
            return new(sampler, revision, new(ResourceBindingType.Sampler), ResourceBinding.SampledWith(sampler), sampler);
        }
        DeviceResource resource = material.GetDeviceResource(property)!;
        switch (resource)
        {
            case BufferSrv view:
            {
                BufferSrvDesc desc = view.Description;
                pass.Read(frame.GetImported(desc.Buffer), desc.Range, sync, ResourceAccess.ShaderResource);
                return new(view, revision, new(ResourceBindingType.BufferSrv, desc.Format, desc.StructureStride),
                    ResourceBinding.ReadOnlyBuffer(view), null);
            }
            case BufferUav view:
            {
                BufferUavDesc desc = view.Description;
                pass.ReadWrite(frame.GetImported(desc.Buffer), desc.Range, sync, ResourceAccess.UnorderedAccess);
                if (desc.CounterBuffer is { } counter)
                    pass.ReadWrite(frame.GetImported(counter), new(desc.CounterOffset, sizeof(uint)), sync, ResourceAccess.UnorderedAccess);
                return new(view, revision, new(ResourceBindingType.BufferUav, desc.Format, desc.StructureStride,
                    HasCounter: desc.CounterBuffer is not null), ResourceBinding.WritableBuffer(view), null);
            }
            case BufferCbv view:
                pass.Read(frame.GetImported(view.Description.Buffer), view.Description.Range, sync, ResourceAccess.ConstantBuffer);
                return new(view, revision, new(ResourceBindingType.ConstantBuffer), ResourceBinding.ConstantBuffer(view), null);
            case AccelerationStructureSrv view:
                pass.Read(frame.GetImported(view.Resource.Info.Storage), view.Resource.Info.StorageRange,
                    sync, ResourceAccess.RayTracingAccelerationStructureRead);
                return new(view, revision, new(ResourceBindingType.AccelerationStructure), ResourceBinding.AccelerationStructure(view), null);
            default: throw new NotSupportedException();
        }
    }

    private MaterialResourceBinding[] ResolveMaterialBindings(Material material)
    {
        IReadOnlyList<MaterialPropertyDefinition> definitions = material.Properties;
        var result = new List<MaterialResourceBinding>(definitions.Count);
        _textureRequests.TryGetValue(material.MaterialId, out Dictionary<uint, TextureRequest>? textureRequests);
        for (uint property = 0; property < definitions.Count; property++)
        {
            Type type = definitions[checked((int)property)].ValueType;
            TextureRequest textureRequest = default;
            MaterialResourceKind? kind =
                typeof(SomeEngine.Resources.Texture).IsAssignableFrom(type)
                    ? ResolveTextureKind(property, textureRequests, out textureRequest) :
                type == typeof(SamplerDesc) ? MaterialResourceKind.Sampler :
                typeof(BufferSrv).IsAssignableFrom(type) ? MaterialResourceKind.BufferSrv :
                typeof(BufferUav).IsAssignableFrom(type) ? MaterialResourceKind.BufferUav :
                typeof(BufferCbv).IsAssignableFrom(type) ? MaterialResourceKind.ConstantBuffer :
                typeof(AccelerationStructureSrv).IsAssignableFrom(type) ? MaterialResourceKind.AccelerationStructure :
                null;
            if (kind is { } resolved)
            {
                result.Add(new MaterialResourceBinding(
                    property,
                    checked((uint)result.Count),
                    resolved,
                    textureRequest));
            }
        }
        return [.. result];

        static MaterialResourceKind ResolveTextureKind(
            uint property,
            Dictionary<uint, TextureRequest>? requests,
            out TextureRequest request)
        {
            if (requests is not null && requests.TryGetValue(property, out request))
                return request.Kind;
            request = new TextureRequest(MaterialResourceKind.TextureSrv, null, null, null);
            return MaterialResourceKind.TextureSrv;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (MaterialState state in _data.Materials.Values)
            foreach (DescriptorValue? value in state.Values) if (value is not null) _renderDevice.Graph.RetireAfterSubmittedFrames(value);
        if (!_renderDevice.Graph.IsDisposed && !_renderDevice.Graph.IsLost)
        {
            using RenderGraphEdit edit = _renderDevice.Graph.BeginEdit();
            edit.Remove(_upload);
            edit.Remove(_values);
            edit.Commit();
        }
        _data.Materials.Clear();
        _textureRequests.Clear();
    }

    private sealed class MaterialFrame(MaterialBindless feature, MaterialData data) : IRenderGraphFrameExecutionObserver, IDisposable
    {
        internal MaterialData Data { get; } = data;
        internal GraphBufferSrvId View;
        internal HashSet<GraphPassId> Consumers { get; } = [];
        internal List<DescriptorValue> Created { get; } = [];
        internal List<DescriptorValue> Retired { get; } = [];
        private bool _submitted;
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            Data.Initialized = true;
            feature._data = Data;
            _submitted = true;
            foreach (DescriptorValue value in Retired) feature._renderDevice.Graph.RetireAfterSubmittedFrames(value);
        }
        public void Dispose() { if (!_submitted) foreach (DescriptorValue value in Created) value.Dispose(); }
    }

    private sealed class MaterialData
    {
        internal MaterialData(uint count) { Values = new uint[count]; Ranges = new(count); }
        private MaterialData(MaterialData source)
        {
            Values = (uint[])source.Values.Clone();
            Ranges = source.Ranges.Copy();
            Initialized = source.Initialized;
            foreach (var pair in source.Materials) Materials.Add(pair.Key, pair.Value.Copy());
        }
        internal uint[] Values { get; }
        internal RangeAllocator Ranges { get; }
        internal Dictionary<long, MaterialState> Materials { get; } = [];
        internal bool Initialized;
        internal MaterialData Copy() => new(this);
    }

    private sealed class MaterialState(uint start, MaterialResourceBinding[] bindings)
    {
        internal uint Base { get; } = start;
        internal MaterialResourceBinding[] Bindings { get; } = bindings;
        internal DescriptorValue?[] Values { get; } = new DescriptorValue?[bindings.Length];
        internal MaterialState Copy()
        {
            var copy = new MaterialState(Base, Bindings);
            Values.CopyTo(copy.Values, 0);
            return copy;
        }
    }
    private sealed record DescriptorValue(object Resource, ulong Revision, DescriptorTable Table, IDisposable? Additional) : IDisposable
    {
        public void Dispose() { Table.Dispose(); Additional?.Dispose(); }
    }
    private readonly record struct ResourceValue(object Resource, ulong Revision, DescriptorEntryDesc Description,
        ResourceBinding Binding, IDisposable? Disposable);
    private enum MaterialResourceKind : byte { TextureSrv, TextureUav, Sampler, BufferSrv, BufferUav, ConstantBuffer, AccelerationStructure }
    private readonly record struct MaterialResourceBinding(uint MaterialProperty, uint ValueOffset,
        MaterialResourceKind Kind, TextureRequest TextureRequest);
    private readonly record struct TextureRequest(MaterialResourceKind Kind, TextureSubresourceRange? Range,
        Format? Format, TextureViewDimension? Dimension);
}
