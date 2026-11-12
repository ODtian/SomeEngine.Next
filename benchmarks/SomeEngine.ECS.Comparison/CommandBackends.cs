using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

internal readonly struct EasyCommandValues(EasyEcs.World world) : ICommandValues<EasyEcs.Entity, EasyEcs.EntityId>
{
    public EasyEcs.EntityId Key(EasyEcs.Entity entity) => entity.Id;
    public EasyEcs.Entity Entity(EasyEcs.EntityId key)
    {
        var entity = world.GetEntity(key);
        return entity;
    }
    public EasyEcs.Entity Create<T>(in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.CreateEntity(value);
    public void Set<T>(EasyEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Set(value); }
    public void Add<T>(EasyEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Set(value); }
    public void Remove<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Remove<T>(); }
    public bool Has<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Has<T>();
    public T Read<T>(EasyEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Read<T>();
    public bool Alive(EasyEcs.Entity entity) => entity.IsAlive;
    public void Destroy(EasyEcs.Entity entity) => entity.Destroy();
}

internal readonly struct FrifloCommandValues(Friflo.Engine.ECS.EntityStore world) : ICommandValues<Friflo.Engine.ECS.Entity, Friflo.Engine.ECS.RawEntity>
{
    public Friflo.Engine.ECS.RawEntity Key(Friflo.Engine.ECS.Entity entity) => entity.RawEntity;
    public Friflo.Engine.ECS.Entity Entity(Friflo.Engine.ECS.RawEntity key)
    {
        var entity = world.GetEntityByRawEntity(key);
        if (!Alive(entity)) throw new ArgumentException("The command target's active lifetime has completed.");
        return entity;
    }
    public Friflo.Engine.ECS.Entity Create<T>(in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.CreateEntity(value);
    public void Set<T>(Friflo.Engine.ECS.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.AddComponent(value); }
    public void Add<T>(Friflo.Engine.ECS.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.AddComponent(value); }
    public void Remove<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.RemoveComponent<T>(); }
    public bool Has<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.HasComponent<T>();
    public T Read<T>(Friflo.Engine.ECS.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.GetComponent<T>();
    public bool Alive(Friflo.Engine.ECS.Entity entity) => !entity.IsNull;
    public void Destroy(Friflo.Engine.ECS.Entity entity) => entity.DeleteEntity();
}

internal readonly struct ArchCommandValues(Arch.Core.World world) : ICommandValues<Arch.Core.Entity, Arch.Core.Entity>
{
    public Arch.Core.Entity Key(Arch.Core.Entity entity) => entity;
    public Arch.Core.Entity Entity(Arch.Core.Entity key)
    {
        var entity = key;
        if (!Alive(entity)) throw new ArgumentException("The command target's active lifetime has completed.");
        return entity;
    }
    public Arch.Core.Entity Create<T>(in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Create(value);
    public void Set<T>(Arch.Core.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { if (!Alive(entity)) throw new ArgumentException("The command target has completed its active lifetime."); world.Set(entity, value); }
    public void Add<T>(Arch.Core.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { if (!Alive(entity)) throw new ArgumentException("The command target has completed its active lifetime."); world.Add(entity, value); }
    public void Remove<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { if (!Alive(entity)) throw new ArgumentException("The command target has completed its active lifetime."); world.Remove<T>(entity); }
    public bool Has<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Has<T>(entity);
    public T Read<T>(Arch.Core.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => world.Get<T>(entity);
    public bool Alive(Arch.Core.Entity entity) => world.IsAlive(entity);
    public void Destroy(Arch.Core.Entity entity) => world.Destroy(entity);
}

internal readonly struct DefaultCommandValues(DefaultEcs.World world) : ICommandValues<DefaultEcs.Entity, DefaultEcs.Entity>
{
    public DefaultEcs.Entity Key(DefaultEcs.Entity entity) => entity;
    public DefaultEcs.Entity Entity(DefaultEcs.Entity key)
    {
        var entity = key;
        if (!Alive(entity)) throw new ArgumentException("The command target's active lifetime has completed.");
        return entity;
    }
    public DefaultEcs.Entity Create<T>(in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => CreateValue(value);
    private DefaultEcs.Entity CreateValue<T>(in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { var entity = world.CreateEntity(); entity.Set(value); return entity; }
    public void Set<T>(DefaultEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Set(value); }
    public void Add<T>(DefaultEcs.Entity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Set(value); }
    public void Remove<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { entity.Remove<T>(); }
    public bool Has<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Has<T>();
    public T Read<T>(DefaultEcs.Entity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent => entity.Get<T>();
    public bool Alive(DefaultEcs.Entity entity) => entity.IsAlive;
    public void Destroy(DefaultEcs.Entity entity) => entity.Dispose();
}
