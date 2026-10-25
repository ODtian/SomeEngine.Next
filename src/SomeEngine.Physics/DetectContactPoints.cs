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
    void Detect<TPartition>(scoped QueryCursor access, CollisionPairBins<TPartition> bins)
        where TPartition : struct;
}

public readonly struct ContactNarrowPhase<TDetection>(int bin, TDetection detection, QueryReadResources? resources = null) : IContactNarrowPhases
    where TDetection : struct, ICollisionPairQueryDetection
{
    public QueryDefinition AddReads(QueryDefinition definition) => detection.AddReads(definition);
    public void Detect<TPartition>(scoped QueryCursor access, CollisionPairBins<TPartition> bins)
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
    public void Detect<TPartition>(scoped QueryCursor access, CollisionPairBins<TPartition> bins)
        where TPartition : struct
    {
        first.Detect(access, bins);
        second.Detect(access, bins);
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
    private readonly RelationQueryReads _reads;

    public DetectContactPoints(World world, RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, TNarrowPhases narrowPhases, ContactPointBuffer output, ReadOnlySpan<JobResourceAccess> inputs)
    {
        _pairs = pairs;
        _bins = bins;
        _classifier = classifier;
        _narrowPhases = narrowPhases;
        _output = output;
        _reads = pairs.ReadQueries(narrowPhases.AddReads(classifier.AddReads(QueryDefinition.Empty)));
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, _reads.Access, accesses);
        accesses.Add(bins.FillAccess);
        accesses.Add(output.WriteAccess);
        foreach (JobResourceAccess input in inputs) accesses.Add(input);
        _accesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new Detect(_pairs, _bins, _classifier, _narrowPhases, _output, _reads), _accesses, dependency);

    private readonly struct Detect(RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, TNarrowPhases narrowPhases, ContactPointBuffer output, RelationQueryReads reads) : IJob, IRelationQueryExecution
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
            narrowPhases.Detect(views.Access, bins);
        }
    }

    public void Dispose() => _reads.Dispose();
}
