using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IContactNarrowPhases
{
    QueryDefinition AddReads(QueryDefinition definition);
    bool AddResources(QueryReadResources resources) => false;
    void Detect<TPartition>(in PhysicsStepContext context, scoped QueryCursor access, CollisionPairBins<TPartition> bins)
        where TPartition : struct;
}

public readonly struct ContactNarrowPhase<TDetection>(int bin, TDetection detection, QueryReadResources? resources = null) : IContactNarrowPhases
    where TDetection : struct, ICollisionPairQueryDetection
{
    public QueryDefinition AddReads(QueryDefinition definition) => detection.AddReads(definition);
    public bool AddResources(QueryReadResources target)
    {
        if (resources is null) return false;
        target.AddFrom(resources);
        return true;
    }
    public void Detect<TPartition>(in PhysicsStepContext context, scoped QueryCursor access, CollisionPairBins<TPartition> bins)
        where TPartition : struct => CollisionPairQueries.DetectBinWithQuery(access, bins, bin, detection, resources);
}

public static class ContactNarrowPhase
{
    public static ContactNarrowPhase<TDetection> Create<TDetection>(int bin, TDetection detection, QueryReadResources? resources = null)
        where TDetection : struct, ICollisionPairQueryDetection => new(bin, detection, resources);
}

public readonly struct ContactNarrowPhases<TFirst, TSecond>(TFirst first, TSecond second) : IContactNarrowPhases
    where TFirst : struct, IContactNarrowPhases where TSecond : struct, IContactNarrowPhases
{
    public QueryDefinition AddReads(QueryDefinition definition) => second.AddReads(first.AddReads(definition));
    public bool AddResources(QueryReadResources resources) => first.AddResources(resources) | second.AddResources(resources);
    public void Detect<TPartition>(in PhysicsStepContext context, scoped QueryCursor access, CollisionPairBins<TPartition> bins)
        where TPartition : struct
    {
        first.Detect(in context, access, bins);
        second.Detect(in context, access, bins);
    }
}

public static class ContactNarrowPhases
{
    public static ContactNarrowPhases<TFirst, TSecond> Combine<TFirst, TSecond>(TFirst first, TSecond second)
        where TFirst : struct, IContactNarrowPhases where TSecond : struct, IContactNarrowPhases => new(first, second);
}

public static class DetectContactPoints
{
    public static DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> Create<TEnumerable, TPartition, TClassifier, TNarrowPhases>(
        World world, RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, TNarrowPhases narrowPhases, ContactPointBuffer output, ReadOnlySpan<JobResourceAccess> inputs)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition> where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier where TNarrowPhases : struct, IContactNarrowPhases =>
        new(world, pairs, bins, classifier, narrowPhases, output, inputs);
}

/// <summary>Fills the selected type bins once and runs concrete kernels into one reusable contact output.</summary>
public readonly struct DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> : IPhysicsStage, IDisposable
    where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition> where TPartition : struct
    where TClassifier : struct, ICollisionPairClassifier where TNarrowPhases : struct, IContactNarrowPhases
{
    private readonly RelationQuery<TEnumerable, (Entity, Entity)> _pairs;
    private readonly CollisionPairBins<TPartition> _bins;
    private readonly TClassifier _classifier;
    private readonly TNarrowPhases _narrowPhases;
    private readonly ContactPointBuffer _output;
    private readonly JobResourceAccess[] _accesses;
    private readonly JobResourceAccess[] _detectionAccesses;
    private readonly QueryReadResources? _resources;
    private readonly RelationQueryReads _reads;

    public DetectContactPoints(World world, RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, TNarrowPhases narrowPhases, ContactPointBuffer output, ReadOnlySpan<JobResourceAccess> inputs)
    {
        _pairs = pairs;
        _bins = bins;
        _classifier = classifier;
        _narrowPhases = narrowPhases;
        _output = output;
        var resources = new QueryReadResources();
        _resources = narrowPhases.AddResources(resources) ? resources : null;
        _reads = pairs.ReadQueries(narrowPhases.AddReads(classifier.AddReads(QueryDefinition.Empty)));
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, _reads.Access, accesses, _resources);
        accesses.Add(bins.FillAccess);
        accesses.Add(output.WriteAccess);
        foreach (JobResourceAccess input in inputs) accesses.Add(input);
        _accesses = accesses.ToArray();
        accesses.Clear();
        accesses.Add(bins.ReadAccess);
        accesses.Add(output.WriteAccess);
        foreach (JobResourceAccess input in inputs) accesses.Add(input);
        _detectionAccesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        if (_resources is null)
            return scheduler.Schedule(new FillAndDetect(_pairs, _bins, _classifier, _narrowPhases, _output, _reads, context), _accesses, dependency);
        JobHandle filled = scheduler.Schedule(new Fill(_pairs, _bins, _classifier, _output, _reads), _accesses, dependency);
        return _resources.ScheduleEntities(context.World, _reads.Access, new PairEntities(_bins),
            new Detect(_bins, _narrowPhases, _reads, context), _detectionAccesses, filled);
    }

    private readonly struct FillAndDetect(RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, TNarrowPhases narrowPhases, ContactPointBuffer output, RelationQueryReads reads,
        PhysicsStepContext context) : IJob, IRelationQueryExecution
    {
        public void Execute()
        {
            output.Clear();
            var work = this;
            reads.Execute(ref work);
        }
        public void Execute(scoped RelationQueryViews views)
        {
            pairs.FillPairBins(bins, classifier, views);
            narrowPhases.Detect(in context, views.Access, bins);
        }
    }

    private readonly struct Fill(RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, ContactPointBuffer output, RelationQueryReads reads) : IJob, IRelationQueryExecution
    {
        public void Execute()
        {
            output.Clear();
            var work = this;
            reads.Execute(ref work);
        }

        public void Execute(scoped RelationQueryViews views) => pairs.FillPairBins(bins, classifier, views);
    }

    private readonly struct Detect(CollisionPairBins<TPartition> bins, TNarrowPhases narrowPhases,
        RelationQueryReads reads, PhysicsStepContext context) : IJob, IRelationQueryExecution
    {
        public void Execute()
        {
            var work = this;
            reads.Execute(ref work);
        }

        public void Execute(scoped RelationQueryViews views) => narrowPhases.Detect(in context, views.Access, bins);
    }

    private readonly struct PairEntities(CollisionPairBins<TPartition> bins) : IQueryReadEntities
    {
        public int Count => bins.Count * 2;
        public Entity GetEntity(int index)
        {
            ref readonly CollisionGeometryPair pair = ref bins.ReadPairs()[index / 2];
            return (index & 1) == 0 ? pair.First : pair.Second;
        }
    }

    public void Dispose() => _reads.Dispose();
}
