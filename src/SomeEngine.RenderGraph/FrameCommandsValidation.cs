namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    internal Buffer GetBuffer(int pass, GraphBufferId id)
    {
        int index = ResolveBuffer(id.Value);
        EnsurePassUsesResource(pass, GraphAccessTargetKind.Buffer, index);
        return _buffers[index].Resource
            ?? throw new InvalidOperationException("The Buffer was not materialized.");
    }

    internal Texture GetTexture(int pass, GraphTextureId id)
    {
        int index = ResolveTexture(id.Value);
        EnsurePassUsesResource(pass, GraphAccessTargetKind.Texture, index);
        return _textures[index].Resource
            ?? throw new InvalidOperationException("The Texture was not materialized.");
    }

    internal BufferCbv GetBufferCbv(int pass, GraphBufferCbvId id) =>
        GetView<BufferCbv>(pass, id.Value, GraphViewKind.BufferCbv);
    internal BufferSrv GetBufferSrv(int pass, GraphBufferSrvId id) =>
        GetView<BufferSrv>(pass, id.Value, GraphViewKind.BufferSrv);
    internal BufferUav GetBufferUav(int pass, GraphBufferUavId id) =>
        GetView<BufferUav>(pass, id.Value, GraphViewKind.BufferUav);
    internal TextureSrv GetTextureSrv(int pass, GraphTextureSrvId id) =>
        GetView<TextureSrv>(pass, id.Value, GraphViewKind.TextureSrv);
    internal TextureUav GetTextureUav(int pass, GraphTextureUavId id) =>
        GetView<TextureUav>(pass, id.Value, GraphViewKind.TextureUav);
    internal ColorAttachmentView GetColorAttachmentView(int pass, GraphColorAttachmentViewId id) =>
        GetView<ColorAttachmentView>(pass, id.Value, GraphViewKind.ColorAttachment);
    internal DepthStencilView GetDepthStencilView(int pass, GraphDepthStencilViewId id) =>
        GetView<DepthStencilView>(pass, id.Value, GraphViewKind.DepthStencil);

    internal QueryPool GetQueryPool(
        int pass,
        GraphQueryPoolId id,
        in QueryRange range,
        bool write)
    {
        int resource = ResolveQueryPool(id.Value);
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind != GraphAccessTargetKind.QueryPool ||
                access.ResourceIndex != resource ||
                !Contains(access.QueryRange, range))
                continue;
            if (write && access.Mode == GraphAccessMode.Read) continue;
            if (!write && access.Mode == GraphAccessMode.Write) continue;
            return _queryPools[resource].Resource;
        }
        throw new InvalidOperationException(
            "RG8001: A Query command operand is not covered by the Pass declaration.");
    }

    internal RayTracingShaderTable GetRayTracingShaderTable(
        int pass,
        GraphRayTracingShaderTableId id,
        bool write)
    {
        int resource = ResolveShaderTable(id.Value);
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind != GraphAccessTargetKind.RayTracingShaderTable ||
                access.ResourceIndex != resource)
                continue;
            if (write && access.Mode == GraphAccessMode.Read) continue;
            if (!write && access.Mode == GraphAccessMode.Write) continue;
            return _shaderTables[resource].Resource;
        }
        throw new InvalidOperationException(
            "RG8001: A RayTracingShaderTable command operand is not covered by the Pass declaration.");
    }

    internal PersistentParameterBindings GetPersistentParameterBindings(
        int pass,
        GraphPersistentParameterBindingsId id)
    {
        if (!_frame.Graph.StructureIndex.Structure.PersistentBindings.Contains(id.Value))
            throw new ArgumentException("The PersistentParameterBindings identity is invalid or stale.");
        List<GraphIdentity>? allowed = _passes[pass].PersistentBindings;
        if (allowed is null || !allowed.Contains(id.Value))
        {
            throw new InvalidOperationException(
                "RG8001: The Pass did not declare these PersistentParameterBindings.");
        }
        return _frame.Graph.StructureIndex.Structure.PersistentBindings.Get(id.Value).Resource;
    }

    private T GetView<T>(int pass, in GraphIdentity identity, GraphViewKind kind)
        where T : DeviceResource
    {
        if (!_viewIndices.TryGetValue(identity, out int index))
            throw new ArgumentException("The View identity is invalid or stale.");
        FrameView view = _views[index];
        if (view.Kind != kind || view.View is not T result)
            throw new ArgumentException("The View identity has the wrong type.");
        if (view.Buffer.IsValid)
            ValidateBuffer(pass, _buffers[ResolveBuffer(view.Buffer)].Resource!, view.BufferRange,
                kind is GraphViewKind.BufferUav);
        if (view.Texture.IsValid)
            ValidateTexture(pass, _textures[ResolveTexture(view.Texture)].Resource!, view.TextureRange,
                kind is GraphViewKind.TextureUav or GraphViewKind.ColorAttachment or GraphViewKind.DepthStencil);
        return result;
    }

    internal void ValidateBuffer(int pass, Buffer buffer, in BufferRange range, bool write)
    {
        int resource = FindBuffer(buffer);
        BufferRange normalized = GraphStructureIndex.ResolveRange(range, buffer.Info.Size);
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind != GraphAccessTargetKind.Buffer || access.ResourceIndex != resource)
                continue;
            if (!Contains(access.BufferRange, normalized)) continue;
            if (write
                    ? access.Mode == GraphAccessMode.Read
                    : access.Mode == GraphAccessMode.Write)
                continue;
            return;
        }
        throw new InvalidOperationException("RG8001: A Buffer command operand is not covered by the Pass declaration.");
    }

    internal void ValidateTexture(
        int pass,
        Texture texture,
        TextureSubresourceRange? range,
        bool write)
    {
        int resource = FindTexture(texture);
        TextureSubresourceRange requested = range ?? new TextureSubresourceRange(
            0,
            texture.Info.MipLevelCount,
            0,
            texture.Info.ArrayLayerCount,
            TextureFormatRules.Aspects(texture.Info.Format));
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind != GraphAccessTargetKind.Texture || access.ResourceIndex != resource)
                continue;
            if (!Contains(access.TextureRange, requested)) continue;
            if (write
                    ? access.Mode == GraphAccessMode.Read
                    : access.Mode == GraphAccessMode.Write)
                continue;
            return;
        }
        throw new InvalidOperationException("RG8001: A Texture command operand is not covered by the Pass declaration.");
    }

    internal void ValidateBindings(int pass, ReadOnlySpan<ResourceBinding> bindings)
    {
        foreach (ref readonly ResourceBinding binding in bindings)
        {
            if (binding.IsNull) continue;
            switch (binding.Value)
            {
                case BufferCbv view:
                    ValidateBuffer(pass, view.Description.Buffer, view.Description.Range, false);
                    break;
                case BufferSrv view:
                    ValidateBuffer(pass, view.Description.Buffer, view.Description.Range, false);
                    break;
                case BufferUav view:
                    ValidateBuffer(pass, view.Description.Buffer, view.Description.Range, true);
                    if (view.Description.CounterBuffer is not null)
                        ValidateBuffer(pass, view.Description.CounterBuffer,
                            new BufferRange(view.Description.CounterOffset, sizeof(uint)), true);
                    break;
                case TextureSrv view:
                    ValidateTexture(pass, view.Description.Texture, view.Description.Range, false);
                    break;
                case SamplerFeedbackUav view:
                    ValidateSamplerFeedback(pass, view, write: true);
                    break;
                case TextureUav view:
                    ValidateTexture(pass, view.Description.Texture, view.Description.Range, true);
                    break;
                case Sampler:
                    break;
                case AccelerationStructureSrv view:
                    ValidateAccelerationStructure(pass, view.Resource, write: false);
                    break;
                default:
                    throw new ArgumentException(
                        "The ResourceBinding has an unknown resource type.",
                        nameof(bindings));
            }
        }
    }

    internal void ValidateAccelerationStructure(
        int pass,
        AccelerationStructure accelerationStructure,
        bool write)
    {
        ArgumentNullException.ThrowIfNull(accelerationStructure);
        ValidateBuffer(
            pass,
            accelerationStructure.Info.Storage,
            accelerationStructure.Info.StorageRange,
            write);
    }

    internal void ValidateSamplerFeedback(
        int pass,
        SamplerFeedbackUav feedback,
        bool write)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ValidateTexture(
            pass,
            feedback.Description.Texture,
            feedback.Description.Range,
            write);
        ValidateTexture(pass, feedback.SampledTexture, range: null, write: false);
    }

    internal void ValidateSamplerFeedbackSource(
        int pass,
        SamplerFeedbackTexture source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateTexture(pass, source, range: null, write: false);
        ValidateTexture(pass, source.SampledTexture, range: null, write: false);
    }

    internal void ValidateAccelerationStructureBuild(
        int pass,
        in AccelerationStructureBuildDesc description)
    {
        ValidateAccelerationStructure(pass, description.Destination, write: true);
        ValidateBuffer(pass, description.Scratch, description.ScratchRange, write: true);
        if (description.Source is not null)
            ValidateAccelerationStructure(pass, description.Source, write: false);
        foreach (ref readonly AccelerationStructureGeometry geometry in description.Geometries)
        {
            ValidateRegion(geometry.Primary);
            ValidateRegion(geometry.Secondary);
            ValidateRegion(geometry.Transform);
        }
        return;

        void ValidateRegion(in BufferRegion region)
        {
            if (region.Buffer is null) return;
            ValidateBuffer(pass, region.Buffer, region.Range, write: false);
        }
    }

    private void EnsurePassUsesResource(int pass, GraphAccessTargetKind kind, int resource)
    {
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind == kind && access.ResourceIndex == resource) return;
        }
        throw new InvalidOperationException("RG8001: The Pass does not declare this resource.");
    }

    private int FindBuffer(Buffer buffer)
    {
        for (int i = 0; i < _buffers.Length; i++)
            if (ReferenceEquals(_buffers[i].Resource, buffer)) return i;
        throw new InvalidOperationException("The Buffer is not owned or imported by this frame.");
    }

    private int FindTexture(Texture texture)
    {
        for (int i = 0; i < _textures.Length; i++)
            if (ReferenceEquals(_textures[i].Resource, texture)) return i;
        throw new InvalidOperationException("The Texture is not owned or imported by this frame.");
    }

    private static bool Contains(in BufferRange container, in BufferRange value) =>
        value.Offset >= container.Offset &&
        value.Size <= container.Size - (value.Offset - container.Offset);

    private static bool Contains(in QueryRange container, in QueryRange value) =>
        value.FirstQuery >= container.FirstQuery &&
        value.QueryCount <= container.QueryCount -
            (value.FirstQuery - container.FirstQuery);

    private static bool Contains(
        in TextureSubresourceRange container,
        in TextureSubresourceRange value)
    {
        return value.FirstMipLevel >= container.FirstMipLevel &&
               value.MipLevelCount <= container.MipLevelCount -
                   (value.FirstMipLevel - container.FirstMipLevel) &&
               value.FirstArrayLayer >= container.FirstArrayLayer &&
               value.ArrayLayerCount <= container.ArrayLayerCount -
                   (value.FirstArrayLayer - container.FirstArrayLayer) &&
               (value.Aspects & ~container.Aspects) == 0;
    }
}

