using System.Diagnostics;

namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    internal Buffer GetBuffer(int pass, GraphBufferId id)
    {
        int index = ResolveBuffer(id.Value);
        EnsurePassUsesResource(pass, GraphAccessTargetKind.Buffer, index);
        Buffer? resource = _buffers[index].Resource;
        DevelopmentCheck.Require(resource is not null, "The Buffer was not materialized.");
        return resource!;
    }

    internal Texture GetTexture(int pass, GraphTextureId id)
    {
        int index = ResolveTexture(id.Value);
        EnsurePassUsesResource(pass, GraphAccessTargetKind.Texture, index);
        Texture? resource = _textures[index].Resource;
        DevelopmentCheck.Require(resource is not null, "The Texture was not materialized.");
        return resource!;
    }

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
        ValidateQueryPoolAccess(pass, resource, range, write);
        return _queryPools[resource].Resource;
    }

    [Conditional("DEBUG")]
    private void ValidateQueryPoolAccess(
        int pass,
        int resource,
        in QueryRange range,
        bool write)
    {
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind != GraphAccessTargetKind.QueryPool ||
                access.ResourceIndex != resource ||
                !Contains(access.QueryRange, range))
                continue;
            if (write && access.Mode == GraphAccessMode.Read) continue;
            if (!write && access.Mode == GraphAccessMode.Write) continue;
            return;
        }
        DevelopmentCheck.Fail(
            "RG8001: A Query command operand is not covered by the Pass declaration.");
    }

    internal RayTracingShaderTable GetRayTracingShaderTable(
        int pass,
        GraphRayTracingShaderTableId id,
        bool write)
    {
        int resource = ResolveShaderTable(id.Value);
        ValidateShaderTableAccess(pass, resource, write);
        return _shaderTables[resource].Resource;
    }

    [Conditional("DEBUG")]
    private void ValidateShaderTableAccess(int pass, int resource, bool write)
    {
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind != GraphAccessTargetKind.RayTracingShaderTable ||
                access.ResourceIndex != resource)
                continue;
            if (write && access.Mode == GraphAccessMode.Read) continue;
            if (!write && access.Mode == GraphAccessMode.Write) continue;
            return;
        }
        DevelopmentCheck.Fail(
            "RG8001: A RayTracingShaderTable command operand is not covered by the Pass declaration.");
    }

    internal PersistentParameterBindings GetPersistentParameterBindings(
        int pass,
        GraphPersistentParameterBindingsId id)
    {
        ValidatePersistentParameterBindings(pass, id.Value);
        return _frame.StructureIndex.Structure.PersistentBindings.Get(id.Value).Resource;
    }

    [Conditional("DEBUG")]
    private void ValidatePersistentParameterBindings(int pass, in GraphIdentity identity)
    {
        DevelopmentCheck.Require(
            _frame.StructureIndex.Structure.PersistentBindings.Contains(identity),
            "The PersistentParameterBindings identity is invalid or stale.");
        List<GraphIdentity>? allowed = _passes[pass].PersistentBindings;
        DevelopmentCheck.Require(
            allowed is not null && allowed.Contains(identity),
            "RG8001: The Pass did not declare these PersistentParameterBindings.");
    }

    private T GetView<T>(int pass, in GraphIdentity identity, GraphViewKind kind)
        where T : DeviceResource
    {
        bool found = TryResolveView(identity, out int index);
        DevelopmentCheck.Require(found, "The View identity is invalid or stale.");
        FrameView view = _views[index];
        DevelopmentCheck.Require(
            view.Kind == kind && view.View is T,
            "The View identity has the wrong type.");
        if (view.Buffer.IsValid)
            ValidateBuffer(pass, _buffers[ResolveBuffer(view.Buffer)].Resource!, view.BufferRange,
                kind is GraphViewKind.BufferUav);
        if (view.Texture.IsValid)
            ValidateTexture(pass, _textures[ResolveTexture(view.Texture)].Resource!, view.TextureRange,
                kind is GraphViewKind.TextureUav or GraphViewKind.ColorAttachment or GraphViewKind.DepthStencil);
        return (T)view.View!;
    }

    [Conditional("DEBUG")]
    internal void ValidateTextureCopyBuffer(int pass, in BufferTextureCopy copy, bool write)
    {
        TextureInfo info = copy.Texture.Info;
        var description = new TextureDesc(
            info.Dimension, info.Width, info.Height, info.Depth,
            info.MipLevelCount, info.ArrayLayerCount, info.SampleCount,
            info.Format, info.Usages, info.PermittedViewFormats);
        TextureCopyFootprint footprint = _frame.Backend.GetTextureCopyFootprint(
            copy.Texture.Device, description, copy, copy.BufferOffset);
        ValidateBuffer(pass, copy.Buffer, footprint.GetBufferRange(copy), write);
    }

    [Conditional("DEBUG")]
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
        DevelopmentCheck.Fail("RG8001: A Buffer command operand is not covered by the Pass declaration.");
    }

    [Conditional("DEBUG")]
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
        DevelopmentCheck.Fail("RG8001: A Texture command operand is not covered by the Pass declaration.");
    }

    [Conditional("DEBUG")]
    internal void ValidateBindings(int pass, ReadOnlySpan<ResourceBinding> bindings)
    {
        foreach (ref readonly ResourceBinding binding in bindings)
        {
            if (binding.IsNull) continue;
            switch (binding.Value)
            {
                case Buffer buffer when binding.Type == ResourceBindingType.ConstantBuffer:
                    ValidateBuffer(pass, buffer, binding.BufferRange, false);
                    break;
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
                    DevelopmentCheck.Fail("The ResourceBinding has an unknown resource type.");
                    break;
            }
        }
    }

    [Conditional("DEBUG")]
    internal void ValidateAccelerationStructure(
        int pass,
        AccelerationStructure accelerationStructure,
        bool write)
    {
        DevelopmentCheck.Require(
            accelerationStructure is not null,
            "The AccelerationStructure cannot be null.");
        ValidateBuffer(
            pass,
            accelerationStructure.Info.Storage,
            accelerationStructure.Info.StorageRange,
            write);
    }

    [Conditional("DEBUG")]
    internal void ValidateSamplerFeedback(
        int pass,
        SamplerFeedbackUav feedback,
        bool write)
    {
        DevelopmentCheck.Require(feedback is not null, "The SamplerFeedbackUav cannot be null.");
        ValidateTexture(
            pass,
            feedback.Description.Texture,
            feedback.Description.Range,
            write);
        ValidateTexture(pass, feedback.SampledTexture, range: null, write: false);
    }

    [Conditional("DEBUG")]
    internal void ValidateSamplerFeedbackSource(
        int pass,
        SamplerFeedbackTexture source)
    {
        DevelopmentCheck.Require(source is not null, "The SamplerFeedbackTexture cannot be null.");
        ValidateTexture(pass, source, range: null, write: false);
        ValidateTexture(pass, source.SampledTexture, range: null, write: false);
    }

    [Conditional("DEBUG")]
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

    [Conditional("DEBUG")]
    private void EnsurePassUsesResource(int pass, GraphAccessTargetKind kind, int resource)
    {
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (access.TargetKind == kind && access.ResourceIndex == resource) return;
        }
        DevelopmentCheck.Fail("RG8001: The Pass does not declare this resource.");
    }

    private int FindBuffer(Buffer buffer)
    {
        for (int i = 0; i < _buffers.Count; i++)
            if (ReferenceEquals(_buffers[i].Resource, buffer)) return i;
        DevelopmentCheck.Fail("The Buffer is not owned or imported by this frame.");
        return 0;
    }

    private int FindTexture(Texture texture)
    {
        for (int i = 0; i < _textures.Count; i++)
            if (ReferenceEquals(_textures[i].Resource, texture)) return i;
        DevelopmentCheck.Fail("The Texture is not owned or imported by this frame.");
        return 0;
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

