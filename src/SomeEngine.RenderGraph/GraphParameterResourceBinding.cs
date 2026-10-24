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
        in TextureSubresourceRange secondaryTextureRange,
        bool requiresExplicitAccess = false,
        BufferContentScope bufferContentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered,
        VariableLayoutReflection? parameterLayout = null,
        byte[]? ordinaryData = null,
        GraphBufferInput? bufferInput = null, GraphParameterInput? parameterInput = null)
    {
        BufferInput = bufferInput;
        ParameterInput = parameterInput;
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
        RequiresExplicitAccess = requiresExplicitAccess;
        BufferContentScope = bufferContentScope;
        UnorderedAccessHazards = unorderedAccessHazards;
        ParameterLayout = parameterLayout ?? VariableLayoutReflection.Null;
        OrdinaryData = ordinaryData;
    }

    internal GraphParameterInput? ParameterInput { get; }
    internal GraphBufferInput? BufferInput { get; }
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
    internal bool RequiresExplicitAccess { get; }
    internal BufferContentScope BufferContentScope { get; }
    internal UnorderedAccessHazardMode UnorderedAccessHazards { get; }
    internal VariableLayoutReflection ParameterLayout { get; }
    internal byte[]? OrdinaryData { get; }
    internal bool IsBufferRange => Type == ResourceBindingType.ConstantBuffer && (BufferInput is not null || Value.Domain == GraphIdentityDomain.Buffer);
    internal bool BeginsParameterBlock => ParameterLayout != VariableLayoutReflection.Null;
    internal bool IsNull => Type != ResourceBindingType.None && !Value.IsValid && Resource is null && BufferInput is null && ParameterInput is null;

    internal static GraphParameterResourceBinding FromInput(GraphParameterInput input) =>
        new(input.Type, default, default, null, default, default, default,
            default, null, default, default, default, requiresExplicitAccess: true, parameterInput: input);

    public static GraphParameterResourceBinding Null(ResourceBindingType type)
    {
        DevelopmentCheck.Require(type != ResourceBindingType.None && Enum.IsDefined(type),
            "A null binding requires a resource binding type.");
        return new(type, default, default, null, default, default, default,
            default, null, default, default, default, requiresExplicitAccess: true);
    }

    internal static GraphParameterResourceBinding BeginBlock(
        VariableLayoutReflection layout, ReadOnlySpan<byte> ordinaryData)
    {
        DevelopmentCheck.Require(layout != VariableLayoutReflection.Null, "The parameter layout cannot be null.");
        return new(ResourceBindingType.None, default, default, null, default,
            default, default, default, null, default, default, default,
            requiresExplicitAccess: true, parameterLayout: layout,
            ordinaryData: ordinaryData.IsEmpty ? null : ordinaryData.ToArray());
    }

    public static GraphParameterResourceBinding ConstantBuffer(GraphBufferInput buffer, PipelineSync sync)
    {
        DevelopmentCheck.Require(buffer is not null, "A constant binding requires a buffer input.");
        return new(ResourceBindingType.ConstantBuffer, default, default, null,
            GraphAccessMode.Read, WriteCoverage.Partial, sync, TextureLayout.Undefined, null,
            BufferRange.Whole, default, default, bufferInput: buffer);
    }

    public static GraphParameterResourceBinding ConstantBuffer(GraphBufferSlice buffer, PipelineSync sync) =>
        new(ResourceBindingType.ConstantBuffer, buffer.Buffer.Value, default, null,
            GraphAccessMode.Read, WriteCoverage.Partial, sync, TextureLayout.Undefined, null,
            buffer.Range, default, default);

    public static GraphParameterResourceBinding ConstantBuffer(
        GraphBufferCbvId view,
        PipelineSync sync) =>
        ForGraphView(ResourceBindingType.ConstantBuffer, view.Value,
            GraphAccessMode.Read, WriteCoverage.Partial, sync,
            TextureLayout.Undefined, null);

    public static GraphParameterResourceBinding ReadOnlyBuffer(
        GraphBufferSrvId view,
        PipelineSync sync,
        BufferContentScope contentScope = BufferContentScope.FullRange) =>
        ForGraphView(ResourceBindingType.BufferSrv, view.Value,
            GraphAccessMode.Read, WriteCoverage.Partial, sync,
            TextureLayout.Undefined, null, bufferContentScope: contentScope);

    /// <summary>
    /// Binds a Buffer SRV descriptor whose accessible bytes come from the frame's buffer ranges
    /// or from ordinary range reads declared by the pass.
    /// </summary>
    public static GraphParameterResourceBinding ReadOnlyBufferDescriptor(
        GraphBufferSrvId view) =>
        ForGraphView(ResourceBindingType.BufferSrv, view.Value,
            GraphAccessMode.Read, WriteCoverage.Partial, PipelineSync.None,
            TextureLayout.Undefined, null, requiresExplicitAccess: true);

    /// <summary>Binds a Buffer UAV whose accessed ranges are declared by the pass.</summary>
    public static GraphParameterResourceBinding WritableBufferDescriptor(GraphBufferUavId view) =>
        ForGraphView(ResourceBindingType.BufferUav, view.Value,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial, PipelineSync.None,
            TextureLayout.Undefined, null, requiresExplicitAccess: true);

    public static GraphParameterResourceBinding WritableBuffer(
        GraphBufferUavId view,
        PipelineSync sync,
        GraphAccessMode mode = GraphAccessMode.ReadWrite,
        WriteCoverage coverage = WriteCoverage.Partial,
        ResourceContentState? resultContents = null,
        BufferContentScope contentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        ValidateStorageMode(mode);
        UnorderedAccessHazardRules.Validate(
            unorderedAccessHazards,
            mode,
            coverage,
            ResourceAccess.UnorderedAccess,
            TextureLayout.Undefined,
            resultContents);
        return ForGraphView(ResourceBindingType.BufferUav, view.Value,
            mode, coverage, sync, TextureLayout.Undefined, resultContents,
            bufferContentScope: contentScope,
            unorderedAccessHazards: unorderedAccessHazards);
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
        TextureLayout layout = TextureLayout.UnorderedAccess,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        ValidateStorageMode(mode);
        UnorderedAccessHazardRules.Validate(
            unorderedAccessHazards,
            mode,
            coverage,
            ResourceAccess.UnorderedAccess,
            layout,
            resultContents);
        return ForGraphView(ResourceBindingType.TextureUav, view.Value,
            mode, coverage, sync, layout, resultContents,
            unorderedAccessHazards: unorderedAccessHazards);
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
        ResourceContentState? resultContents,
        bool requiresExplicitAccess = false,
        BufferContentScope bufferContentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered) =>
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
            default,
            requiresExplicitAccess,
            bufferContentScope,
            unorderedAccessHazards);

    internal void ValidateStatic(GraphStructure structure, Device device)
    {
        if (BeginsParameterBlock || IsNull || ParameterInput is not null)
            return;
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

        if (BufferInput is not null)
        {
            if ((BufferInput.Usages & BufferUsages.Constant) == 0)
                throw new ArgumentException("A constant binding requires a Constant buffer input.");
            return;
        }
        if (IsBufferRange)
        {
            GraphBuffer buffer = structure.Buffers.Get(Value);
            _ = GraphStructureIndex.ResolveRange(BufferRange, buffer.Description.Size);
            if ((buffer.Description.Usages & BufferUsages.Constant) == 0)
                throw new ArgumentException("A constant binding requires a Constant buffer.");
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
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
    }

}
