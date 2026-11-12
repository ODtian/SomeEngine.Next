using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed class EntityPrefab : IDisposable
{
    private sealed class BoundTargets(int generation, Archetype[] targets)
    {
        internal readonly int Generation = generation;
        internal readonly Archetype[] Targets = targets;
    }
    private readonly World _values;
    private Entity[] _templates;
    private readonly ConditionalWeakTable<World, BoundTargets> _targets = new();
    private readonly ConditionalWeakTable<World, BoundTargets>.CreateValueCallback _bind;
    public int EntityCount => _templates.Length;

    public EntityPrefab(World source, ReadOnlySpan<Entity> entities)
    {
        using WorldAccess access = source.OpenStructure();
        source.RequireStructure();
        _values = new World(entities.Length);
        var references = new Entity[entities.Length];
        for (int i = 0; i < entities.Length; i++) references[i] = new(-i - 1, 1);
        _templates = new Entity[entities.Length];
        source.CreateCopyTargets(_values, entities, _templates);
        var map = new EntityMap(entities, references);
        source.CopyValuesTo(_values, entities, _templates, map);
        _bind = Bind;
    }

    private BoundTargets Bind(World world)
        => new(world.DataGeneration, _values.BindCopyTargets(world, _templates));

    public void Instantiate(World world, Span<Entity> entities, int copies = 1)
    {
        using WorldAccess access = world.OpenStructure();
        world.RequireStructure();
        entities = entities[..(_templates.Length * copies)];
        BoundTargets bound = _targets.GetValue(world, _bind);
        if (bound.Generation != world.DataGeneration)
        {
            _targets.Remove(world);
            bound = _targets.GetValue(world, _bind);
        }
        world.CreateCopies(bound.Targets, copies, entities);
        for (int i = 0; i < copies; i++)
        {
            Span<Entity> targets = entities.Slice(i * _templates.Length, _templates.Length);
            var map = new EntityMap(targets);
            _values.CopyValuesTo(world, _templates, targets, map);
        }
        world.CompleteCopies(entities);
    }

    public void Instantiate<TMapper>(World world, Span<Entity> entities, ref TMapper mapper, int copies = 1)
        where TMapper : struct, IEntityMapper
    {
        using WorldAccess access = world.OpenStructure();
        world.RequireStructure();
        entities = entities[..(_templates.Length * copies)];
        BoundTargets bound = _targets.GetValue(world, _bind);
        if (bound.Generation != world.DataGeneration)
        {
            _targets.Remove(world);
            bound = _targets.GetValue(world, _bind);
        }
        world.CreateCopies(bound.Targets, copies, entities);
        for (int i = 0; i < copies; i++)
        {
            Span<Entity> targets = entities.Slice(i * _templates.Length, _templates.Length);
            var map = EntityMap.Create(targets, ref mapper);
            _values.CopyValuesTo(world, _templates, targets, map);
        }
        world.CompleteCopies(entities);
    }

    public void Dispose()
    {
        _values.Dispose();
        _templates = [];
        _targets.Clear();
    }
}
