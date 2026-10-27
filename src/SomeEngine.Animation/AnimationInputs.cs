using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Animation;

/// <summary>Observes the inputs that can change an idle animation batch.</summary>
internal sealed unsafe class AnimationInputs : IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _timelines;
    private readonly QueryHandle _graphs;
    private readonly QueryHandle _entities;
    private readonly HashSet<Type> _values = new();
    private readonly List<CompositionInput> _compositions = new();
    private uint _entityVersion;
    private uint _timelineVersion;
    private uint _readVersion;
    private bool _updated;
    private bool _hadActiveTimelines;
    private bool _hasActiveTimelines;
    private bool _hadCompositionChanges;
    private bool _hasCompositionChanges;

    internal AnimationInputs(World world)
    {
        _world = world;
        _timelines = world.Query(world.QueryDefinition().All<Timeline>().Enabled<Timeline>());
        _graphs = world.Query(world.QueryDefinition().Read<GraphInstance>().Optional<Timeline>());
        _entities = world.Query(QueryDefinition.Empty);
        Add<float>();
    }

    internal void Add<T>() where T : unmanaged
    {
        if (!_values.Add(typeof(T)))
            return;
        QueryHandle query = _world.Query(_world.QueryDefinition().ReadBuffer<Contribution<T>>()
            .OptionalBuffer<PropertySource<T>>(QueryAccess.Read).OptionalBuffer<BaseValue<T>>(QueryAccess.Read)
            .Optional<AnimationLayers>(QueryAccess.Read).OptionalBuffer<LayerChannel>(QueryAccess.Read));
        _compositions.Add(new CompositionInput(query, &HasCompositionChanges<T>));
    }

    internal void BeginUpdate()
    {
        _hasActiveTimelines = false;
        _hasCompositionChanges = false;
    }

    internal bool HasWork()
    {
        if (_world.HasQueryMembers(_timelines))
        {
            _hasActiveTimelines = true;
            return true;
        }
        bool work = !_updated || _hadActiveTimelines || _hadCompositionChanges ||
            _world.ComponentWriteVersion<Timeline>() != _timelineVersion ||
            _world.MembershipVersion(_entities) != _entityVersion || HasGraphWork();
        foreach (CompositionInput input in _compositions)
            if (_world.HasQueryMembers(input.Query) && input.HasChanges(_world, input.Query, _readVersion))
            {
                _hasCompositionChanges = true;
                return true;
            }
        return work;
    }

    internal void Accept()
    {
        _entityVersion = _world.MembershipVersion(_entities);
        _timelineVersion = _world.ComponentWriteVersion<Timeline>();
        // The next direct buffer write starts after this completed input window.
        _readVersion = _world.AcquireSystemTick();
        _hadActiveTimelines = _hasActiveTimelines;
        // A following composition pass restores fixed sources after transient contributions.
        _hadCompositionChanges = !_updated || _hasCompositionChanges;
        _updated = true;
    }

    private bool HasGraphWork()
    {
        if (!_world.HasQueryMembers(_graphs))
            return false;
        bool work = false;
        _world.ExecuteQuery(_graphs, ref work, static (QueryCursor rows, ref bool work) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GraphInstance graph = row.Read<GraphInstance>();
                if (graph.Data is not null || !row.Has<Timeline>() && !graph.CleanupQueued)
                {
                    work = true;
                    return;
                }
            }
        });
        return work;
    }

    private static bool HasCompositionChanges<T>(World world, QueryHandle query, uint version) where T : unmanaged
    {
        bool changed = false;
        world.ExecuteQuery(query, version, ref changed, static (QueryCursor rows, ref bool changed) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                if (chunk.HasBufferChangedSinceLastSystemVersion<Contribution<T>>() ||
                    chunk.HasBuffer<PropertySource<T>>() && chunk.HasBufferChangedSinceLastSystemVersion<PropertySource<T>>() ||
                    chunk.HasBuffer<BaseValue<T>>() && chunk.HasBufferChangedSinceLastSystemVersion<BaseValue<T>>() ||
                    chunk.Has<AnimationLayers>() && chunk.HasChangedSinceLastSystemVersion<AnimationLayers>() ||
                    chunk.HasBuffer<LayerChannel>() && chunk.HasBufferChangedSinceLastSystemVersion<LayerChannel>())
                {
                    changed = true;
                    return;
                }
            }
        });
        return changed;
    }

    public void Dispose()
    {
        foreach (CompositionInput input in _compositions)
            _world.ReleaseQuery(input.Query);
        _world.ReleaseQuery(_timelines);
        _world.ReleaseQuery(_graphs);
        _world.ReleaseQuery(_entities);
    }

    private readonly struct CompositionInput(QueryHandle query,
        delegate* managed<World, QueryHandle, uint, bool> hasChanges)
    {
        internal readonly QueryHandle Query = query;
        internal readonly delegate* managed<World, QueryHandle, uint, bool> HasChanges = hasChanges;
    }
}
