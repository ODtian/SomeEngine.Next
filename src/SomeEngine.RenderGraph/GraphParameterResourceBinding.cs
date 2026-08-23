namespace SomeEngine.RenderGraph;

/// <summary>
/// Defines one shader parameter binding together with the exact resources that make
/// the binding legal for a pass. It owns neither the graph identities nor the RHI object.
/// </summary>
public readonly struct GraphParameterResourceBinding
{
    private GraphParameterResourceBinding(
        ResourceBindingType type,
        in GraphIdentity value,
        in GraphIdentity secondaryValue,
        DeviceResource? resource,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        TextureLayout layout,
        ResourceContentState? resultContents,
        in BufferRange bufferRange,
        in TextureSubresourceRange textureRange,
        in TextureSubresourceRange secondaryTextureRange)
    {
        Type = type;
        Value = value;
        SecondaryValue = secondaryValue;
        Resource = resource;
        Mode = mode;
        Coverage = coverage;
        Sync = sync;
        Layout = layout;
        ResultContents = resultContents;
        BufferRange = bufferRange;
        TextureRange = textureRange;
        SecondaryTextureRange = secondaryTextureRange;
    }

    internal ResourceBindingType Type { get; }
    internal GraphIdentity Value { get; }
    internal GraphIdentity SecondaryValue { get; }
    internal DeviceResource? Resource { get; }
    internal Sampler? Sampler => Resource as Sampler;
    internal AccelerationStructureSrv? AccelerationStructureSrv => Resource as AccelerationStructureSrv;
    internal SamplerFeedbackUav? SamplerFeedbackUav => Resource as SamplerFeedbackUav;
    internal GraphAccessMode Mode { get; }
    internal WriteCoverage Coverage { get; }
    internal PipelineSync Sync { get; }
    internal TextureLayout Layout { get; }
    internal ResourceContentState? ResultContents { get; }
    internal BufferRange BufferRange { get; }
    internal TextureSubresourceRange TextureRange { get; }
    internal TextureSubresourceRange SecondaryTextureRange { get; }

    public static GraphParameterResourceBinding ConstantBuffer(
        GraphBufferCbvId view,
        PipelineSync sync) =>
        ForGraphView(ResourceBindingType.ConstantBuffer, view.Value,
            GraphAccessMode.Read, WriteCoverage.Partial, sync,
            TextureLayout.Undefined, null);

    public static GraphParameterResourceBinding ReadOnlyBuffer(
        GraphBufferSrvId view,
        PipelineSync sync) =>
        ForGraphView(ResourceBindingType.BufferSrv, view.Value,
            GraphAccessMode.Read, WriteCoverage.Partial, sync,
            TextureLayout.Undefined, null);

    public static GraphParameterResourceBinding WritableBuffer(
        GraphBufferUavId view,
        PipelineSync sync,
        GraphAccessMode mode = GraphAccessMode.ReadWrite,
        WriteCoverage coverage = WriteCoverage.Partial,
        ResourceContentState? resultContents = null)
    {
        ValidateStorageMode(mode);
        return ForGraphView(ResourceBindingType.BufferUav, view.Value,
            mode, coverage, sync, TextureLayout.Undefined, resultContents);
    }

    public static GraphParameterResourceBinding SampledTexture(
        GraphTextureSrvId view,
        PipelineSync sync,
        TextureLayout layout = TextureLayout.ShaderResource) =>
        ForGraphView(ResourceBindingType.TextureSrv, view.Value,
            GraphAccessMode.Read, WriteCoverage.Partial, sync, layout, null);

    public static GraphParameterResourceBinding StorageTexture(
        GraphTextureUavId view,
        PipelineSync sync,
        GraphAccessMode mode = GraphAccessMode.ReadWrite,
        WriteCoverage coverage = WriteCoverage.Partial,
        ResourceContentState? resultContents = null,
        TextureLayout layout = TextureLayout.UnorderedAccess)
    {
        ValidateStorageMode(mode);
        return ForGraphView(ResourceBindingType.TextureUav, view.Value,
            mode, coverage, sync, layout, resultContents);
    }

    public static GraphParameterResourceBinding SampledWith(Sampler sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        return new GraphParameterResourceBinding(
            ResourceBindingType.Sampler,
            default,
            default,
            sampler,
            GraphAccessMode.Read,
            WriteCoverage.Partial,
            PipelineSync.None,
            TextureLayout.Undefined,
            null,
            default,
            default,
            default);
    }

    /// <summary>
    /// Defines an acceleration-structure SRV and the exact storage range which owns
    /// its synchronization and lifetime.
    /// </summary>
    public static GraphParameterResourceBinding AccelerationStructure(
        AccelerationStructureSrv view,
        GraphBufferId storage,
        in BufferRange storageRange,
        PipelineSync sync)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new GraphParameterResourceBinding(
            ResourceBindingType.AccelerationStructure,
            storage.Value,
            default,
            view,
            GraphAccessMode.Read,
            WriteCoverage.Partial,
            sync,
            TextureLayout.Undefined,
            null,
            storageRange,
            default,
            default);
    }

    /// <summary>
    /// Defines a sampler-feedback UAV. The feedback texture is written while the
    /// sampled texture is read by the same shader binding.
    /// </summary>
    public static GraphParameterResourceBinding SamplerFeedback(
        SamplerFeedbackUav view,
        GraphTextureId feedbackTexture,
        in TextureSubresourceRange feedbackRange,
        GraphTextureId sampledTexture,
        in TextureSubresourceRange sampledRange,
        PipelineSync sync,
        WriteCoverage coverage = WriteCoverage.Partial,
        ResourceContentState? resultContents = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new GraphParameterResourceBinding(
            ResourceBindingType.TextureUav,
            feedbackTexture.Value,
            sampledTexture.Value,
            view,
            GraphAccessMode.Write,
            coverage,
            sync,
            TextureLayout.UnorderedAccess,
            resultContents,
            default,
            feedbackRange,
            sampledRange);
    }

    private static GraphParameterResourceBinding ForGraphView(
        ResourceBindingType type,
        in GraphIdentity view,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        TextureLayout layout,
        ResourceContentState? resultContents) =>
        new(
            type,
            view,
            default,
            null,
            mode,
            coverage,
            sync,
            layout,
            resultContents,
            default,
            default,
            default);

    internal void ValidateStatic(GraphStructure structure, Device device)
    {
        if (Type == ResourceBindingType.Sampler)
        {
            Sampler sampler = Sampler ??
                throw new ArgumentException("A sampler binding has no Sampler.");
            if (!ReferenceEquals(sampler.Device, device))
                throw new ArgumentException("The Sampler belongs to another Device.");
            return;
        }

        if (Type == ResourceBindingType.AccelerationStructure)
        {
            AccelerationStructureSrv view = AccelerationStructureSrv ??
                throw new ArgumentException("An acceleration-structure binding has no SRV.");
            if (!ReferenceEquals(view.Device, device))
                throw new ArgumentException("The AccelerationStructureSrv belongs to another Device.");
            GraphBuffer storage = structure.Buffers.Get(Value);
            BufferRange range = GraphStructureIndex.ResolveRange(BufferRange, storage.Description.Size);
            Buffer? storageResource = storage.RegisteredResource ?? storage.PersistentResource;
            if (!ReferenceEquals(storageResource, view.Resource.Info.Storage) ||
                range != view.Resource.Info.StorageRange)
            {
                throw new ArgumentException(
                    "The acceleration-structure binding does not match its registered Graph Buffer range.");
            }
            return;
        }

        if (Type == ResourceBindingType.TextureUav && SecondaryValue.IsValid)
        {
            SamplerFeedbackUav view = SamplerFeedbackUav ??
                throw new ArgumentException("A sampler-feedback binding has no UAV.");
            if (!ReferenceEquals(view.Device, device))
                throw new ArgumentException("The SamplerFeedbackUav belongs to another Device.");
            GraphTexture feedback = structure.Textures.Get(Value);
            GraphTexture sampled = structure.Textures.Get(SecondaryValue);
            GraphStructureIndex.ValidateTextureRange(feedback, TextureRange);
            GraphStructureIndex.ValidateTextureRange(sampled, SecondaryTextureRange);
            Texture? feedbackResource = feedback.RegisteredResource ?? feedback.PersistentResource;
            Texture? sampledResource = sampled.RegisteredResource ?? sampled.PersistentResource;
            if (!ReferenceEquals(feedbackResource, view.Description.Texture) ||
                !ReferenceEquals(sampledResource, view.SampledTexture) ||
                TextureRange != view.Description.Range)
            {
                throw new ArgumentException(
                    "The sampler-feedback binding does not match its registered Graph Textures or UAV range.");
            }
            return;
        }

        GraphView graphView = structure.Views.Get(Value);
        bool valid = Type switch
        {
            ResourceBindingType.ConstantBuffer => graphView.Kind == GraphViewKind.BufferCbv,
            ResourceBindingType.BufferSrv => graphView.Kind == GraphViewKind.BufferSrv,
            ResourceBindingType.BufferUav => graphView.Kind == GraphViewKind.BufferUav,
            ResourceBindingType.TextureSrv => graphView.Kind == GraphViewKind.TextureSrv,
            ResourceBindingType.TextureUav => graphView.Kind == GraphViewKind.TextureUav,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("The parameter binding type does not match its Graph View.");
    }

    private static void ValidateStorageMode(GraphAccessMode mode)
    {
        if (mode is not (GraphAccessMode.Write or GraphAccessMode.ReadWrite))
            throw new ArgumentOutOfRangeException(nameof(mode));
    }
}
