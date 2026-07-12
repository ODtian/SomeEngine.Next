using System.Reflection;
using System.Numerics;

namespace SomeEngine.RenderGraph;

internal sealed class GraphRecording
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<MutableResource> _resources = new();
    private readonly List<MutableBufferView> _bufferViews = new();
    private readonly List<MutableTextureView> _textureViews = new();
    private readonly List<MutablePass> _passes = new();
    private readonly Dictionary<BufferHandle, int> _importedBuffers = new();
    private readonly Dictionary<TextureHandle, int> _importedTextures = new();
    private readonly Dictionary<(int Resource, short HistoryOffset), int> _historyResources = new();
    private readonly List<int> _exports = new();
    private bool _consumed;

    public GraphRecording() => Token = new GraphToken();

    public GraphToken Token { get; }
    public long[] PreparedExportTickets => _resources
        .Where(static resource => resource.Exported && resource.ExportTicket != 0)
        .Select(static resource => resource.ExportTicket)
        .ToArray();

    public BufferId AddBuffer(in BufferDesc desc, ImportedBuffer import)
    {
        EnsureMutable();
        if (import.IsValid && !_importedBuffers.TryAdd(import.Handle, _resources.Count))
            throw new InvalidOperationException("A physical buffer may be imported only once per graph invocation.");
        int ordinal = _resources.Count;
        _resources.Add(MutableResource.Buffer(desc, import, ordinal));
        return new BufferId(Token, ordinal);
    }

    public BufferId AddBuffer(in BufferResourceDesc desc)
    {
        EnsureMutable();
        desc.Validate();
        int ordinal = _resources.Count;
        _resources.Add(MutableResource.Buffer(desc, ordinal));
        return new BufferId(Token, ordinal);
    }

    public TextureId AddTexture(in TextureDesc desc, ImportedTexture import)
    {
        EnsureMutable();
        if (import.IsValid && !_importedTextures.TryAdd(import.Handle, _resources.Count))
            throw new InvalidOperationException("A physical texture may be imported only once per graph invocation.");
        int ordinal = _resources.Count;
        _resources.Add(MutableResource.Texture(desc, import, ordinal));
        return new TextureId(Token, ordinal);
    }

    public TextureId AddTexture(in TextureResourceDesc desc)
    {
        EnsureMutable();
        desc.Validate();
        int ordinal = _resources.Count;
        _resources.Add(MutableResource.Texture(desc, ordinal));
        return new TextureId(Token, ordinal);
    }

    public BufferViewId AddBufferView(
        BufferId buffer,
        BufferRange range,
        BindingKind kind,
        Format format,
        uint stride,
        string? name)
    {
        EnsureMutable();
        int resourceOrdinal = ResolveResource(buffer);
        FrozenResource resource = _resources[resourceOrdinal].Freeze(default);
        BufferRange normalized = AccessNormalizer.NormalizeBuffer(resource.BufferDesc, range);
        ValidateBufferView(resource.BufferDesc, kind, format, stride, normalized);
        int ordinal = _bufferViews.Count;
        _bufferViews.Add(new MutableBufferView(resourceOrdinal, normalized, kind, format, stride, name));
        return new BufferViewId(Token, ordinal);
    }

    public TextureViewId AddTextureView(
        TextureId texture,
        TextureSubresourceRange range,
        TextureViewUsage usage,
        Format format,
        string? name,
        TextureViewDimension? dimension = null)
    {
        EnsureMutable();
        int resourceOrdinal = ResolveResource(texture);
        FrozenResource resource = _resources[resourceOrdinal].Freeze(default);
        TextureViewDimension resolvedDimension = dimension ?? InferTextureViewDimension(resource.TextureDesc);
        ValidatedTextureViewDescription validated = TextureViewValidation.Validate(
            resource.TextureDesc,
            range,
            usage,
            format,
            resolvedDimension);
        int ordinal = _textureViews.Count;
        _textureViews.Add(new MutableTextureView(
            resourceOrdinal,
            validated.Range,
            validated.Usage,
            validated.Format,
            validated.Dimension,
            name));
        return new TextureViewId(Token, ordinal);
    }

    private static TextureViewDimension InferTextureViewDimension(in TextureDesc texture) => texture.Dimension switch
    {
        TextureDimension.Texture1D => texture.ArrayLayers > 1
            ? TextureViewDimension.Texture1DArray
            : TextureViewDimension.Texture1D,
        TextureDimension.Texture2D when texture.SampleCount > 1 => texture.ArrayLayers > 1
            ? TextureViewDimension.Texture2DMSArray
            : TextureViewDimension.Texture2DMS,
        TextureDimension.Texture2D => texture.ArrayLayers > 1
            ? TextureViewDimension.Texture2DArray
            : TextureViewDimension.Texture2D,
        TextureDimension.Texture3D => TextureViewDimension.Texture3D,
        _ => throw new ArgumentOutOfRangeException(nameof(texture)),
    };

    public int AddPass(
        string name,
        QueueSelection queues,
        PassRecordingLane recordingLane = PassRecordingLane.Worker)
    {
        EnsureMutable();
        _ = queues.ToArray();
        if (!Enum.IsDefined(recordingLane)) throw new ArgumentOutOfRangeException(nameof(recordingLane));
        int ordinal = _passes.Count;
        _passes.Add(new MutablePass(name, queues, recordingLane));
        return ordinal;
    }

    public BufferAccess AddBufferAccess(
        int pass,
        BufferId buffer,
        ResourceEffect effect,
        BufferUse use,
        BufferRange range,
        PriorContents priorContents,
        WriteCoverage coverage)
    {
        EnsureMutable();
        int resource = ResolveResource(buffer);
        if (buffer.HistoryOffset != 0 && effect != ResourceEffect.Read)
            throw new InvalidOperationException("Temporal history slices are read-only; write the current resource id instead.");
        return AddBufferAccessCore(pass, resource, -1, effect, use, range, priorContents, coverage);
    }

    public TextureAccess AddTextureAccess(
        int pass,
        TextureId texture,
        ResourceEffect effect,
        TextureUse use,
        TextureSubresourceRange range,
        PriorContents priorContents,
        WriteCoverage coverage)
    {
        EnsureMutable();
        int resource = ResolveResource(texture);
        if (texture.HistoryOffset != 0 && effect != ResourceEffect.Read)
            throw new InvalidOperationException("Temporal history slices are read-only; write the current resource id instead.");
        if (use == TextureUse.ColorAttachment)
            throw new ArgumentException("Color attachments must be declared through PassBuilder.ColorAttachment using a graph texture view.", nameof(use));
        return AddTextureAccessCore(pass, resource, -1, effect, use, range, priorContents, coverage);
    }

    public void AddExport(BufferId buffer)
    {
        EnsureMutable();
        ValidateExport(buffer.Owner, buffer.Ordinal, buffer.HistoryOffset, ResourceNodeKind.Buffer);
    }

    public void AddExport(TextureId texture)
    {
        EnsureMutable();
        ValidateExport(texture.Owner, texture.Ordinal, texture.HistoryOffset, ResourceNodeKind.Texture);
    }

    public void PrepareManagedResources(ResourceContinuity continuity, long frameIndex)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(continuity);
        for (int resource = 0; resource < _resources.Count; resource++)
        {
            MutableResource value = _resources[resource];
            if (!value.IsManaged || value.IsImported) continue;
            PreparedResource prepared = continuity.Prepare(value, frameIndex);
            value = value with
            {
                ImportedBuffer = prepared.Buffer,
                ImportedTexture = prepared.Texture,
                ContinuityGeneration = prepared.Generation,
                ExportTicket = prepared.ExportTicket,
            };
            if (value.Kind == ResourceNodeKind.Buffer)
            {
                if (!_importedBuffers.TryAdd(prepared.Buffer.Handle, resource))
                    throw new InvalidOperationException("Managed buffer identity resolves to the same physical slice more than once in one invocation.");
            }
            else if (!_importedTextures.TryAdd(prepared.Texture.Handle, resource))
            {
                throw new InvalidOperationException("Managed texture identity resolves to the same physical slice more than once in one invocation.");
            }
            _resources[resource] = value;
        }
    }

    public BufferViewAccess AddBufferViewAccess(
        int pass,
        BufferViewId view,
        ResourceEffect effect,
        PriorContents priorContents,
        WriteCoverage coverage)
    {
        EnsureMutable();
        MutableBufferView value = ValidateBufferViewId(view);
        BufferUse use = value.Kind switch
        {
            BindingKind.ConstantBuffer => BufferUse.VertexOrConstant,
            BindingKind.ReadOnlyBuffer => BufferUse.ShaderRead,
            BindingKind.StorageBuffer => BufferUse.ShaderWrite,
            _ => throw new ArgumentException($"Buffer view kind {value.Kind} is not a shader buffer view.", nameof(view)),
        };
        ValidateViewEffect(effect, value.Kind, nameof(view));
        BufferAccess resourceAccess = AddBufferAccessCore(
            pass,
            value.Resource,
            view.Ordinal,
            effect,
            use,
            value.Range,
            priorContents,
            coverage);
        return new BufferViewAccess(resourceAccess, view.Ordinal);
    }

    public TextureViewAccess AddTextureViewAccess(
        int pass,
        TextureViewId view,
        ResourceEffect effect,
        PriorContents priorContents,
        WriteCoverage coverage)
    {
        EnsureMutable();
        MutableTextureView value = ValidateTextureViewId(view);
        BindingKind kind;
        TextureUse use;
        if (effect == ResourceEffect.Read && (value.Usage & TextureViewUsage.ShaderResource) != 0)
        {
            kind = BindingKind.SampledTexture;
            use = TextureUse.Sampled;
        }
        else if ((value.Usage & TextureViewUsage.Storage) != 0)
        {
            kind = BindingKind.StorageTexture;
            use = TextureUse.Storage;
        }
        else
        {
            throw new ArgumentException("Shader texture accesses require a view with ShaderResource or Storage usage.", nameof(view));
        }
        ValidateViewEffect(effect, kind, nameof(view));
        TextureAccess resourceAccess = AddTextureAccessCore(
            pass,
            value.Resource,
            view.Ordinal,
            effect,
            use,
            value.Range,
            priorContents,
            coverage);
        return new TextureViewAccess(resourceAccess, view.Ordinal);
    }

    public ColorAttachmentAccess AddColorAttachment(int pass, int slot, TextureViewId view, LoadAction load, Vector4 clearColor)
    {
        EnsureMutable();
        if ((uint)slot >= 8u) throw new ArgumentOutOfRangeException(nameof(slot), "Color attachment slots are in the range [0, 7].");
        if (!Enum.IsDefined(load)) throw new ArgumentOutOfRangeException(nameof(load));
        MutableTextureView value = ValidateTextureViewId(view);
        if ((value.Usage & TextureViewUsage.ColorAttachment) == 0)
            throw new ArgumentException("A color attachment requires a texture view with ColorAttachment usage.", nameof(view));
        MutablePass mutablePass = GetPass(pass);
        if (mutablePass.ColorAttachments.Any(attachment => attachment.Slot == slot))
            throw new InvalidOperationException($"Pass '{mutablePass.Name}' already declares color attachment slot {slot}.");

        PriorContents prior = load == LoadAction.Load ? PriorContents.Required : PriorContents.Discard;
        WriteCoverage coverage = load == LoadAction.Clear ? WriteCoverage.Full : WriteCoverage.Partial;
        TextureAccess resourceAccess = AddTextureAccessCore(
            pass,
            value.Resource,
            view.Ordinal,
            ResourceEffect.Write,
            TextureUse.ColorAttachment,
            value.Range,
            prior,
            coverage);
        int access = resourceAccess.Access;
        mutablePass.ColorAttachments.Add(new MutableColorAttachment(slot, view.Ordinal, access, load, clearColor));
        return new ColorAttachmentAccess(new TextureViewAccess(resourceAccess, view.Ordinal), slot, load, clearColor);
    }

    public DepthStencilAttachmentAccess AddDepthStencilAttachment(
        int pass,
        TextureViewId view,
        DepthAttachmentOps? depth,
        StencilAttachmentOps? stencil)
    {
        EnsureMutable();
        if (depth is null && stencil is null)
            throw new ArgumentException("A depth-stencil attachment must select at least one plane.");
        MutableTextureView value = ValidateTextureViewId(view);
        if ((value.Usage & TextureViewUsage.DepthStencilAttachment) == 0)
            throw new ArgumentException("A depth-stencil attachment requires a view with DepthStencilAttachment usage.", nameof(view));
        MutablePass mutablePass = GetPass(pass);
        if (mutablePass.DepthStencilAttachment is not null)
            throw new InvalidOperationException($"Pass '{mutablePass.Name}' already declares a depth-stencil attachment.");

        TextureAccess depthAccess = default;
        TextureAccess stencilAccess = default;
        int depthAccessOrdinal = -1;
        int stencilAccessOrdinal = -1;
        if (depth is DepthAttachmentOps depthOps)
        {
            ValidateDepthOps(depthOps);
            if ((value.Range.Aspect & TextureAspect.Depth) == 0)
                throw new ArgumentException("The attachment view does not include the depth plane.", nameof(view));
            depthAccess = AddAttachmentPlaneAccess(pass, view.Ordinal, value, TextureAspect.Depth, depthOps.Load, depthOps.ReadOnly);
            depthAccessOrdinal = depthAccess.Access;
        }
        if (stencil is StencilAttachmentOps stencilOps)
        {
            ValidateStencilOps(stencilOps);
            if ((value.Range.Aspect & TextureAspect.Stencil) == 0)
                throw new ArgumentException("The attachment view does not include the stencil plane.", nameof(view));
            FrozenResource resource = _resources[value.Resource].Freeze(default);
            if (resource.TextureDesc.Format != Format.D24UNormS8UInt)
                throw new ArgumentException("Stencil attachment operations require D24UNormS8UInt.", nameof(stencil));
            stencilAccess = AddAttachmentPlaneAccess(pass, view.Ordinal, value, TextureAspect.Stencil, stencilOps.Load, stencilOps.ReadOnly);
            stencilAccessOrdinal = stencilAccess.Access;
        }

        mutablePass.DepthStencilAttachment = new MutableDepthStencilAttachment(
            view.Ordinal,
            depthAccessOrdinal,
            stencilAccessOrdinal,
            depth,
            stencil);
        return new DepthStencilAttachmentAccess(view, depthAccess, stencilAccess, depth is not null, stencil is not null);
    }

    public ShaderBindingAccess AddShaderBindingAccess(
        int pass,
        uint group,
        uint binding,
        uint element,
        BufferViewAccess access)
    {
        EnsureMutable();
        ValidateBufferViewAccessToken(pass, access);
        return new ShaderBindingAccess(
            Token,
            pass,
            group,
            binding,
            element,
            ShaderBindingAccessKind.BufferView,
            access.ResourceAccess.Access,
            access.View);
    }

    public ShaderBindingAccess AddShaderBindingAccess(
        int pass,
        uint group,
        uint binding,
        uint element,
        TextureViewAccess access)
    {
        EnsureMutable();
        ValidateTextureViewAccessToken(pass, access);
        return new ShaderBindingAccess(
            Token,
            pass,
            group,
            binding,
            element,
            ShaderBindingAccessKind.TextureView,
            access.ResourceAccess.Access,
            access.View);
    }

    public ShaderBindingAccess AddExternallyManagedShaderBinding(int pass, uint group, uint binding, uint element)
    {
        EnsureMutable();
        _ = GetPass(pass);
        return new ShaderBindingAccess(
            Token,
            pass,
            group,
            binding,
            element,
            ShaderBindingAccessKind.ExternallyManaged,
            -1,
            -1);
    }

    public void AddShader(int pass, in ShaderDesc shader, ReadOnlySpan<ShaderBindingAccess> mappings)
    {
        EnsureMutable();
        MutablePass mutablePass = GetPass(pass);
        FrozenShaderContract contract = ShaderContractValidator.Freeze(shader, mappings, Token, pass);
        FrozenAccess[] accesses = mutablePass.Accesses.Select(static access => access.Freeze()).ToArray();
        FrozenBufferView[] bufferViews = _bufferViews.Select(static view => view.Freeze()).ToArray();
        FrozenTextureView[] textureViews = _textureViews.Select(static view => view.Freeze()).ToArray();
        ShaderContractValidator.Validate(contract, mutablePass.Name, accesses, bufferViews, textureViews);
        mutablePass.Shaders.Add(contract);
    }

    public void AddPipeline(int pass, PipelineHandle pipeline)
    {
        EnsureMutable();
        if (!pipeline.IsValid) throw new ArgumentException("A valid pipeline handle is required.", nameof(pipeline));
        MutablePass mutablePass = GetPass(pass);
        if (!mutablePass.Pipelines.Add(pipeline))
            throw new InvalidOperationException($"Pass '{mutablePass.Name}' already declares pipeline {pipeline}.");
    }

    public void AddQueryPool(int pass, QueryPoolHandle pool)
    {
        EnsureMutable();
        if (!pool.IsValid) throw new ArgumentException("A valid query-pool handle is required.", nameof(pool));
        MutablePass mutablePass = GetPass(pass);
        if (!mutablePass.QueryPools.Add(pool))
            throw new InvalidOperationException($"Pass '{mutablePass.Name}' already declares query pool {pool}.");
    }

    public void SetExecution(int pass, PassExecution execution)
    {
        EnsureMutable();
        MutablePass mutablePass = GetPass(pass);
        if (mutablePass.Execution is not null) throw new InvalidOperationException("A pass execution delegate can only be assigned once.");
        mutablePass.Execution = execution;
    }

    public FrozenGraph Freeze(IDevice device)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(device);
        ValidateImportedResources(device);
        ValidatePipelineContracts(device);
        _consumed = true;

        FrozenResource[] resources = new FrozenResource[_resources.Count];
        for (int index = 0; index < resources.Length; index++)
        {
            MutableResource resource = _resources[index];
            if (resource.IsManaged && !resource.IsImported)
                throw new InvalidOperationException("Managed graph resources must be prepared by their owning RenderGraph before freezing.");
            ResourceRequirements requirements = default;
            if (!resource.IsImported)
            {
                requirements = resource.Kind == ResourceNodeKind.Buffer
                    ? device.GetBufferRequirements(resource.BufferDesc, MemoryType.DeviceLocal)
                    : device.GetTextureRequirements(resource.TextureDesc);
            }
            resources[index] = resource.Freeze(requirements);
        }

        FrozenBufferView[] bufferViews = _bufferViews.Select(static view => view.Freeze()).ToArray();
        FrozenTextureView[] textureViews = _textureViews.Select(static view => view.Freeze()).ToArray();
        FrozenPass[] passes = new FrozenPass[_passes.Count];
        for (int index = 0; index < passes.Length; index++)
        {
            MutablePass pass = _passes[index];
            PassExecution execution = pass.Execution ?? throw new InvalidOperationException($"Pass '{pass.Name}' has no execution delegate.");
            ExecutorIdentity identity = ExecutorIdentity.Create(execution.Method);
            FrozenAccess[] accesses = AccessNormalizer.Normalize(resources, pass.Accesses.Select(static access => access.Freeze()).ToArray());
            FrozenColorAttachment[] colors = pass.ColorAttachments
                .OrderBy(static attachment => attachment.Slot)
                .Select(static attachment => attachment.Freeze())
                .ToArray();
            FrozenDepthStencilAttachment? depthStencil = pass.DepthStencilAttachment?.Freeze();
            FrozenQueryPool[] queryPools = pass.QueryPools
                .Select(pool => new FrozenQueryPool(pool, device.GetQueryPoolMetadata(pool)))
                .ToArray();
            foreach (FrozenShaderContract shader in pass.Shaders)
                ShaderContractValidator.Validate(shader, pass.Name, accesses, bufferViews, textureViews);
            passes[index] = new FrozenPass(
                pass.Name,
                pass.Queues,
                pass.RecordingLane,
                accesses,
                colors,
                depthStencil,
                pass.Shaders.ToArray(),
                pass.Pipelines.OrderBy(static pipeline => pipeline.Slot).ThenBy(static pipeline => pipeline.Generation).ToArray(),
                execution,
                identity,
                queryPools);
        }

        GraphCanonicalData canonical = GraphCanonicalData.Create(device.Compilation, resources, bufferViews, textureViews, passes);
        return new FrozenGraph(Token, resources, bufferViews, textureViews, passes, canonical);
    }

    private int ResolveResource(BufferId buffer)
    {
        ValidateResource(buffer.Owner, buffer.Ordinal, ResourceNodeKind.Buffer);
        return ResolveHistoryResource(buffer.Ordinal, buffer.HistoryOffset);
    }

    private int ResolveResource(TextureId texture)
    {
        ValidateResource(texture.Owner, texture.Ordinal, ResourceNodeKind.Texture);
        return ResolveHistoryResource(texture.Ordinal, texture.HistoryOffset);
    }

    private int ResolveHistoryResource(int baseResource, short historyOffset)
    {
        if (historyOffset == 0) return baseResource;
        MutableResource value = _resources[baseResource];
        if (value.Lifetime != ResourceLifetime.Temporal)
            throw new InvalidOperationException("Only temporal resources expose history slices.");
        if (historyOffset > value.HistoryCount)
            throw new ArgumentOutOfRangeException(nameof(historyOffset), $"The resource retains {value.HistoryCount} prior frames.");
        if (_historyResources.TryGetValue((baseResource, historyOffset), out int existing)) return existing;
        int ordinal = _resources.Count;
        _resources.Add(value with
        {
            BaseOrdinal = baseResource,
            HistoryOffset = historyOffset,
            ImportedBuffer = default,
            ImportedTexture = default,
            Exported = false,
            ExportTicket = 0,
            ContinuityGeneration = 0,
        });
        _historyResources.Add((baseResource, historyOffset), ordinal);
        return ordinal;
    }

    private void ValidateExport(GraphToken? owner, int ordinal, short historyOffset, ResourceNodeKind kind)
    {
        ValidateResource(owner, ordinal, kind);
        if (historyOffset != 0) throw new InvalidOperationException("A prior history slice cannot be exported.");
        MutableResource resource = _resources[ordinal];
        if (resource.IsImported) throw new InvalidOperationException("Imported resources already have external ownership and cannot be exported.");
        if (resource.Lifetime != ResourceLifetime.Transient)
            throw new InvalidOperationException("Temporal and persistent resources remain owned by RenderGraph and cannot be exported.");
        if (resource.Exported) throw new InvalidOperationException("A graph resource may be exported only once.");
        _resources[ordinal] = resource with { Exported = true };
        _exports.Add(ordinal);
    }

    private BufferAccess AddBufferAccessCore(
        int pass,
        int resource,
        int view,
        ResourceEffect effect,
        BufferUse use,
        BufferRange range,
        PriorContents priorContents,
        WriteCoverage coverage)
    {
        if (_resources[resource].HistoryOffset != 0 && effect != ResourceEffect.Read)
            throw new InvalidOperationException("Temporal history slices are read-only; write the current resource id instead.");
        ValidateBufferEffect(effect, use);
        MutablePass mutablePass = GetPass(pass);
        int access = mutablePass.Accesses.Count;
        mutablePass.Accesses.Add(MutableAccess.Buffer(resource, view, effect, use, range, priorContents, coverage));
        return new BufferAccess(Token, pass, access, resource, effect, use, range);
    }

    private TextureAccess AddTextureAccessCore(
        int pass,
        int resource,
        int view,
        ResourceEffect effect,
        TextureUse use,
        TextureSubresourceRange range,
        PriorContents priorContents,
        WriteCoverage coverage)
    {
        if (_resources[resource].HistoryOffset != 0 && effect != ResourceEffect.Read)
            throw new InvalidOperationException("Temporal history slices are read-only; write the current resource id instead.");
        ValidateTextureEffect(effect, use);
        MutablePass mutablePass = GetPass(pass);
        int access = mutablePass.Accesses.Count;
        mutablePass.Accesses.Add(MutableAccess.Texture(resource, view, effect, use, range, priorContents, coverage));
        return new TextureAccess(Token, pass, access, resource, effect, use, range);
    }

    private TextureAccess AddAttachmentPlaneAccess(
        int pass,
        int view,
        in MutableTextureView value,
        TextureAspect aspect,
        LoadAction load,
        bool readOnly)
    {
        TextureSubresourceRange range = value.Range with { Aspect = aspect };
        ResourceEffect effect = readOnly ? ResourceEffect.Read : ResourceEffect.Write;
        TextureUse use = readOnly ? TextureUse.DepthRead : TextureUse.DepthWrite;
        PriorContents prior = readOnly || load == LoadAction.Load ? PriorContents.Required : PriorContents.Discard;
        WriteCoverage coverage = !readOnly && load == LoadAction.Clear ? WriteCoverage.Full : WriteCoverage.Partial;
        return AddTextureAccessCore(pass, value.Resource, view, effect, use, range, prior, coverage);
    }

    private static void ValidateDepthOps(in DepthAttachmentOps ops)
    {
        if (!Enum.IsDefined(ops.Load)) throw new ArgumentOutOfRangeException(nameof(ops));
        if (ops.ReadOnly && ops.Load != LoadAction.Load)
            throw new ArgumentException("A read-only depth plane requires Load.", nameof(ops));
        if (ops.ClearValue is < 0f or > 1f || float.IsNaN(ops.ClearValue))
            throw new ArgumentOutOfRangeException(nameof(ops), "Depth clear value must be in [0, 1].");
    }

    private static void ValidateStencilOps(in StencilAttachmentOps ops)
    {
        if (!Enum.IsDefined(ops.Load)) throw new ArgumentOutOfRangeException(nameof(ops));
        if (ops.ReadOnly && ops.Load != LoadAction.Load)
            throw new ArgumentException("A read-only stencil plane requires Load.", nameof(ops));
    }

    private MutablePass GetPass(int pass) => (uint)pass < (uint)_passes.Count
        ? _passes[pass]
        : throw new ArgumentOutOfRangeException(nameof(pass));

    private void ValidateImportedResources(IDevice device)
    {
        DeviceDomain domain = device.Domain;
        Dictionary<PhysicalAllocationId, List<ImportedAllocationRange>> allocationRanges = [];
        for (int resource = 0; resource < _resources.Count; resource++)
        {
            MutableResource value = _resources[resource];
            if (!value.IsImported) continue;
            DeviceDomain importedDomain;
            PhysicalAllocationInfo allocation;
            if (value.Kind == ResourceNodeKind.Buffer)
            {
                ImportedBuffer import = value.ImportedBuffer;
                importedDomain = import.Handle.Domain;
                BufferMetadata current = device.GetBufferMetadata(import.Handle);
                if (current != import.Metadata || current.Description != value.BufferDesc)
                {
                    throw new InvalidOperationException(
                        $"Imported buffer at resource ordinal {resource} no longer matches its recorded live metadata.");
                }
                allocation = current.Allocation;
            }
            else
            {
                ImportedTexture import = value.ImportedTexture;
                importedDomain = import.Handle.Domain;
                TextureMetadata current = device.GetTextureMetadata(import.Handle);
                if (current != import.Metadata || current.Description != value.TextureDesc)
                {
                    throw new InvalidOperationException(
                        $"Imported texture at resource ordinal {resource} no longer matches its recorded live metadata.");
                }
                allocation = current.Allocation;
            }
            if (importedDomain != domain)
            {
                throw new ArgumentException(
                    $"Imported {value.Kind.ToString().ToLowerInvariant()} at resource ordinal {resource} belongs to another device domain.");
            }
            if (!allocation.Identity.IsValid || allocation.Identity.Domain != domain)
            {
                throw new InvalidOperationException(
                    $"Imported resource at ordinal {resource} has invalid physical-allocation metadata.");
            }

            if (!allocationRanges.TryGetValue(allocation.Identity, out List<ImportedAllocationRange>? ranges))
            {
                ranges = [];
                allocationRanges.Add(allocation.Identity, ranges);
            }
            ulong end = allocation.End;
            foreach (ImportedAllocationRange prior in ranges)
            {
                if (allocation.Offset < prior.End && prior.Offset < end)
                {
                    throw new InvalidOperationException(
                        $"Imported resources at ordinals {prior.Resource} and {resource} overlap in one physical allocation.");
                }
            }
            ranges.Add(new ImportedAllocationRange(resource, allocation.Offset, end));

            GpuCompletion[] readiness = value.Kind == ResourceNodeKind.Buffer
                ? value.ImportedBuffer.Readiness ?? []
                : value.ImportedTexture.Readiness ?? [];
            QueueType? priorQueue = null;
            foreach (GpuCompletion completion in readiness)
            {
                if (!completion.IsValid || completion.Domain != domain)
                    throw new ArgumentException($"Imported resource at ordinal {resource} has invalid or cross-device readiness.");
                if (priorQueue is not null && completion.Queue <= priorQueue.Value)
                    throw new ArgumentException($"Imported resource at ordinal {resource} readiness is not queue-normalized.");
                priorQueue = completion.Queue;
                _ = device.Wait(completion, TimeSpan.Zero);
            }
        }
    }

    private readonly record struct ImportedAllocationRange(int Resource, ulong Offset, ulong End);

    private void ValidatePipelineContracts(IDevice device)
    {
        DeviceDomain domain = device.Domain;
        foreach (MutablePass pass in _passes)
        foreach (PipelineHandle pipeline in pass.Pipelines)
        {
            if (pipeline.Domain != domain)
                throw new ArgumentException($"Pass '{pass.Name}' declares a pipeline from another device domain.");
            PipelineMetadata metadata = device.GetPipelineMetadata(pipeline);
            foreach (PipelineShaderIdentity shader in metadata.Shaders)
            {
                if (!pass.Shaders.Any(contract =>
                        contract.Key == shader.Key &&
                        contract.Stage == shader.Stage))
                {
                    throw new InvalidOperationException(
                        $"Pass '{pass.Name}' pipeline shader {shader.Stage}/{shader.Key} is absent from its frozen UsesShader contracts.");
                }
            }
        }
    }

    private void ValidateResource(GraphToken? owner, int ordinal, ResourceNodeKind kind)
    {
        if (!ReferenceEquals(owner, Token)) throw new ArgumentException("The resource belongs to a different graph invocation.");
        if ((uint)ordinal >= (uint)_resources.Count || _resources[ordinal].Kind != kind)
            throw new ArgumentException("The resource id has the wrong kind or ordinal.");
    }

    private MutableBufferView ValidateBufferViewId(BufferViewId view)
    {
        if (!ReferenceEquals(view.Owner, Token)) throw new ArgumentException("The buffer view belongs to a different graph invocation.", nameof(view));
        if ((uint)view.Ordinal >= (uint)_bufferViews.Count) throw new ArgumentException("The buffer view id has an invalid ordinal.", nameof(view));
        return _bufferViews[view.Ordinal];
    }

    private MutableTextureView ValidateTextureViewId(TextureViewId view)
    {
        if (!ReferenceEquals(view.Owner, Token)) throw new ArgumentException("The texture view belongs to a different graph invocation.", nameof(view));
        if ((uint)view.Ordinal >= (uint)_textureViews.Count) throw new ArgumentException("The texture view id has an invalid ordinal.", nameof(view));
        return _textureViews[view.Ordinal];
    }

    private void ValidateBufferViewAccessToken(int pass, BufferViewAccess access)
    {
        BufferAccess resourceAccess = access.ResourceAccess;
        if (!access.IsValid || !ReferenceEquals(resourceAccess.Owner, Token) || resourceAccess.Pass != pass)
            throw new ArgumentException("The buffer view access must be declared by this pass.", nameof(access));
        MutablePass mutablePass = GetPass(pass);
        if ((uint)resourceAccess.Access >= (uint)mutablePass.Accesses.Count ||
            (uint)access.View >= (uint)_bufferViews.Count)
            throw new ArgumentException("The buffer view access token has an invalid ordinal.", nameof(access));
        MutableAccess expected = mutablePass.Accesses[resourceAccess.Access];
        MutableBufferView view = _bufferViews[access.View];
        if (expected.Kind != ResourceNodeKind.Buffer || expected.Resource != resourceAccess.Resource ||
            expected.Resource != view.Resource || expected.View != access.View)
            throw new ArgumentException("The buffer view access token does not match its exact declared access.", nameof(access));
    }

    private void ValidateTextureViewAccessToken(int pass, TextureViewAccess access)
    {
        TextureAccess resourceAccess = access.ResourceAccess;
        if (!access.IsValid || !ReferenceEquals(resourceAccess.Owner, Token) || resourceAccess.Pass != pass)
            throw new ArgumentException("The texture view access must be declared by this pass.", nameof(access));
        MutablePass mutablePass = GetPass(pass);
        if ((uint)resourceAccess.Access >= (uint)mutablePass.Accesses.Count ||
            (uint)access.View >= (uint)_textureViews.Count)
            throw new ArgumentException("The texture view access token has an invalid ordinal.", nameof(access));
        MutableAccess expected = mutablePass.Accesses[resourceAccess.Access];
        MutableTextureView view = _textureViews[access.View];
        if (expected.Kind != ResourceNodeKind.Texture || expected.Resource != resourceAccess.Resource ||
            expected.Resource != view.Resource || expected.View != access.View)
            throw new ArgumentException("The texture view access token does not match its exact declared access.", nameof(access));
    }

    private void EnsureMutable()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("Graph recording is single-writer.");
        if (_consumed) throw new InvalidOperationException("Graph recording has already been consumed.");
    }

    private static void ValidateBufferEffect(ResourceEffect effect, BufferUse use)
    {
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        bool writable = use is BufferUse.CopyDestination or BufferUse.ShaderWrite;
        bool readable = use != BufferUse.CopyDestination;
        if (effect == ResourceEffect.Read && !readable) throw new ArgumentException($"Buffer use '{use}' does not permit read access.");
        if (effect == ResourceEffect.Write && !writable) throw new ArgumentException($"Buffer use '{use}' does not permit write access.");
        if (effect == ResourceEffect.ReadWrite && use != BufferUse.ShaderWrite) throw new ArgumentException("ReadWrite buffer access requires ShaderWrite use.");
    }

    private static void ValidateTextureEffect(ResourceEffect effect, TextureUse use)
    {
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        bool writeUse = use is TextureUse.CopyDestination or TextureUse.ResolveDestination or TextureUse.Storage or TextureUse.ColorAttachment or TextureUse.DepthWrite;
        bool readUse = use is TextureUse.CopySource or TextureUse.ResolveSource or TextureUse.Sampled or TextureUse.Storage or TextureUse.DepthRead;
        if (effect == ResourceEffect.Read && !readUse) throw new ArgumentException($"Texture use '{use}' does not permit read access.");
        if (effect == ResourceEffect.Write && !writeUse) throw new ArgumentException($"Texture use '{use}' does not permit write access.");
        if (effect == ResourceEffect.ReadWrite && use != TextureUse.Storage) throw new ArgumentException("ReadWrite texture access requires Storage use.");
    }

    private static void ValidateBufferView(
        in BufferDesc desc,
        BindingKind kind,
        Format format,
        uint stride,
        in BufferRange range)
    {
        BufferUsage required = kind switch
        {
            BindingKind.ConstantBuffer => BufferUsage.Constant,
            BindingKind.ReadOnlyBuffer => BufferUsage.ShaderRead,
            BindingKind.StorageBuffer => BufferUsage.ShaderWrite,
            _ => throw new ArgumentException($"Binding kind {kind} cannot describe a buffer view.", nameof(kind)),
        };
        if ((desc.Usage & required) == 0) throw new ArgumentException($"Buffer view kind {kind} requires resource usage {required}.", nameof(kind));
        if (stride > range.Size) throw new ArgumentOutOfRangeException(nameof(stride));
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
    }

    private static void ValidateViewEffect(ResourceEffect effect, BindingKind kind, string parameterName)
    {
        bool writable = kind is BindingKind.StorageBuffer or BindingKind.StorageTexture;
        if (effect != ResourceEffect.Read && !writable)
            throw new ArgumentException($"View kind {kind} is read-only.", parameterName);
    }
}

internal sealed class MutablePass
{
    public MutablePass(string name, QueueSelection queues, PassRecordingLane recordingLane)
    {
        Name = name;
        Queues = queues;
        RecordingLane = recordingLane;
    }

    public string Name { get; }
    public QueueSelection Queues { get; }
    public PassRecordingLane RecordingLane { get; }
    public List<MutableAccess> Accesses { get; } = new();
    public List<MutableColorAttachment> ColorAttachments { get; } = new();
    public MutableDepthStencilAttachment? DepthStencilAttachment { get; set; }
    public List<FrozenShaderContract> Shaders { get; } = new();
    public HashSet<PipelineHandle> Pipelines { get; } = [];
    public HashSet<QueryPoolHandle> QueryPools { get; } = [];
    public PassExecution? Execution { get; set; }
}

internal readonly record struct ImportedBuffer(
    BufferHandle Handle,
    BufferMetadata Metadata,
    BufferUse InitialUse,
    BufferUse FinalUse,
    bool ContentsAvailable,
    GpuCompletion[]? Readiness = null,
    ResourceState? InitialStateOverride = null,
    ResourceState? FinalStateOverride = null)
{
    public bool IsValid => Handle.IsValid;
}

internal readonly record struct ImportedTexture(
    TextureHandle Handle,
    TextureMetadata Metadata,
    TextureUse InitialUse,
    TextureUse FinalUse,
    bool ContentsAvailable,
    GpuCompletion[]? Readiness = null,
    ResourceState? InitialStateOverride = null,
    ResourceState? FinalStateOverride = null)
{
    public bool IsValid => Handle.IsValid;
}

internal readonly record struct MutableResource(
    ResourceNodeKind Kind,
    BufferDesc BufferDesc,
    TextureDesc TextureDesc,
    ImportedBuffer ImportedBuffer,
    ImportedTexture ImportedTexture,
    ResourceLifetime Lifetime,
    Guid StableId,
    int BaseOrdinal,
    short HistoryOffset,
    int HistoryCount,
    bool Exported,
    ulong ContinuityGeneration,
    long ExportTicket)
{
    public bool IsImported => Kind == ResourceNodeKind.Buffer ? ImportedBuffer.IsValid : ImportedTexture.IsValid;
    public bool IsManaged => Lifetime != ResourceLifetime.Transient || Exported;

    public static MutableResource Buffer(in BufferDesc desc, ImportedBuffer import, int ordinal) =>
        new(ResourceNodeKind.Buffer, desc, default, import, default, ResourceLifetime.Transient, default, ordinal, 0, 0, false, 0, 0);

    public static MutableResource Buffer(in BufferResourceDesc desc, int ordinal) =>
        new(ResourceNodeKind.Buffer, desc.Description, default, default, default, desc.Lifetime, desc.StableId, ordinal, 0, desc.HistoryCount, false, 0, 0);

    public static MutableResource Texture(in TextureDesc desc, ImportedTexture import, int ordinal) =>
        new(ResourceNodeKind.Texture, default, desc, default, import, ResourceLifetime.Transient, default, ordinal, 0, 0, false, 0, 0);

    public static MutableResource Texture(in TextureResourceDesc desc, int ordinal) =>
        new(ResourceNodeKind.Texture, default, desc.Description, default, default, desc.Lifetime, desc.StableId, ordinal, 0, desc.HistoryCount, false, 0, 0);

    public FrozenResource Freeze(ResourceRequirements requirements) => new(
        Kind,
        BufferDesc,
        TextureDesc,
        IsImported,
        ImportedBuffer,
        ImportedTexture,
        requirements,
        Lifetime,
        StableId,
        BaseOrdinal,
        HistoryOffset,
        HistoryCount,
        Exported,
        ContinuityGeneration,
        ExportTicket);
}

internal readonly record struct MutableAccess(
    ResourceNodeKind Kind,
    int Resource,
    int View,
    ResourceEffect Effect,
    BufferUse BufferUse,
    TextureUse TextureUse,
    BufferRange BufferRange,
    TextureSubresourceRange TextureRange,
    PriorContents PriorContents,
    WriteCoverage Coverage)
{
    public static MutableAccess Buffer(int resource, int view, ResourceEffect effect, BufferUse use, BufferRange range, PriorContents prior, WriteCoverage coverage) =>
        new(ResourceNodeKind.Buffer, resource, view, effect, use, default, range, default, prior, coverage);

    public static MutableAccess Texture(int resource, int view, ResourceEffect effect, TextureUse use, TextureSubresourceRange range, PriorContents prior, WriteCoverage coverage) =>
        new(ResourceNodeKind.Texture, resource, view, effect, default, use, default, range, prior, coverage);

    public FrozenAccess Freeze() => new(Kind, Resource, View, Effect, BufferUse, TextureUse, BufferRange, TextureRange, PriorContents, Coverage);
}

internal readonly record struct MutableBufferView(
    int Resource,
    BufferRange Range,
    BindingKind Kind,
    Format Format,
    uint Stride,
    string? Name)
{
    public FrozenBufferView Freeze() => new(Resource, Range, Kind, Format, Stride, Name);
}

internal readonly record struct MutableTextureView(
    int Resource,
    TextureSubresourceRange Range,
    TextureViewUsage Usage,
    Format Format,
    TextureViewDimension Dimension,
    string? Name)
{
    public FrozenTextureView Freeze() => new(Resource, Range, Usage, Format, Dimension, Name);
}

internal readonly record struct MutableColorAttachment(int Slot, int View, int Access, LoadAction Load, Vector4 ClearColor)
{
    public FrozenColorAttachment Freeze() => new(Slot, View, Access, Load, ClearColor);
}

internal readonly record struct MutableDepthStencilAttachment(
    int View,
    int DepthAccess,
    int StencilAccess,
    DepthAttachmentOps? Depth,
    StencilAttachmentOps? Stencil)
{
    public FrozenDepthStencilAttachment Freeze() => new(View, DepthAccess, StencilAccess, Depth, Stencil);
}

internal readonly record struct ExecutorIdentity(Guid Module, int MetadataToken, string DeclaringType, string Method)
{
    public static ExecutorIdentity Create(MethodInfo method)
    {
        try
        {
            return new ExecutorIdentity(method.Module.ModuleVersionId, method.MetadataToken, method.DeclaringType?.FullName ?? string.Empty, method.Name);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException("Pass executors must have stable metadata identity; dynamic methods are not supported.", nameof(method), exception);
        }
    }
}
