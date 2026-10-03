namespace SomeEngine.RenderGraph;

internal readonly record struct GraphRegionEntry(GraphIdentity Identity, long Start, long End);

internal sealed class GraphRegion
{
    internal required string Label;
    internal GraphIdentity Parent;
    internal bool IsFrame;
    internal bool Enabled = true;
    internal ImmutableList<GraphRegionEntry> Children = [];

    internal GraphRegion Clone() => (GraphRegion)MemberwiseClone();
}

internal sealed partial class GraphStructure
{
    internal GraphRegion GetRegion(GraphIdentity identity) =>
        identity.IsValid ? Regions.Get(identity) : RootRegion;

    internal GraphRegion GetMutableRegion(GraphIdentity identity) =>
        identity.IsValid ? Regions.GetMutable(identity) : RootRegion;

    internal GraphRegionEntry GetRegionInterval(GraphIdentity identity)
    {
        if (!identity.IsValid) return new GraphRegionEntry(default, 0, long.MaxValue / 2);
        GraphRegion region = Regions.Get(identity);
        GraphRegion parent = GetRegion(region.Parent);
        int index = parent.Children.FindIndex(child => child.Identity == identity);
        return parent.Children[index];
    }

    internal bool IsRegionEnabled(GraphIdentity identity)
    {
        while (identity.IsValid)
        {
            GraphRegion region = Regions.Get(identity);
            if (!region.Enabled) return false;
            identity = region.Parent;
        }
        return RootRegion.Enabled;
    }
}

public ref partial struct RenderGraphEdit
{
    // Frame row counts determine the declaration space reserved for frame passes.
    private const long FrameRegionMinimumWidth = (long)int.MaxValue + 1;

    public GraphRegionId AddRegion(string label, GraphRegionId parent = default) =>
        AddRegion(label, parent, isFrame: false);

    public GraphRegionId AddFrameRegion(string label, GraphRegionId parent = default) =>
        AddRegion(label, parent, isFrame: true);

    private GraphRegionId AddRegion(string label, GraphRegionId parent, bool isFrame)
    {
        DevelopmentCheck.Require(!string.IsNullOrWhiteSpace(label), "A region requires a label.");
        ValidateRegion(parent);
        DevelopmentCheck.Require(!Structure.GetRegion(parent.Value).IsFrame,
            "Child regions require a persistent parent region.");
        var region = new GraphRegion { Label = label, Parent = parent.Value, IsFrame = isFrame };
        GraphIdentity identity = Staging.Regions.Add(Graph.Identity, region);
        _ = AddRegionEntry(parent.Value, identity);
        Changes.TouchRegion(identity);
        return new GraphRegionId(identity);
    }

    public void Remove(GraphRegionId region)
    {
        DevelopmentCheck.Require(region != default, "The root region must remain in the graph.");
        ValidateRegion(region);
        GraphRegion row = Structure.Regions.Get(region.Value);
        foreach (GraphRegionEntry child in row.Children)
        {
            if (child.Identity.Domain == GraphIdentityDomain.Region)
                Remove(new GraphRegionId(child.Identity));
            else
                Remove(new GraphPassId(child.Identity));
        }
        RemoveRegionEntry(row.Parent, region.Value);
        _ = Staging.Regions.Remove(region.Value);
        Changes.TouchRegion(region.Value);
    }

    private void SetPassRegion(GraphIdentity identity, GraphRegionId region)
    {
        ValidateRegion(region);
        DevelopmentCheck.Require(!Structure.GetRegion(region.Value).IsFrame,
            "Persistent passes require a persistent region.");
        GraphPass pass = Staging.Passes.GetMutable(identity);
        pass.Region = region.Value;
        pass.DeclarationOrdinal = AddRegionEntry(region.Value, identity).Start;
    }

    private GraphRegionEntry AddRegionEntry(GraphIdentity parent, GraphIdentity identity)
    {
        GraphRegion region = Staging.GetMutableRegion(parent);
        GraphRegionEntry interval = Structure.GetRegionInterval(parent);
        long previous = region.Children.Count == 0 ? interval.Start : region.Children[^1].End;
        long width = (interval.End - previous) / 4;
        long minimumWidth = identity.Domain == GraphIdentityDomain.Region ? RegionPositionCount(identity) : 1;
        GraphRegionEntry entry;
        if (region.Children.Count == 0 && identity.Domain == GraphIdentityDomain.Region &&
            interval.End - interval.Start > minimumWidth + 2)
            entry = new GraphRegionEntry(identity, interval.Start + 1, interval.End - 1);
        else if (width >= minimumWidth)
            entry = new GraphRegionEntry(identity, previous + width, previous + width * 2);
        else
        {
            region.Children = region.Children.Add(new GraphRegionEntry(identity, 0, 0));
            RecompileRegion(parent, interval.Start, interval.End);
            return Staging.GetRegion(parent).Children[^1];
        }
        region.Children = region.Children.Add(entry);
        Changes.TouchRegion(parent);
        return entry;
    }

    private long RegionPositionCount(GraphIdentity identity)
    {
        if (identity.Domain != GraphIdentityDomain.Region) return 1;
        GraphRegion region = Structure.Regions.Get(identity);
        if (region.IsFrame) return FrameRegionMinimumWidth;
        long count = region.Children.Count + 1L;
        foreach (GraphRegionEntry child in region.Children)
            count = checked(count + RegionPositionCount(child.Identity));
        return Math.Max(3, count);
    }

    private void RecompileRegion(GraphIdentity identity, long start, long end)
    {
        GraphRegion region = Staging.GetMutableRegion(identity);
        long count = region.Children.Count + 1L;
        foreach (GraphRegionEntry child in region.Children)
            count = checked(count + RegionPositionCount(child.Identity));
        if (end - start < count)
        {
            ExtendRegionInterval(identity, count);
            GraphRegionEntry interval = Structure.GetRegionInterval(identity);
            start = interval.Start;
            end = interval.End;
        }
        long step = (end - start) / count;
        long position = start;
        var children = ImmutableList.CreateBuilder<GraphRegionEntry>();
        foreach (GraphRegionEntry child in region.Children)
        {
            long childStart = position + step;
            long childEnd = childStart + RegionPositionCount(child.Identity) * step;
            children.Add(new GraphRegionEntry(child.Identity, childStart, childEnd));
            if (child.Identity.Domain == GraphIdentityDomain.Region)
                RecompileRegion(child.Identity, childStart, childEnd);
            else if (child.Identity.Domain == GraphIdentityDomain.Pass)
            {
                GraphPass pass = Staging.Passes.GetMutable(child.Identity);
                if (pass.DeclarationOrdinal != childStart)
                {
                    pass.DeclarationOrdinal = childStart;
                    Changes.TouchPass(child.Identity);
                }
            }
            position = childEnd;
        }
        region.Children = children.ToImmutable();
        Changes.TouchRegion(identity);
    }

    private void ExtendRegionInterval(GraphIdentity identity, long count)
    {
        if (!identity.IsValid)
            throw new InvalidOperationException("The graph declaration sequence is exhausted.");
        GraphRegion region = Staging.GetMutableRegion(identity);
        GraphRegion parent = Staging.GetMutableRegion(region.Parent);
        int index = parent.Children.FindIndex(child => child.Identity == identity);
        GraphRegionEntry interval = Structure.GetRegionInterval(region.Parent);
        long start = index == 0 ? interval.Start + 1 : parent.Children[index - 1].End + 1;
        long end = index + 1 == parent.Children.Count ? interval.End - 1 : parent.Children[index + 1].Start - 1;
        if (end - start < count)
        {
            RecompileRegion(region.Parent, interval.Start, interval.End);
            return;
        }
        parent.Children = parent.Children.SetItem(index, new GraphRegionEntry(identity, start, end));
        Changes.TouchRegion(identity);
        Changes.TouchRegion(region.Parent);
    }

    private void RemoveRegionEntry(GraphIdentity parent, GraphIdentity identity)
    {
        GraphRegion region = Staging.GetMutableRegion(parent);
        int index = region.Children.FindIndex(entry => entry.Identity == identity);
        region.Children = region.Children.RemoveAt(index);
        Changes.TouchRegion(parent);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void ValidateRegion(GraphRegionId region)
    {
        if (region == default) return;
        DevelopmentCheck.Require(region.Value.Owner == Graph.Identity && Structure.Regions.Contains(region.Value),
            "The region must belong to this graph structure.");
    }
}

public ref partial struct RenderGraphFrame
{
    public void SetRegionEnabled(GraphRegionId region, bool enabled) => State.SetRegionEnabled(region, enabled);
}
