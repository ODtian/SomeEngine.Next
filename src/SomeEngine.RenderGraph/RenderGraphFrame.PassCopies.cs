namespace SomeEngine.RenderGraph;

public ref partial struct RenderGraphFrame
{
    /// <summary>Returns the passes within a retained region in declaration order.</summary>
    public GraphPassId[] GetRegionPasses(GraphRegionId region) => State.GetRegionPasses(region);

    /// <summary>
    /// Copies a retained pass's commands and declarations into this frame. The optional
    /// constant-buffer value supplies per-copy shader data; command resources retain
    /// their original declarations. Copies execute within the specified frame region.
    /// </summary>
    public GraphPassId AddPassCopy(
        GraphPassId source, string label, GraphRegionId region,
        GraphBufferInput? constantBuffer = null, GraphBufferSlice value = default) =>
        State.AddPassCopy(source, label, region, constantBuffer, value);
}

internal sealed partial class RenderGraphFrameState
{
    internal GraphPassId[] GetRegionPasses(GraphRegionId region)
    {
        GraphStructure structure = StructureIndex.Structure;
        var passes = new List<GraphPassId>();
        Collect(structure.GetRegion(region.Value));
        return [.. passes];
        void Collect(GraphRegion current)
        {
            foreach (GraphRegionEntry child in current.Children)
                if (child.Identity.Domain == GraphIdentityDomain.Region)
                    Collect(structure.Regions.Get(child.Identity));
                else if (child.Identity.Domain == GraphIdentityDomain.Pass)
                    passes.Add(new GraphPassId(child.Identity));
        }
    }

    internal GraphPassId AddPassCopy(
        GraphPassId source, string label, GraphRegionId region,
        GraphBufferInput? constantBuffer, GraphBufferSlice value)
    {
        EnsureAuthoring();
        ValidateStatic(source.Value, StructureIndex.Structure.Passes);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        DevelopmentCheck.Require(region == default ||
            region.Value.Owner == Graph.Identity &&
            StructureIndex.Structure.GetRegion(region.Value).IsFrame,
            "Pass copies require a frame region.");
        GraphPass definition = StructureIndex.Structure.Passes.Get(source.Value);
        GraphIdentity identity = NewIdentity(_dynamicPasses.Count, GraphIdentityDomain.Pass);
        (List<GraphParameterResourceBinding> bindings, GraphBufferInput? bufferInput) =
            GetCopyBindings(definition, region.Value, constantBuffer);
        bool replaced = bufferInput is not null;
        if (bufferInput is not null) SetBufferInput(bufferInput, value);
        if (!_copiedCommands.TryGetValue(source.Value, out PassCallbackStorage? commands))
            _copiedCommands.Add(source.Value, commands = definition.CallbackStorage.Copy());
        _dynamicPasses.Add(new FramePass
        {
            Identity = identity,
            CommandSource = source.Value,
            CopyDefinition = definition,
            Label = label,
            Kind = definition.Kind,
            QueuePolicy = definition.Queue,
            Options = definition.Options with { RepeatCommands = false },
            Pipeline = definition.Pipeline,
            ParameterLayout = definition.ParameterLayout,
            ParameterOrdinaryData = definition.ParameterOrdinaryData,
            ParameterBindings = bindings,
            PersistentBindings = definition.PersistentBindings,
            PersistentCallbacks = commands,
            DeclarationOrdinal = identity.Slot,
            Enabled = true,
            Region = region.Value,
        });
        if (definition.ColorAttachments.Count != 0)
            DynamicColorAttachments.Add(identity, new List<GraphColorAttachment>(definition.ColorAttachments));
        if (definition.DepthStencilAttachment is { } depth) DynamicDepthAttachments.Add(identity, depth);
        if (definition.RenderingRegions.Count != 0)
            DynamicRenderingRegions.Add(identity, new List<PassRenderingRegion>(definition.RenderingRegions));
        bool bufferAccessReplaced = false;
        foreach (CopyAccess item in GetCopyAccesses(source.Value, definition))
        {
            FrameResourceAccess access = item.Access;
            access.Identity = NewIdentity(_dynamicAccesses.Count, GraphIdentityDomain.Access);
            access.Pass = identity;
            if (replaced && ReferenceEquals(item.Source.BufferInput, constantBuffer))
            {
                access.Target = value.Buffer.Value;
                access.BufferRange = item.Source.DynamicRange && item.Source.CurrentBufferRange is { } range
                    ? range : value.ResolveRange(item.Source.BufferRange);
                bufferAccessReplaced = true;
            }
            _dynamicAccesses.Add(access);
        }
        if (replaced && !bufferAccessReplaced)
        {
            var pass = new PassDefinition(this, identity, definition.Kind);
            foreach (GraphParameterResourceBinding binding in bindings)
                if (ReferenceEquals(binding.BufferInput, bufferInput))
                    _ = pass.Read(value.Buffer, value.Range, binding.Sync, ResourceAccess.ConstantBuffer);
        }
        foreach (ExplicitPassOrder order in _dynamicOrders.Incident(source.Value))
        {
            if (order.Consumer == source.Value)
                _dynamicOrders.Add(new ExplicitPassOrder(order.Predecessor, identity));
            if (order.Predecessor == source.Value)
                _dynamicOrders.Add(new ExplicitPassOrder(identity, order.Consumer));
        }
        if (!_copiedPasses.TryGetValue(source.Value, out List<GraphIdentity>? copies))
            _copiedPasses.Add(source.Value, copies = []);
        copies.Add(identity);
        return new GraphPassId(identity);
    }
    private sealed record CopyBindings(GraphPass Source, GraphBufferInput? SourceBuffer,
        List<GraphParameterResourceBinding> Bindings, GraphBufferInput? Buffer);

    private readonly Dictionary<(GraphIdentity Region, int Ordinal), CopyBindings> _copyBindings = [];
    private readonly Dictionary<GraphIdentity, int> _copyCounts = [];

    private (List<GraphParameterResourceBinding>, GraphBufferInput?) GetCopyBindings(
        GraphPass source, GraphIdentity region, GraphBufferInput? constantBuffer)
    {
        int ordinal = _copyCounts.GetValueOrDefault(region);
        _copyCounts[region] = ordinal + 1;
        var key = (region, ordinal);
        if (_copyBindings.TryGetValue(key, out CopyBindings? previous) &&
            ReferenceEquals(previous.Source, source) && ReferenceEquals(previous.SourceBuffer, constantBuffer))
            return (previous.Bindings, previous.Buffer);
        List<GraphParameterResourceBinding> bindings = source.ParameterBindings;
        GraphBufferInput? buffer = null;
        if (constantBuffer is not null)
            for (int index = 0; index < bindings.Count; index++)
                if (ReferenceEquals(bindings[index].BufferInput, constantBuffer))
                {
                    DevelopmentCheck.Require(bindings[index].IsBufferRange,
                        "A copied constant buffer must be a range binding.");
                    if (buffer is null)
                    {
                        bindings = new List<GraphParameterResourceBinding>(bindings);
                        buffer = new GraphBufferInput(Graph.Identity, BufferUsages.Constant);
                        _copyBufferInputs.Add(buffer);
                    }
                    bindings[index] = GraphParameterResourceBinding.ConstantBuffer(buffer, bindings[index].Sync);
                }
        if (previous?.Buffer is { } oldBuffer) _copyBufferInputs.Remove(oldBuffer);
        _copyBindings[key] = new CopyBindings(source, constantBuffer, bindings, buffer);
        return (bindings, buffer);
    }

    private void TrimCopyBindings()
    {
        foreach (var key in _copyBindings.Keys.ToArray())
            if (!_copyCounts.TryGetValue(key.Region, out int count) || key.Ordinal >= count)
            {
                if (_copyBindings[key].Buffer is { } buffer) _copyBufferInputs.Remove(buffer);
                _copyBindings.Remove(key);
            }
        _copyCounts.Clear();
    }

    private static void DetachCopyConnections(ref FramePass row)
    {
        if (row.CopyDefinition is null) return;
        row.ParameterBindings = row.ParameterBindings is null ? null : [.. row.ParameterBindings];
        row.PersistentBindings = row.PersistentBindings is null ? null : [.. row.PersistentBindings];
        row.CopyDefinition = null;
    }

    private void DetachCopyConnections(GraphIdentity pass)
    {
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        DetachCopyConnections(ref row);
        _dynamicPasses[index] = row;
    }

    private readonly record struct CopyAccess(PassResourceAccess Source, FrameResourceAccess Access);
    private readonly Dictionary<GraphIdentity, CopyAccess[]> _copyAccesses = [];
    private readonly HashSet<GraphBufferInput> _copyBufferInputs = [];

    private CopyAccess[] GetCopyAccesses(GraphIdentity source, GraphPass definition)
    {
        if (_copyAccesses.TryGetValue(source, out CopyAccess[]? previous)) return previous;
        var accesses = new List<CopyAccess>(definition.Accesses.Count);
        foreach (GraphIdentity accessIdentity in definition.Accesses)
        {
            PassResourceAccess sourceAccess = StructureIndex.Structure.Accesses.Get(accessIdentity);
            GraphIdentity target = sourceAccess.Target;
            BufferRange bufferRange = sourceAccess.BufferRange;
            TextureSubresourceRange textureRange = sourceAccess.TextureRange;
            if (sourceAccess.BufferInput is not null)
            {
                GraphBufferSlice input = ResolveBufferInput(sourceAccess.BufferInput);
                target = input.Buffer.Value;
                bufferRange = input.ResolveRange(bufferRange);
            }
            if (sourceAccess.TextureInput is not null)
            {
                GraphTextureId input = ResolveTextureInput(sourceAccess.TextureInput);
                target = input.Value;
                textureRange = ResolveTextureRange(input,
                    textureRange.MipLevelCount == 0 ? null : textureRange);
            }
            if (sourceAccess.ParameterInput is not null)
            {
                ParameterInputValue input = ResolveParameterInput(sourceAccess.ParameterInput);
                target = sourceAccess.ParameterSecondary ? input.Secondary : input.Resource;
                if (!target.IsValid) continue;
                bufferRange = sourceAccess.ParameterSecondary ? input.SecondaryBufferRange : input.BufferRange;
                textureRange = sourceAccess.ParameterSecondary ? input.SecondaryTextureRange : input.TextureRange;
            }
            if (sourceAccess.DynamicRange)
            {
                bufferRange = sourceAccess.CurrentBufferRange ?? bufferRange;
                textureRange = sourceAccess.CurrentTextureRange ?? textureRange;
            }
            accesses.Add(new CopyAccess(sourceAccess, new FrameResourceAccess
            {
                TargetKind = sourceAccess.TargetKind,
                Target = target,
                Mode = sourceAccess.Mode,
                Coverage = sourceAccess.Coverage,
                Sync = sourceAccess.Sync,
                Access = sourceAccess.Access,
                BufferRange = bufferRange,
                BufferDescriptor = sourceAccess.BufferDescriptor,
                BufferDescriptorOnly = sourceAccess.BufferDescriptorOnly,
                TextureRange = textureRange,
                QueryRange = sourceAccess.QueryRange,
                TextureLayout = sourceAccess.TextureLayout,
                ResultContents = sourceAccess.ResultContents,
                BufferContentScope = sourceAccess.BufferContentScope,
                UnorderedAccessHazards = sourceAccess.UnorderedAccessHazards,
            }));
        }
        CopyAccess[] result = [.. accesses];
        _copyAccesses.Add(source, result);
        return result;
    }

    private void InvalidateCopyAccesses() => _copyAccesses.Clear();

}
