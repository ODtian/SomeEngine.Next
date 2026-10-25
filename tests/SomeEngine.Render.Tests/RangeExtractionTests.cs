using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RangeExtractionTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    [Fact]
    public void RangeCollectorPublishesSourceChangesThroughExistingRenderBoundary()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World source = WorldJobs.CreateWorld(scheduler: scheduler);
        using var destination = new RenderWorld();
        using var extraction = new RenderExtractionSystems(destination, new ValueExtractor());
        Entity first = source.CreateEntity(new SourceValue(37));
        Entity second = source.CreateEntity(new SourceValue(53));
        source.Add(second, new OtherGroup());

        extraction.Extract(source);
        graphics.Apply(extraction, destination);
        Entity firstMirror = extraction.RequireMirror(first);
        Entity secondMirror = extraction.RequireMirror(second);
        Assert.Equal(37, destination.Read<RenderValue>(firstMirror).Value);
        Assert.Equal(53, destination.Read<RenderValue>(secondMirror).Value);

        source.Replace(first, new SourceValue(71));
        extraction.Extract(source);
        graphics.Apply(extraction, destination);
        Assert.Equal(firstMirror, extraction.RequireMirror(first));
        Assert.Equal(71, destination.Read<RenderValue>(firstMirror).Value);
        Assert.Equal(53, destination.Read<RenderValue>(secondMirror).Value);
    }

    private readonly record struct SourceValue(int Value) : IComponent;
    private readonly record struct RenderValue(int Value) : IComponent;
    private readonly record struct OtherGroup : IComponent;

    private sealed class ValueExtractor : IRenderExtractionRangeSystem
    {
        private readonly List<(Entity Source, int Value)> _values = [];

        public void DeclareReads(RenderExtractionQuery query)
        {
            query.ReadOptional<SourceValue>();
            query.Write<RenderValue>();
        }

        public void Reset() => _values.Clear();
        public IRenderExtractionCollector CreateRangeCollector() => new ValueCollector();

        public bool MergeRangeCollector(IRenderExtractionCollector collector, RenderExtractionContext context)
        {
            var values = (ValueCollector)collector;
            _values.AddRange(values.Values);
            return values.Values.Count != 0;
        }

        public void Collect(QueryChunkView chunk) => throw new NotSupportedException("This feature collects source ranges.");
        public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) =>
            throw new NotSupportedException("This feature collects source ranges.");

        public void Apply(RenderExtractionContext context)
        {
            foreach (var value in _values)
                context.Upsert(context.RetainMirror(value.Source), new RenderValue(value.Value));
        }

        public void ApplyChanges(RenderExtractionContext context)
        {
            foreach (var value in _values)
                context.UpdateExisting(context.RequireMirror(value.Source), new RenderValue(value.Value));
        }
    }

    private sealed class ValueCollector : IRenderExtractionCollector
    {
        internal readonly List<(Entity Source, int Value)> Values = [];
        public void Reset() => Values.Clear();
        public void Dispose() => Values.Clear();

        public void Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion, bool fullCollection)
        {
            if (!packet.TryRead<SourceValue>(out var values)) return;
            for (int index = 0; index < packet.Count; index++)
                if (fullCollection || packet.ChangedSince<SourceValue>(lastSystemVersion))
                    Values.Add((packet.Entities[index], values[index].Value));
        }
    }
}
