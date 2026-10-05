namespace SomeEngine.RenderGraph;

public ref partial struct RenderGraphEdit
{
    /// <summary>Copies the current declarations into a retained region and shares the
    /// source's command data. The replacement constant input supplies copy-specific values.</summary>
    public GraphPassId AddPassCopy(GraphPassId source, string label, GraphRegionId region,
        GraphBufferInput? constantBuffer = null, GraphBufferInput? replacement = null)
    {
        ValidatePass(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        GraphPass original = Structure.Passes.Get(source.Value);
        GraphPass copy = original.Clone();
        copy.Label = label;
        copy.CommandSource = original.CommandSource.IsValid ? original.CommandSource : source.Value;
        copy.Accesses = [];
        copy.Region = default;
        copy.Options = copy.Options with { RepeatCommands = false };
        if (constantBuffer is not null && replacement is not null)
            for (int index = 0; index < copy.ParameterBindings.Count; index++)
                if (ReferenceEquals(copy.ParameterBindings[index].BufferInput, constantBuffer))
                    copy.ParameterBindings[index] = GraphParameterResourceBinding.ConstantBuffer(
                        replacement, copy.ParameterBindings[index].Sync);
        GraphIdentity identity = Staging.Passes.Add(Graph.Identity, copy);
        SetPassRegion(identity, region);
        Changes.TouchPass(identity, membership: true);
        foreach (GraphIdentity access in original.Accesses)
        {
            PassResourceAccess declaration = Structure.Accesses.Get(access).Clone();
            declaration.Pass = identity;
            if (constantBuffer is not null && replacement is not null &&
                ReferenceEquals(declaration.BufferInput, constantBuffer)) declaration.BufferInput = replacement;
            GraphIdentity copied = Staging.Accesses.Add(Graph.Identity, declaration);
            Staging.Passes.GetMutable(identity).Accesses.Add(copied);
            Changes.TouchAccess(copied, identity, declaration.Target, membership: true);
        }
        return new GraphPassId(identity);
    }

    /// <summary>Returns the retained region's passes in declaration order.</summary>
    public GraphPassId[] GetRegionPasses(GraphRegionId region)
    {
        GraphStructure structure = Structure;
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
}
