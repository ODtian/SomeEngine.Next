namespace SomeEngine.RenderGraph;

internal sealed partial class RenderGraphFrameState
{
    private readonly Dictionary<GraphIdentity, bool> _passEnabled = [];
    private readonly Dictionary<GraphIdentity, bool> _regionEnabled = [];

    internal void SetRegionEnabled(GraphRegionId region, bool enabled)
    {
        EnsureEditable();
        GraphStructure structure = StructureIndex.Structure;
        DevelopmentCheck.Require(region == default ||
            region.Value.ScopeId == Graph.Identity && structure.Regions.Contains(region.Value),
            "The region must belong to this frame's graph structure.");
        GraphRegion row = structure.GetRegion(region.Value);
        bool current = _regionEnabled.GetValueOrDefault(region.Value, row.Enabled);
        if (current == enabled) return;
        _regionEnabled[region.Value] = enabled;
        ChangeRegionPasses(row, _executor.ChangePass);
    }

    internal void SetPassEnabled(GraphPassId pass, bool enabled)
    {
        EnsureEditable();
        ValidateStatic(pass.Value, StructureIndex.Structure.Passes);
        GraphPass row = StructureIndex.Structure.Passes.Get(pass.Value);
        bool current = _passEnabled.GetValueOrDefault(pass.Value, row.Enabled);
        if (current == enabled) return;
        _passEnabled[pass.Value] = enabled;
        _executor.ChangePass(pass.Value);
    }

    internal bool IsRegionEnabled(GraphRegionId region)
    {
        GraphIdentity identity = region.Value;
        while (true)
        {
            GraphRegion row = StructureIndex.Structure.GetRegion(identity);
            if (!_regionEnabled.GetValueOrDefault(identity, row.Enabled)) return false;
            if (!identity.IsValid) return true;
            identity = row.Parent;
        }
    }

    internal bool IsPassEnabled(GraphIdentity identity)
    {
        if (identity.ScopeId == Identity)
        {
            FramePass framePass = _dynamicPasses[identity.Index];
            return framePass.Enabled && IsRegionEnabled(new(framePass.Region));
        }
        GraphPass row = StructureIndex.Structure.Passes.Get(identity);
        return _passEnabled.GetValueOrDefault(identity, row.Enabled) && IsRegionEnabled(new(row.Region));
    }

    internal bool IsPassEnabled(GraphPassId pass)
    {
        EnsureEditable();
        ValidateStatic(pass.Value, StructureIndex.Structure.Passes);
        GraphPass row = StructureIndex.Structure.Passes.Get(pass.Value);
        return IsPassEnabled(pass.Value) && HasBufferUploads(row.CallbackStorage);
    }

    private void ChangeRegionPasses(GraphRegion region, Action<GraphIdentity> changed)
    {
        foreach (GraphRegionEntry child in region.Children)
        {
            if (child.Identity.Domain == GraphIdentityDomain.Region)
                ChangeRegionPasses(StructureIndex.Structure.Regions.Get(child.Identity), changed);
            else if (child.Identity.Domain == GraphIdentityDomain.Pass)
                changed(child.Identity);
        }
    }

    private void AdoptEnabledValues()
    {
        GraphStructure structure = StructureIndex.Structure;
        foreach ((GraphIdentity identity, bool enabled) in _passEnabled)
        {
            GraphPass row = structure.Passes.Get(identity);
            if (row.Enabled == enabled) continue;
            row.Enabled = enabled;
            Graph.PropertyChanged(identity, GraphStoreKind.Passes, currentFrame: Resources);
        }
        foreach ((GraphIdentity identity, bool enabled) in _regionEnabled)
        {
            GraphRegion row = structure.GetRegion(identity);
            if (row.Enabled == enabled) continue;
            row.Enabled = enabled;
            ChangeRegionPasses(row, pass => Graph.PropertyChanged(pass, GraphStoreKind.Passes, currentFrame: Resources));
        }
    }
}
