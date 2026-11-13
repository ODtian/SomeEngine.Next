using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Layers;

internal sealed class LayerViewEntities : IDisposable
{
    private readonly RenderWorld _world;
    private readonly QueryHandle _query;
    private readonly List<SourceView> _sources = [];
    private readonly Dictionary<Entity, Entity> _views = [];
    private readonly List<Entity> _removed = [];
    private readonly HashSet<Entity> _current = [];

    internal LayerViewEntities(RenderWorld world)
    {
        _world = world;
        _query = world.Query(new QueryDefinitionBuilder().Read<RenderView>().Optional<RenderViewTarget>(QueryAccess.Read)
            .Optional<RenderPreviousView>(QueryAccess.Read));
    }

    internal void Update()
    {
        _sources.Clear();
        _current.Clear();
        _world.ExecuteQuery(_query, values =>
        {
            foreach (QueryRow row in values.Rows)
            {
                row.TryRead(out RenderViewTarget target);
                if (target.Kind != ViewTargetKind.Screen) continue;
                _sources.Add(new(row.Entity, row.Read<RenderView>(),
                    row.Has<RenderPreviousView>() ? row.Read<RenderPreviousView>() : null));
                _current.Add(row.Entity);
            }
        });
        _sources.Sort(static (left, right) => left.Entity.Index.CompareTo(right.Entity.Index));
        _removed.Clear();
        foreach ((Entity source, Entity view) in _views)
            if (!_current.Contains(source))
            {
                _world.DestroyEntity(view);
                _removed.Add(source);
            }
        foreach (Entity source in _removed) _views.Remove(source);
        for (int index = 0; index < _sources.Count; index++)
        {
            SourceView source = _sources[index];
            var target = new RenderViewTarget(ViewTargetKind.LayerTiles, (uint)index);
            if (!_views.TryGetValue(source.Entity, out Entity entity))
            {
                entity = _world.CreateEntity(source.View);
                _world.Add(entity, target);
                _views.Add(source.Entity, entity);
            }
            else
            {
                if (_world.Read<RenderView>(entity) != source.View) _world.Replace(entity, source.View);
                if (_world.Read<RenderViewTarget>(entity) != target) _world.Replace(entity, target);
            }
            if (source.Previous is { } previous)
            {
                if (_world.Has<RenderPreviousView>(entity))
                {
                    if (_world.Read<RenderPreviousView>(entity) != previous) _world.Replace(entity, previous);
                }
                else _world.Add(entity, previous);
            }
            else if (_world.Has<RenderPreviousView>(entity)) _world.Remove<RenderPreviousView>(entity);
        }
    }

    public void Dispose()
    {
        foreach (Entity entity in _views.Values) _world.DestroyEntity(entity);
        _views.Clear();
        _world.ReleaseQuery(_query);
    }

    private readonly record struct SourceView(Entity Entity, RenderView View, RenderPreviousView? Previous);
}
