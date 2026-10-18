using System.Diagnostics;
using SomeEngine.ECS.Benchmarks.Relations;
using SomeEngine.ECS.Benchmarks.Relations.Bitmap;
using SomeEngine.ECS.Benchmarks.Relations.Spatial;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Relations.Benchmarks;

public abstract class MeasurementSide : IDisposable
{
    private readonly JobResourceToken _outputResource;
    private readonly JobResourceToken _contactResource;

    protected MeasurementSide(MeasurementData data)
    {
        Data = data;
        if (data.Input.ChangesEveryFrame)
        {
            _outputResource = data.Jobs.CreateResourceToken("Relation measurement pairs");
            _contactResource = data.Jobs.CreateResourceToken("Relation measurement contacts");
        }
    }

    public MeasurementData Data { get; }
    public long SetupTicks { get; set; }
    public long IndexBuildTicks { get; set; }
    public abstract int Count { get; }
    public abstract ReadOnlySpan<(Entity, Entity)> Pairs { get; }
    public virtual ReadOnlySpan<int> ContactCounts => Data.ContactCounts;
    public virtual ReadOnlySpan<float> Depths => Data.Depths;
    protected JobResourceAccess OutputWrite => JobResourceAccess.Write(_outputResource);
    protected JobResourceAccess OutputRead => JobResourceAccess.Read(_outputResource);
    protected JobResourceAccess ContactWrite => JobResourceAccess.Write(_contactResource);
    public abstract void Query();
    public virtual JobHandle ScheduleMaintenance(JobHandle dependency = default) => dependency;
    public virtual JobHandle ScheduleQuery(JobHandle dependency = default)
    {
        dependency.Complete();
        Query();
        return default;
    }
    public virtual JobHandle ScheduleConsume(JobHandle dependency = default)
    {
        dependency.Complete();
        Consume();
        return default;
    }

    public virtual void Consume()
    {
        Array.Clear(Data.ContactCounts);
        Array.Clear(Data.Depths);
        if (Data.Input.HasSpace)
            SpatialCollisionConsumption.Measure(Data.World, Data.Members, Pairs, Data.ContactCounts, Data.Depths);
        else
            BitmapCollisionConsumption.CountContacts(Pairs, Data.ContactCounts);
    }

    public virtual IndexMemory Memory() => IndexMemory.Read(Data, Count);

    public virtual void Dispose()
    {
        if (Data.Input.ChangesEveryFrame)
        {
            Data.Jobs.ReleaseResourceToken(_outputResource);
            Data.Jobs.ReleaseResourceToken(_contactResource);
        }
        Data.Dispose();
    }

    protected readonly struct QueryJob(MeasurementSide side) : IJob
    {
        public void Execute() => side.Query();
    }

    protected readonly struct ConsumeJob(MeasurementSide side) : IJob
    {
        public void Execute() => side.Consume();
    }
}

public class RelationArraySide<TEnumerable>(MeasurementData data,
    RelationQuery<TEnumerable, (Entity, Entity)> query) : MeasurementSide(data)
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
{
    private int _count;
    public override int Count => _count;
    public override ReadOnlySpan<(Entity, Entity)> Pairs => Data.Output.AsSpan(0, _count);

    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        query.Enumerate(ref output);
        _count = output.Count;
    }

}

public sealed class BitmapRelationArraySide<TEnumerable, TRelation, TImplementation, TPermission>(
    MeasurementData data, RelationQuery<TEnumerable, (Entity, Entity)> query,
    CollisionBitmapSummary<TRelation, TImplementation, Body, TPermission> summary)
    : RelationArraySide<TEnumerable>(data, query)
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TRelation : struct, IRelation<Body, Body>
    where TImplementation : struct, IBitmapRelation<TRelation, Body, TPermission>
    where TPermission : struct, ICategoryPermission
{
    public override IndexMemory Memory() => IndexMemory.Read(Data, Count, summary.Tree, summary.NodeCapacity);
}

public sealed class CharacterRelationArraySide<TEnumerable>(MeasurementData data,
    RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionKeySummary<Hair, Entity> summary)
    : RelationArraySide<TEnumerable>(data, query)
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
{
    public override IndexMemory Memory() => IndexMemory.Read(Data, Count, summary.Tree, keyCapacity: summary.NodeCapacity);
}

public abstract class HandwrittenArraySide(MeasurementData data) : MeasurementSide(data)
{
    protected int PairCount;
    public override int Count => PairCount;
    public override ReadOnlySpan<(Entity, Entity)> Pairs => Data.Output.AsSpan(0, PairCount);
}

public sealed class HandwrittenOneWayCandidatesSide(MeasurementData data) : HandwrittenArraySide(data)
{
    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        HandwrittenBitmapCollision.OneWay(Data.World, Data.Members, Data.Candidates, ref output);
        PairCount = output.Count;
    }
}

public sealed class HandwrittenMutualCandidatesSide(MeasurementData data) : HandwrittenArraySide(data)
{
    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        HandwrittenBitmapCollision.Mutual(Data.World, Data.Members, Data.Candidates, ref output);
        PairCount = output.Count;
    }
}

public sealed class HandwrittenDefaultSide(MeasurementData data) : HandwrittenArraySide(data)
{
    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        HandwrittenDefaultPairs.Enumerate(Data.World, Data.Members, ref output);
        PairCount = output.Count;
    }
}

public sealed class HandwrittenCharacterSetSide(MeasurementData data) : HandwrittenArraySide(data)
{
    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        HandwrittenKeyPairs<Hair, Entity>.Except(Data.World, Data.Members, ref output);
        PairCount = output.Count;
    }
}

public sealed class HandwrittenOneWaySide(MeasurementData data, CollisionTree tree,
    CollisionBitmapSummary<OneWayCategories, OneWayCategoryBitmap, Body, OneWayPermission> summary)
    : MeasurementSide(data)
{
    private int _count;
    public override int Count => _count;
    public override ReadOnlySpan<(Entity, Entity)> Pairs => Data.Output.AsSpan(0, _count);
    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        HandwrittenSpatialBitmap.OneWay(Data.World, Data.Members, tree, summary.Nodes, ref output);
        _count = output.Count;
    }
    public override IndexMemory Memory() => IndexMemory.Read(Data, Count, tree, summary.NodeCapacity);
}

public sealed class HandwrittenMutualSide(MeasurementData data, CollisionTree tree,
    CollisionBitmapSummary<MutualCategories, MutualCategoryBitmap, Body, MutualPermission> summary)
    : MeasurementSide(data)
{
    private int _count;
    public override int Count => _count;
    public override ReadOnlySpan<(Entity, Entity)> Pairs => Data.Output.AsSpan(0, _count);
    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        HandwrittenSpatialBitmap.Mutual(Data.World, Data.Members, tree, summary.Nodes, ref output);
        _count = output.Count;
    }
    public override IndexMemory Memory() => IndexMemory.Read(Data, Count, tree, summary.NodeCapacity);
}

public sealed class HandwrittenCharacterSide(MeasurementData data, CollisionTree tree,
    CollisionKeySummary<Hair, Entity> summary) : MeasurementSide(data)
{
    private int _count;
    public override int Count => _count;
    public override ReadOnlySpan<(Entity, Entity)> Pairs => Data.Output.AsSpan(0, _count);
    public override void Query()
    {
        var output = new SpatialPairOutput(Data.Output);
        HandwrittenSpatialKeys.Except(Data.World, Data.Members, tree, summary.Nodes, ref output);
        _count = output.Count;
    }
    public override IndexMemory Memory() => IndexMemory.Read(Data, Count, tree, keyCapacity: summary.NodeCapacity);
}

public sealed class MaintainedRelationSide<TFilter, TGroupResults> : MeasurementSide
    where TFilter : struct, ISpatialFilter<TGroupResults>
    where TGroupResults : unmanaged
{
    private readonly CollisionMaintenance<Body, BodyBoundsReader, TFilter, TGroupResults> _maintenance;
    private readonly CollisionBitmapSummary<MutualCategories, MutualCategoryBitmap, Body, MutualPermission> _categories;
    private readonly CollisionKeySummary<Hair, Entity> _keys;
    private readonly CollisionPairBuffer? _pairs;
    private readonly SpatialContactTotals? _totals;
    private int _count;

    public MaintainedRelationSide(MeasurementData data,
        CollisionMaintenance<Body, BodyBoundsReader, TFilter, TGroupResults> maintenance,
        CollisionBitmapSummary<MutualCategories, MutualCategoryBitmap, Body, MutualPermission> categories,
        CollisionKeySummary<Hair, Entity> keys) : base(data)
    {
        _maintenance = maintenance;
        _categories = categories;
        _keys = keys;
        if (data.Input.Class == "threads")
        {
            _pairs = new CollisionPairBuffer(data.Jobs, data.Input.PartitionCapacity);
            _totals = new SpatialContactTotals(data.Jobs, data.Entities.Length + data.Characters.Length + 32);
        }
    }

    public override int Count => _pairs?.Count ?? _count;
    public override ReadOnlySpan<(Entity, Entity)> Pairs => _pairs is { } pairs ? pairs.Pairs : Data.Output.AsSpan(0, _count);
    public override ReadOnlySpan<int> ContactCounts => _totals is { } totals ? totals.ContactCounts : Data.ContactCounts;
    public override ReadOnlySpan<float> Depths => _totals is { } totals ? totals.PenetrationDepths : Data.Depths;
    public override JobHandle ScheduleMaintenance(JobHandle dependency = default) => _maintenance.Schedule(dependency);

    public override void Query()
    {
        if (_pairs is { } pairs)
        {
            if (Data.Input.Parallel)
                _maintenance.Query.FillPairsParallel(pairs);
            else
                _maintenance.Query.FillPairs(pairs);
        }
        else
        {
            var output = new SpatialPairOutput(Data.Output);
            _maintenance.Query.Enumerate(ref output);
            _count = output.Count;
        }
    }

    public override JobHandle ScheduleQuery(JobHandle dependency = default)
    {
        if (_pairs is { } pairs)
            return Data.Input.Parallel ? SpatialCollisionFrames.SchedulePairsParallel(_maintenance, pairs, dependency)
                : SpatialCollisionFrames.SchedulePairs(_maintenance, pairs, dependency);
        var job = new QueryJob(this);
        ReadOnlySpan<JobResourceAccess> accesses = [OutputWrite];
        return _maintenance.ScheduleRead(in job, accesses, dependency);
    }

    public override JobHandle ScheduleConsume(JobHandle dependency = default)
    {
        if (_pairs is { } pairs)
            return SpatialCollisionFrames.ScheduleConsume(_maintenance, pairs, _totals!, dependency);
        var job = new ConsumeJob(this);
        ReadOnlySpan<JobResourceAccess> accesses = [OutputRead, ContactWrite];
        return _maintenance.ScheduleRead(in job, accesses, dependency);
    }

    public override void Consume()
    {
        if (_totals is { } totals)
        {
            // The scheduling fixture performs the same clear and geometric consumption.
            SpatialCollisionFrames.ScheduleConsume(_maintenance, _pairs!, totals).Complete();
        }
        else
            base.Consume();
    }

    public override IndexMemory Memory() => IndexMemory.Read(Data, Count, _maintenance.Tree,
        _categories.NodeCapacity, _keys.NodeCapacity, _pairs, _maintenance.AccessCount,
        _totals?.ContactCounts.Length ?? 0);

    public override void Dispose()
    {
        _pairs?.Dispose();
        _totals?.Dispose();
        _maintenance.Dispose();
        base.Dispose();
    }
}

public sealed class MaintainedHandwrittenSide : MeasurementSide
{
    private readonly HandwrittenSpatialMaintenance _maintenance;
    private readonly CollisionPairBuffer? _pairs;
    private readonly SpatialContactTotals? _totals;
    private int _count;

    public MaintainedHandwrittenSide(MeasurementData data, HandwrittenSpatialMaintenance maintenance) : base(data)
    {
        _maintenance = maintenance;
        if (data.Input.Class == "threads")
        {
            _pairs = new CollisionPairBuffer(data.Jobs, data.Input.PartitionCapacity);
            _totals = new SpatialContactTotals(data.Jobs, data.Entities.Length + data.Characters.Length + 32);
        }
    }

    public override int Count => _pairs?.Count ?? _count;
    public override ReadOnlySpan<(Entity, Entity)> Pairs => _pairs is { } pairs ? pairs.Pairs : Data.Output.AsSpan(0, _count);
    public override ReadOnlySpan<int> ContactCounts => _totals is { } totals ? totals.ContactCounts : Data.ContactCounts;
    public override ReadOnlySpan<float> Depths => _totals is { } totals ? totals.PenetrationDepths : Data.Depths;
    public override JobHandle ScheduleMaintenance(JobHandle dependency = default) => _maintenance.Schedule(dependency);

    public override void Query()
    {
        if (_pairs is { } pairs)
        {
            if (Data.Input.Parallel)
                _maintenance.Query.FillPairsParallel(pairs);
            else
                _maintenance.Query.FillPairs(pairs);
        }
        else
        {
            var output = new SpatialPairOutput(Data.Output);
            _maintenance.Enumerate(ref output);
            _count = output.Count;
        }
    }

    public override JobHandle ScheduleQuery(JobHandle dependency = default)
    {
        if (_pairs is { } pairs)
            return Data.Input.Parallel ? SpatialCollisionFrames.SchedulePairsParallel(_maintenance, pairs, dependency)
                : SpatialCollisionFrames.SchedulePairs(_maintenance, pairs, dependency);
        var job = new QueryJob(this);
        ReadOnlySpan<JobResourceAccess> accesses = [OutputWrite];
        return _maintenance.ScheduleRead(in job, accesses, dependency);
    }

    public override JobHandle ScheduleConsume(JobHandle dependency = default)
    {
        if (_pairs is { } pairs)
            return SpatialCollisionFrames.ScheduleConsume(_maintenance, pairs, _totals!, dependency);
        var job = new ConsumeJob(this);
        ReadOnlySpan<JobResourceAccess> accesses = [OutputRead, ContactWrite];
        return _maintenance.ScheduleRead(in job, accesses, dependency);
    }

    public override void Consume()
    {
        if (_totals is { } totals)
            SpatialCollisionFrames.ScheduleConsume(_maintenance, _pairs!, totals).Complete();
        else
            base.Consume();
    }

    public override IndexMemory Memory() => IndexMemory.Read(Data, Count, _maintenance.Tree,
        _maintenance.Categories.NodeCapacity, _maintenance.Keys.NodeCapacity, _pairs, _maintenance.AccessCount,
        _totals?.ContactCounts.Length ?? 0);

    public override void Dispose()
    {
        _pairs?.Dispose();
        _totals?.Dispose();
        _maintenance.Dispose();
        base.Dispose();
    }
}

public static class MeasurementSides
{
    public static MeasurementSide Create(MeasurementCase input, bool relation)
    {
        long started = Stopwatch.GetTimestamp();
        var data = new MeasurementData(input);
        long created = Stopwatch.GetTimestamp();
        MeasurementSide side = Build(data, relation);
        side.SetupTicks = created - started;
        side.IndexBuildTicks = Stopwatch.GetTimestamp() - created;
        return side;
    }

    private static MeasurementSide Build(MeasurementData data, bool relation)
    {
        MeasurementCase input = data.Input;
        if (input.Class == "finite")
        {
            if (!relation)
                return input.Mutual ? new HandwrittenMutualCandidatesSide(data) : new HandwrittenOneWayCandidatesSide(data);
            var source = BitmapCollisionExample.Candidates(data.World, data.Members, data.Candidates);
            return input.Mutual
                ? Array(data, source.Where(default(MutualContacts), default(MutualContactBitmap)))
                : Array(data, source.Where(default(OneWayContacts), default(OneWayContactBitmap)));
        }
        if (input.Class == "default")
            return relation ? Array(data, data.World.Within(data.Members).Where(default(DefaultContacts)))
                : new HandwrittenDefaultSide(data);
        if (input.Class == "character")
            return relation ? Array(data, data.World.Within(data.Members).Except(default(SameCharacterHair), default(HairCharacterIndex)))
                : new HandwrittenCharacterSetSide(data);

        CollisionTree tree = Collision.Build<Body, BodyBoundsReader>(data.World, data.Members, default);
        var candidates = Collision.Candidates<Body, BodyBoundsReader>(data.World, data.Members, tree, default);
        if (input.Class == "bitmap-space")
        {
            if (input.Mutual)
            {
                var query = candidates.Where(default(MutualCategories), default(MutualCategoryBitmap));
                var summary = query.Enumerable.Filter.Summary;
                query.RebuildSummaries();
                return relation ? BitmapArray(data, query, summary) : new HandwrittenMutualSide(data, tree, summary);
            }
            else
            {
                var query = candidates.Where(default(OneWayCategories), default(OneWayCategoryBitmap));
                var summary = query.Enumerable.Filter.Summary;
                query.RebuildSummaries();
                return relation ? BitmapArray(data, query, summary) : new HandwrittenOneWaySide(data, tree, summary);
            }
        }
        if (input.Class == "character-space")
        {
            var query = candidates.Except(default(SameCharacterHair), default(HairCharacterIndex));
            var summary = query.Enumerable.Filter.Filter.Summary;
            query.RebuildSummaries();
            return relation ? new CharacterRelationArraySide<SpatialCandidates<Body, BodyBoundsReader,
                SpatialExcept<SpatialKeyFilter<SameCharacterHair, HairCharacterIndex, Hair, Entity>, GroupMatch>, GroupMatch>>
                (data, query, summary) : new HandwrittenCharacterSide(data, tree, summary);
        }
        if (!relation)
        {
            var maintenance = new HandwrittenSpatialMaintenance(data.World, data.Members, tree, data.Jobs);
            maintenance.Schedule().Complete();
            return new MaintainedHandwrittenSide(data, maintenance);
        }
        var contacts = candidates.Where(default(DefaultContacts))
            .Where(default(MutualCategories), default(MutualCategoryBitmap))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        return Maintained(data, contacts, contacts.Enumerable.Filter.First.Second.Summary,
            contacts.Enumerable.Filter.Second.Filter.Summary);
    }

    private static MeasurementSide Array<TEnumerable>(MeasurementData data, RelationQuery<TEnumerable, (Entity, Entity)> query)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)> => new RelationArraySide<TEnumerable>(data, query);

    private static MeasurementSide BitmapArray<TEnumerable, TRelation, TImplementation, TPermission>(MeasurementData data,
        RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionBitmapSummary<TRelation, TImplementation, Body, TPermission> summary)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        where TRelation : struct, IRelation<Body, Body>
        where TImplementation : struct, IBitmapRelation<TRelation, Body, TPermission>
        where TPermission : struct, ICategoryPermission =>
        new BitmapRelationArraySide<TEnumerable, TRelation, TImplementation, TPermission>(data, query, summary);

    private static MeasurementSide Maintained<TFilter, TGroupResults>(MeasurementData data,
        RelationQuery<SpatialCandidates<Body, BodyBoundsReader, TFilter, TGroupResults>, (Entity, Entity)> query,
        CollisionBitmapSummary<MutualCategories, MutualCategoryBitmap, Body, MutualPermission> categories,
        CollisionKeySummary<Hair, Entity> keys)
        where TFilter : struct, ISpatialFilter<TGroupResults>
        where TGroupResults : unmanaged
    {
        var maintenance = query.CreateMaintenance(data.Jobs);
        maintenance.Schedule().Complete();
        return new MaintainedRelationSide<TFilter, TGroupResults>(data, maintenance, categories, keys);
    }
}
