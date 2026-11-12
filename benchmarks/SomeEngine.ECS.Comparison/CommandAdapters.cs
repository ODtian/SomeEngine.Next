using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal readonly struct SomeCommandComparison : ICommandComparison<Entity, DeferredEntity>
{
    private readonly World _world;
    private readonly CommandBatch _commands;
    internal SomeCommandComparison(World world, int producers) => (_world, _commands) = (world, new(world, producers));
    internal CommandBatch Batch => _commands;
    public void Clear() => _commands.Clear();
    public DeferredEntity Create(int producer, int value) => _commands[producer].Create(new Position { X = value });
    public void Set(int producer, DeferredEntity entity, int value) => _commands[producer].Set(entity, new Position { X = value });
    public void SetExisting(int producer, Entity entity, int value) => _commands[producer].Set(entity, new Position { X = value });
    public void AddExtra(int producer, DeferredEntity entity, int value) => _commands[producer].Add(entity, new Extra { Value = value });
    public void RemoveExtra(int producer, DeferredEntity entity) => _commands[producer].Remove<Extra>(entity);
    public void DestroyDeferred(int producer, DeferredEntity entity) => _commands[producer].Destroy(entity);
    public void Playback() => _commands.Playback();
    public Entity Resolve(DeferredEntity handle) => handle.Resolve();
    public bool Alive(Entity entity) => _world.IsAlive(entity);
    public int Read(Entity entity) => (int)_world.Read<Position>(entity).X;
    public bool HasExtra(Entity entity) => _world.Has<Extra>(entity);
    public void Destroy(Entity entity) => _world.Destroy(entity);
    public void Dispose() => _commands.Dispose();
}

internal readonly struct NativeCommandComparison<TEntity, TKey, TValues> : ICommandComparison<TEntity, ReferenceDeferredEntity<TEntity>>
    where TEntity : struct where TKey : struct where TValues : struct, ICommandValues<TEntity, TKey>
{
    private readonly ReferenceCommands<TEntity, TKey, TValues> _commands;
    internal NativeCommandComparison(TValues values, int producers) => _commands = new(values, producers);
    public void Clear() => _commands.Clear();
    public ReferenceDeferredEntity<TEntity> Create(int producer, int value) => _commands[producer].Create(new Position { X = value });
    public void Set(int producer, ReferenceDeferredEntity<TEntity> entity, int value) => _commands[producer].Set(entity, new Position { X = value });
    public void SetExisting(int producer, TEntity entity, int value) => _commands[producer].Set(entity, new Position { X = value });
    public void AddExtra(int producer, ReferenceDeferredEntity<TEntity> entity, int value) => _commands[producer].Add(entity, new Extra { Value = value });
    public void RemoveExtra(int producer, ReferenceDeferredEntity<TEntity> entity) => _commands[producer].Remove<Extra>(entity);
    public void DestroyDeferred(int producer, ReferenceDeferredEntity<TEntity> entity) => _commands[producer].Destroy(entity);
    public void Playback() => _commands.Playback();
    public TEntity Resolve(ReferenceDeferredEntity<TEntity> handle) => handle.Resolve();
    public bool Alive(TEntity entity) => _commands.Values.Alive(entity);
    public int Read(TEntity entity) => (int)_commands.Values.Read<Position>(entity).X;
    public bool HasExtra(TEntity entity) => _commands.Values.Has<Extra>(entity);
    public void Destroy(TEntity entity) => _commands.Values.Destroy(entity);
    public void Dispose() => _commands.Dispose();
}

internal abstract partial class ComparisonWorld
{
    internal ICommandScenario? CommandScenario;
    internal abstract void PrepareCommandsForComparison(string workload);
    internal void CommandCycle() => CommandScenario!.Cycle();
}

internal sealed partial class SomeEngineWorld
{
    internal override void PrepareCommandsForComparison(string workload)
    {
        int producers = workload == "commands-4" ? 4 : 1;
        CommandScenario ??= new CommandScenario<Entity, DeferredEntity, SomeCommandComparison>(
            new(_world, producers), _entities, producers);
    }
}

internal sealed partial class EasyEcsWorld
{
    internal override void PrepareCommandsForComparison(string workload)
    {
        int producers = workload == "commands-4" ? 4 : 1;
        CommandScenario ??= new CommandScenario<EasyEcs.Entity, ReferenceDeferredEntity<EasyEcs.Entity>, NativeCommandComparison<EasyEcs.Entity, EasyEcs.EntityId, EasyCommandValues>>(
            new(new EasyCommandValues(_world), producers), _entities, producers);
    }
}

internal sealed partial class FrifloWorld
{
    internal override void PrepareCommandsForComparison(string workload)
    {
        int producers = workload == "commands-4" ? 4 : 1;
        CommandScenario ??= new CommandScenario<Friflo.Engine.ECS.Entity, ReferenceDeferredEntity<Friflo.Engine.ECS.Entity>, NativeCommandComparison<Friflo.Engine.ECS.Entity, Friflo.Engine.ECS.RawEntity, FrifloCommandValues>>(
            new(new FrifloCommandValues(_world), producers), _entities, producers);
    }
}

internal sealed partial class ArchWorld
{
    internal override void PrepareCommandsForComparison(string workload)
    {
        int producers = workload == "commands-4" ? 4 : 1;
        CommandScenario ??= new CommandScenario<Arch.Core.Entity, ReferenceDeferredEntity<Arch.Core.Entity>, NativeCommandComparison<Arch.Core.Entity, Arch.Core.Entity, ArchCommandValues>>(
            new(new ArchCommandValues(_world), producers), _entities, producers);
    }
}

internal sealed partial class DefaultEcsWorld
{
    internal override void PrepareCommandsForComparison(string workload)
    {
        int producers = workload == "commands-4" ? 4 : 1;
        CommandScenario ??= new CommandScenario<DefaultEcs.Entity, ReferenceDeferredEntity<DefaultEcs.Entity>, NativeCommandComparison<DefaultEcs.Entity, DefaultEcs.Entity, DefaultCommandValues>>(
            new(new DefaultCommandValues(_world), producers), _entities, producers);
    }
}
