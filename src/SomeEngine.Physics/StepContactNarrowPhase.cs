using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

public interface IPhysicsCollisionPairDetection
{
    QueryDefinition AddReads(QueryDefinition definition);
    void Execute(in CollisionGeometryPair pair, scoped QueryCursor access, scoped QueryRow first, scoped QueryRow second,
        in PhysicsStepContext context);
}

public static class StepContactNarrowPhase
{
    public static StepContactNarrowPhase<TDetection> Create<TDetection>(int bin, TDetection detection, QueryReadResources? resources = null)
        where TDetection : struct, IPhysicsCollisionPairDetection => new(bin, detection, resources);
}

public readonly struct StepContactNarrowPhase<TDetection>(int bin, TDetection detection, QueryReadResources? resources = null) : IContactNarrowPhases
    where TDetection : struct, IPhysicsCollisionPairDetection
{
    public QueryDefinition AddReads(QueryDefinition definition) => detection.AddReads(definition);
    public bool AddResources(QueryReadResources target)
    {
        if (resources is null) return false;
        target.AddFrom(resources);
        return true;
    }
    public void Detect<TPartition>(in PhysicsStepContext context, scoped QueryCursor access, CollisionPairBins<TPartition> bins)
        where TPartition : struct =>
        CollisionPairQueries.DetectBinWithQuery(access, bins, bin, new StepDetection(detection, context), resources);

    private readonly struct StepDetection(TDetection detection, PhysicsStepContext context) : ICollisionPairQueryDetection
    {
        private readonly PhysicsStepContext _context = context;
        public QueryDefinition AddReads(QueryDefinition definition) => detection.AddReads(definition);
        public void Execute(in CollisionGeometryPair pair, scoped QueryCursor access, scoped QueryRow first, scoped QueryRow second) =>
            detection.Execute(in pair, access, first, second, in _context);
    }
}
