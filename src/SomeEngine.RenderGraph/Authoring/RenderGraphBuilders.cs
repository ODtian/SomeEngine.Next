using System.Numerics;

namespace SomeEngine.RenderGraph;

public delegate void BaseRenderFunc<PassData, ContextType>(
    PassData data,
    ContextType renderGraphContext)
    where PassData : class, new()
    where ContextType : allows ref struct;

public interface IBaseRenderGraphBuilder : IDisposable
{
    void UseBuffer(
        in BufferHandle input,
        GraphResourceUsage state,
        GraphAccess flags = GraphAccess.Read,
        BufferRange? range = null);

    void UseTexture(
        in TextureHandle input,
        GraphResourceUsage state,
        GraphAccess flags = GraphAccess.Read,
        TextureSubresourceRange? range = null);

    void UseBuffer(in BufferViewHandle input, GraphAccess flags = GraphAccess.Read);
    void UseTexture(in TextureViewHandle input, GraphAccess flags = GraphAccess.Read);
    void UseBuffer(
        in BufferViewHandle input,
        in DescriptorTableHandle descriptorTable,
        GraphAccess flags = GraphAccess.Read);
    void UseTexture(
        in TextureViewHandle input,
        in DescriptorTableHandle descriptorTable,
        GraphAccess flags = GraphAccess.Read);
    void UseAccelerationStructure(in AccelerationStructureHandle input);
    void UseSampler(in SamplerHandle input);
    void UseQuery(in QueryPoolHandle input);
    void SetPipeline(Pipeline pipeline);
    void SetParameterBlock(
        VariableLayoutReflection layout,
        ReadOnlySpan<byte> ordinaryData = default);
    void EnableAsyncCompute(bool value);
    void AllowPassCulling(bool value);
    void AllowGlobalStateModification(bool value);
}

public interface IRenderAttachmentRenderGraphBuilder : IBaseRenderGraphBuilder
{
    void SetRenderAttachment(
        TextureViewHandle texture,
        int index,
        GraphAccess flags = GraphAccess.Write,
        LoadType load = LoadType.Load,
        Vector4 clearColor = default,
        TextureViewHandle resolveTexture = default,
        ResolveType resolveMode = ResolveType.Average);

    void SetRenderAttachmentDepth(
        TextureViewHandle texture,
        GraphAccess flags = GraphAccess.ReadWrite,
        LoadType depthLoad = LoadType.Load,
        LoadType stencilLoad = LoadType.Load,
        float clearDepth = 1f,
        byte clearStencil = 0);
}

public interface IUnsafeRenderGraphBuilder : IRenderAttachmentRenderGraphBuilder
{
    void SetRenderFunc<PassData>(
        BaseRenderFunc<PassData, UnsafeGraphContext> renderFunc)
        where PassData : class, new();
}

public interface IRasterRenderGraphBuilder : IRenderAttachmentRenderGraphBuilder
{
    void SetRenderFunc<PassData>(
        BaseRenderFunc<PassData, UnsafeGraphContext> renderFunc)
        where PassData : class, new();
}

public interface IComputeRenderGraphBuilder : IBaseRenderGraphBuilder
{
    void SetRenderFunc<PassData>(
        BaseRenderFunc<PassData, UnsafeGraphContext> renderFunc)
        where PassData : class, new();
}

internal delegate void PassExecutor(ref UnsafeGraphContext context);

internal sealed class RenderGraphBuilders :
    IUnsafeRenderGraphBuilder,
    IRasterRenderGraphBuilder,
    IComputeRenderGraphBuilder
{
    private RenderGraph? _graph;
    private readonly object _passData;
    private readonly int _pass;
    private readonly PassRollbackMarker _rollback;

    internal RenderGraphBuilders(
        RenderGraph graph,
        object passData,
        int pass,
        in PassRollbackMarker rollback)
    {
        _graph = graph;
        _passData = passData;
        _pass = pass;
        _rollback = rollback;
    }

    public void UseBuffer(
        in BufferHandle input,
        GraphResourceUsage state,
        GraphAccess flags = GraphAccess.Read,
        BufferRange? range = null)
    {
        try
        {
            Graph.UseBuffer(_pass, input, state, flags, range);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseTexture(
        in TextureHandle input,
        GraphResourceUsage state,
        GraphAccess flags = GraphAccess.Read,
        TextureSubresourceRange? range = null)
    {
        try
        {
            Graph.UseTexture(_pass, input, state, flags, range);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseBuffer(
        in BufferViewHandle input,
        GraphAccess flags = GraphAccess.Read)
    {
        try
        {
            Graph.UseBuffer(_pass, input, flags, shaderArgument: true);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseTexture(
        in TextureViewHandle input,
        GraphAccess flags = GraphAccess.Read)
    {
        try
        {
            Graph.UseTexture(_pass, input, flags, shaderArgument: true);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseBuffer(
        in BufferViewHandle input,
        in DescriptorTableHandle descriptorTable,
        GraphAccess flags = GraphAccess.Read)
    {
        try
        {
            Graph.UseBuffer(_pass, input, descriptorTable, flags);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseTexture(
        in TextureViewHandle input,
        in DescriptorTableHandle descriptorTable,
        GraphAccess flags = GraphAccess.Read)
    {
        try
        {
            Graph.UseTexture(_pass, input, descriptorTable, flags);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseAccelerationStructure(in AccelerationStructureHandle input)
    {
        try
        {
            Graph.UseAccelerationStructure(_pass, input, shaderArgument: true);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseSampler(in SamplerHandle input)
    {
        try
        {
            Graph.UseSampler(_pass, input);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void UseQuery(in QueryPoolHandle input)
    {
        try
        {
            Graph.UseQuery(_pass, input);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void SetPipeline(Pipeline pipeline)
    {
        try
        {
            Graph.SetPipeline(_pass, pipeline);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void SetParameterBlock(
        VariableLayoutReflection layout,
        ReadOnlySpan<byte> ordinaryData = default)
    {
        try
        {
            Graph.SetParameterBlock(_pass, layout, ordinaryData);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void EnableAsyncCompute(bool value)
    {
        try
        {
            Graph.EnableAsyncCompute(_pass, value);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void AllowPassCulling(bool value)
    {
        try
        {
            Graph.AllowPassCulling(_pass, value);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void AllowGlobalStateModification(bool value)
    {
        try
        {
            Graph.AllowGlobalStateModification(_pass, value);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void SetRenderAttachment(
        TextureViewHandle texture,
        int index,
        GraphAccess flags = GraphAccess.Write,
        LoadType load = LoadType.Load,
        Vector4 clearColor = default,
        TextureViewHandle resolveTexture = default,
        ResolveType resolveMode = ResolveType.Average)
    {
        try
        {
            Graph.SetRenderAttachment(
                _pass,
                texture,
                index,
                flags,
                load,
                clearColor,
                resolveTexture,
                resolveMode);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void SetRenderAttachmentDepth(
        TextureViewHandle texture,
        GraphAccess flags = GraphAccess.ReadWrite,
        LoadType depthLoad = LoadType.Load,
        LoadType stencilLoad = LoadType.Load,
        float clearDepth = 1f,
        byte clearStencil = 0)
    {
        try
        {
            Graph.SetRenderAttachmentDepth(
                _pass,
                texture,
                flags,
                depthLoad,
                stencilLoad,
                clearDepth,
                clearStencil);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void SetRenderFunc<PassData>(
        BaseRenderFunc<PassData, UnsafeGraphContext> renderFunc)
        where PassData : class, new()
    {
        try
        {
            ArgumentNullException.ThrowIfNull(renderFunc);
            if (_passData is not PassData data)
                throw new ArgumentException(
                    $"The render function expects {typeof(PassData).FullName}, " +
                    $"but this pass owns {_passData.GetType().FullName}.",
                    nameof(renderFunc));
            Graph.SetRenderFunc(_pass, data, renderFunc);
        }
        catch
        {
            Abort();
            throw;
        }
    }

    public void Dispose()
    {
        RenderGraph? graph = Interlocked.Exchange(ref _graph, null);
        if (graph is null) return;
        graph.EndBuilderPass(_pass, in _rollback);
    }

    private RenderGraph Graph =>
        Volatile.Read(ref _graph) ??
        throw new ObjectDisposedException(nameof(RenderGraphBuilders));

    private void Abort()
    {
        RenderGraph? graph = Interlocked.Exchange(ref _graph, null);
        if (graph is not null)
            graph.AbortBuilderPass(_pass, in _rollback);
    }
}

public sealed partial class RenderGraph
{
    public IUnsafeRenderGraphBuilder AddUnsafePass<PassData>(
        string name,
        out PassData passData,
        QueueType queue = QueueType.Graphics,
        PassFlags flags = PassFlags.None)
        where PassData : class, new() =>
        AddBuilderPass(name, queue, flags, out passData);

    public IRasterRenderGraphBuilder AddRasterRenderPass<PassData>(
        string name,
        out PassData passData,
        PassFlags flags = PassFlags.None)
        where PassData : class, new() =>
        AddBuilderPass(name, QueueType.Graphics, flags, out passData);

    public IComputeRenderGraphBuilder AddComputePass<PassData>(
        string name,
        out PassData passData,
        PassFlags flags = PassFlags.None)
        where PassData : class, new() =>
        AddBuilderPass(name, QueueType.Graphics, flags, out passData);

    private RenderGraphBuilders AddBuilderPass<PassData>(
        string name,
        QueueType queue,
        PassFlags flags,
        out PassData passData)
        where PassData : class, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        PassRollbackMarker rollback = BeginPassRollbackMarker();
        int pass = BeginPass(name, queue, flags);
        try
        {
            BeginBuilderDeclarations(pass);
            passData = new PassData();
            return new RenderGraphBuilders(this, passData, pass, in rollback);
        }
        catch
        {
            RollbackPass(pass, in rollback);
            throw;
        }
    }

    internal void UseBuffer(
        int pass,
        BufferHandle input,
        GraphResourceUsage state,
        GraphAccess flags,
        BufferRange? range)
    {
        RequireBuilderPass(pass);
        DeclareBufferAccess(pass, input, state, flags, range);
    }

    internal void UseTexture(
        int pass,
        TextureHandle input,
        GraphResourceUsage state,
        GraphAccess flags,
        TextureSubresourceRange? range)
    {
        RequireBuilderPass(pass);
        DeclareTextureAccess(pass, input, state, flags, range);
    }

    internal void UseBuffer(
        int pass,
        BufferViewHandle input,
        GraphAccess flags,
        bool shaderArgument)
    {
        RequireBuilderPass(pass);
        if (shaderArgument && GetPassPipeline(pass) is not null)
            DeclareShaderArgument(pass, input, flags);
        else
            _ = DeclareBufferViewAccess(pass, input, flags);
    }

    internal void UseTexture(
        int pass,
        TextureViewHandle input,
        GraphAccess flags,
        bool shaderArgument)
    {
        RequireBuilderPass(pass);
        if (shaderArgument && GetPassPipeline(pass) is not null)
            DeclareShaderArgument(pass, input, flags);
        else
            _ = DeclareTextureViewAccess(pass, input, flags);
    }

    internal void UseBuffer(
        int pass,
        BufferViewHandle input,
        DescriptorTableHandle descriptorTable,
        GraphAccess flags)
    {
        RequireBuilderPass(pass);
        int view = ValidateBufferView(input);
        int access = DeclareBufferViewAccess(pass, input, flags);
        _ = AddBindlessAccess(
            pass,
            descriptorTable,
            GetBufferViewType(view),
            access,
            view);
    }

    internal void UseTexture(
        int pass,
        TextureViewHandle input,
        DescriptorTableHandle descriptorTable,
        GraphAccess flags)
    {
        RequireBuilderPass(pass);
        int view = ValidateTextureView(input);
        int access = DeclareTextureViewAccess(pass, input, flags);
        _ = AddBindlessAccess(
            pass,
            descriptorTable,
            TextureBindingKind(view),
            access,
            view);
    }

    internal void UseAccelerationStructure(
        int pass,
        AccelerationStructureHandle input,
        bool shaderArgument)
    {
        RequireBuilderPass(pass);
        if (shaderArgument && GetPassPipeline(pass) is not null)
            DeclareShaderArgument(pass, input);
        else
        {
            int accessOrdinal = AddAccelerationStructureAccess(pass, input);
            MarkViewMaterialization(ref GetPass(pass), accessOrdinal);
        }
    }

    internal void UseSampler(int pass, SamplerHandle input)
    {
        RequireBuilderPass(pass);
        _ = GetPassPipeline(pass) ??
            throw new InvalidOperationException("A sampler declaration requires SetPipeline.");
        DeclareShaderArgument(pass, input);
    }

    internal void UseQuery(int pass, QueryPoolHandle input)
    {
        RequireBuilderPass(pass);
        AddQueryPool(pass, input);
    }

    internal void SetPipeline(int pass, Pipeline pipeline)
    {
        RequireBuilderPass(pass);
        ArgumentNullException.ThrowIfNull(pipeline);
        if (GetPassPipeline(pass) is not null)
            throw new InvalidOperationException("A pass pipeline can be assigned only once.");
        ValidatePipelineOwner(pass, pipeline);
        EnsurePassPipelineColumn();
        _passPipelines[pass] = pipeline;
    }

    internal void SetParameterBlock(
        int pass,
        VariableLayoutReflection layout,
        ReadOnlySpan<byte> ordinaryData)
    {
        RequireBuilderPass(pass);
        if (GetPassPipeline(pass) is null)
            throw new InvalidOperationException("A parameter block requires SetPipeline.");
        if (layout == VariableLayoutReflection.Null)
            throw new ArgumentException("The Slang parameter layout cannot be null.", nameof(layout));
        if (_parameterLayouts[pass] != VariableLayoutReflection.Null)
            throw new InvalidOperationException("A pass parameter block can be assigned only once.");
        _parameterLayouts[pass] = layout;
        _parameterOrdinaryData[pass] = ordinaryData.ToArray();
    }

    internal void AllowPassCulling(int pass, bool value)
    {
        RequireBuilderPass(pass);
        ref PassData data = ref GetPass(pass);
        if (value)
            data.Flags &= ~PassFlags.NeverCull;
        else
            data.Flags |= PassFlags.NeverCull;
    }

    internal void EnableAsyncCompute(int pass, bool value)
    {
        RequireBuilderPass(pass);
        GetPass(pass).Queue = value ? QueueType.Compute : QueueType.Graphics;
    }

    internal void AllowGlobalStateModification(int pass, bool value)
    {
        RequireBuilderPass(pass);
        ref PassData data = ref GetPass(pass);
        const PassFlags serialized =
            PassFlags.NeverCull | PassFlags.NeverParallel | PassFlags.NeverMerge;
        if (value)
            data.Flags |= serialized;
        else
            data.Flags &= ~serialized;
    }

    internal void SetRenderAttachment(
        int pass,
        TextureViewHandle texture,
        int index,
        GraphAccess flags,
        LoadType load,
        Vector4 clearColor,
        TextureViewHandle resolveTexture,
        ResolveType resolveMode)
    {
        RequireBuilderPass(pass);
        ValidateAttachmentFlags(flags, nameof(flags));
        AddColorAttachment(
            pass,
            index,
            texture,
            flags,
            load,
            clearColor,
            resolveTexture.IsValid ? resolveTexture : null,
            resolveMode);
    }

    internal void SetRenderAttachmentDepth(
        int pass,
        TextureViewHandle texture,
        GraphAccess flags,
        LoadType depthLoad,
        LoadType stencilLoad,
        float clearDepth,
        byte clearStencil)
    {
        RequireBuilderPass(pass);
        ValidateAttachmentFlags(flags, nameof(flags));
        int ordinal = ValidateTextureView(texture);
        TextureAspects planes = _textureViewRanges[ordinal].Aspects;
        bool readOnly = (flags & GraphAccess.Write) == 0;
        AddDepthStencilAttachment(
            pass,
            texture,
            (planes & TextureAspects.Depth) != 0,
            depthLoad,
            readOnly,
            clearDepth,
            (planes & TextureAspects.Stencil) != 0,
            stencilLoad,
            readOnly,
            clearStencil);
    }

    internal void SetRenderFunc<PassData>(
        int pass,
        PassData passData,
        BaseRenderFunc<PassData, UnsafeGraphContext> renderFunc)
        where PassData : class, new()
    {
        RequireBuilderPass(pass);
        if (_passExecutors[pass] is not null)
            throw new InvalidOperationException("A pass render function can be assigned only once.");
        _passExecutors[pass] =
            (ref UnsafeGraphContext context) => renderFunc(passData, context);
    }

    internal void EndBuilderPass(int pass, in PassRollbackMarker rollback)
    {
        RequireBuilderPass(pass);
        try
        {
            Pipeline? pipeline = GetPassPipeline(pass);
            if (pipeline is not null)
                ValidatePipeline(pass, pipeline);
            _dynamicDeclarations = false;
            _declarationPass = -1;
            EndPass(pass);
        }
        catch
        {
            RollbackPass(pass, in rollback);
            throw;
        }
    }

    internal void AbortBuilderPass(
        int pass,
        in PassRollbackMarker rollback)
    {
        if (_openPass == pass)
            RollbackPass(pass, in rollback);
    }

    private void RequireBuilderPass(int pass)
    {
        if (_openPass != pass || _declarationPass != pass || !_dynamicDeclarations)
            throw new InvalidOperationException("The render-graph builder is not the active pass.");
    }

    private static void ValidateAttachmentFlags(
        GraphAccess flags,
        string parameterName)
    {
        GraphAccess effect = flags & GraphAccess.ReadWrite;
        if ((flags & ~(GraphAccess.ReadWrite | GraphAccess.Discard)) != 0 ||
            effect == GraphAccess.None)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
