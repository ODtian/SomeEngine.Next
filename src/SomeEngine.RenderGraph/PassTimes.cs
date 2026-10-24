namespace SomeEngine.RenderGraph;

internal readonly record struct PassTimeMeasurement(GraphQueryPoolId Pool, uint FirstQuery, GraphIdentity Access);

public ref partial struct RenderGraphEdit
{
    /// <summary>Writes two timestamps around this pass's commands whenever the pass executes.</summary>
    public void MeasurePassTime(GraphPassId pass, GraphQueryPoolId pool, uint firstQuery)
    {
        ValidatePass(pass);
        ValidateOwner(pool.Value);
        DevelopmentCheck.Require(Structure.QueryPools.Get(pool.Value).Resource.Description.Type == QueryType.Timestamp,
            "Pass time measurements require timestamp queries.");
        GraphIdentity access = AddQueryAccess(pass.Value, pool, GraphAccessMode.Write, WriteCoverage.Complete,
            new QueryRange(firstQuery, 2), ResourceContentState.Defined);
        Staging.Passes.GetMutable(pass.Value).TimeMeasurements.Add(new(pool, firstQuery, access));
        Changes.TouchPass(pass.Value);
    }

    /// <summary>Returns persistent passes in the region's declaration order, including child regions.</summary>
    public GraphPassId[] GetPasses(GraphRegionId region)
    {
        ValidateRegion(region);
        var passes = new List<GraphPassId>();
        Add(Structure, region.Value, passes);
        return passes.ToArray();

        static void Add(GraphStructure structure, GraphIdentity region, List<GraphPassId> passes)
        {
            foreach (GraphRegionEntry child in structure.GetRegion(region).Children)
                if (child.Identity.Domain == GraphIdentityDomain.Region) Add(structure, child.Identity, passes);
                else if (child.Identity.Domain == GraphIdentityDomain.Pass) passes.Add(new(child.Identity));
        }
    }

    private void RemovePassTimes(GraphQueryPoolId pool)
    {
        for (int index = 0; index < Structure.Passes.Identities.Count; index++)
        {
            GraphIdentity identity = Structure.Passes.Identities[index];
            GraphPass pass = Structure.Passes.Get(identity);
            foreach (PassTimeMeasurement time in pass.TimeMeasurements.ToArray())
            {
                if (time.Pool != pool) continue;
                RemoveAccess(time.Access);
                Staging.Passes.GetMutable(identity).TimeMeasurements.Remove(time);
                Changes.TouchPass(identity);
            }
        }
    }
}
