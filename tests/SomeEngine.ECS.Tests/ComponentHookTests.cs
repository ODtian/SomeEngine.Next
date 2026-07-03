using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using System.Reflection;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct HookProbe : SomeEngine.ECS.Components.IComponent
{
    public int Value;
}

public struct HookStatus : SomeEngine.ECS.Components.IEnableableComponent
{
    public int Value;
}

public class ComponentHookTests
{
    [Fact]
    public void AddHookSees()
    {
        var world = new World();
        var entity = world.CreateEntity();
        int observed = -1;

        world.Hooks<HookProbe>().OnAdd((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
        {
            Assert.True(worldArg.Has<HookProbe>(entityArg));
            Assert.Equal(42, worldArg.Read<HookProbe>(entityArg).Value);
            observed = component.Value;
        });

        world.Add(entity, new HookProbe { Value = 42 });

        Assert.Equal(42, observed);
    }

    [Fact]
    public void InsertHookRuns()
    {
        var world = new World();
        var entity = world.CreateEntity();
        int observed = -1;

        world.Hooks<HookProbe>().OnInsert((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
        {
            Assert.True(worldArg.Has<HookProbe>(entityArg));
            observed = component.Value;
        });

        world.Add(entity, new HookProbe { Value = 43 });

        Assert.Equal(43, observed);
    }

    [Fact]
    public void RemoveHookSees()
    {
        var world = new World();
        var entity = world.CreateEntity(new HookProbe { Value = 7 });
        int observed = -1;

        world.Hooks<HookProbe>().OnRemove((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
        {
            Assert.False(worldArg.Has<HookProbe>(entityArg));
            observed = component.Value;
        });

        world.Remove<HookProbe>(entity);

        Assert.Equal(7, observed);
        Assert.False(world.Has<HookProbe>(entity));
    }

    [Fact]
    public void SetHookPairs()
    {
        var world = new World();
        var entity = world.CreateEntity(new HookProbe { Value = 1 });
        int replaceCount = 0;
        int insertCount = 0;
        int replaced = 0;
        int inserted = 0;

        world.Hooks<HookProbe>()
            .OnReplace((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                replaceCount++;
                replaced = component.Value;
                Assert.Equal(20, worldArg.Read<HookProbe>(entityArg).Value);
            })
            .OnInsert((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                insertCount++;
                inserted = component.Value;
                Assert.Equal(20, worldArg.Read<HookProbe>(entityArg).Value);
            });

        ref var component = ref world.Get<HookProbe>(entity);
        component.Value = 10;

        Assert.Equal(0, replaceCount);
        Assert.Equal(0, insertCount);

        world.Replace(entity, new HookProbe { Value = 20 });

        Assert.Equal(1, replaceCount);
        Assert.Equal(1, insertCount);
        Assert.Equal(10, replaced);
        Assert.Equal(20, inserted);
    }

    [Fact]
    public void CopyHookSnapshot()
    {
        var world = new World();
        var source = world.CreateEntity(new HookProbe { Value = 20 });
        var target = world.CreateEntity(new HookProbe { Value = 10 });
        int replaced = 0;
        int inserted = 0;

        world.Hooks<HookProbe>()
            .OnReplace((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                Assert.Equal(target, entityArg);
                Assert.Equal(20, worldArg.Read<HookProbe>(entityArg).Value);
                replaced = component.Value;
            })
            .OnInsert((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                Assert.Equal(target, entityArg);
                Assert.Equal(20, worldArg.Read<HookProbe>(entityArg).Value);
                inserted = component.Value;
            });

        world.CopyEntity(source, target);

        Assert.Equal(10, replaced);
        Assert.Equal(20, inserted);
        Assert.Equal(20, world.Read<HookProbe>(target).Value);
    }

    [Fact]
    public void CopyRemoveSnapshot()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity(new HookProbe { Value = 10 });
        int replaced = 0;
        int removed = 0;

        world.Hooks<HookProbe>()
            .OnReplace((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                Assert.Equal(target, entityArg);
                Assert.False(worldArg.Has<HookProbe>(entityArg));
                replaced = component.Value;
            })
            .OnRemove((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                Assert.Equal(target, entityArg);
                Assert.False(worldArg.Has<HookProbe>(entityArg));
                removed = component.Value;
            });

        world.CopyEntity(source, target);

        Assert.Equal(10, replaced);
        Assert.Equal(10, removed);
        Assert.False(world.Has<HookProbe>(target));
    }

    [Fact]
    public void DuplicateHookFails()
    {
        var world = new World();
        var hooks = world.Hooks<HookProbe>();

        hooks.OnAdd((DeferredWorld worldArg, Entity entityArg, in HookProbe component) => { });

        Assert.Throws<InvalidOperationException>(() =>
            hooks.OnAdd((DeferredWorld worldArg, Entity entityArg, in HookProbe component) => { }));
    }

    [Fact]
    public void ToggleSkipsHooks()
    {
        var world = new World();
        var entity = world.CreateEntity();
        int addCount = 0;
        int removeCount = 0;
        int insertCount = 0;

        world.Hooks<HookStatus>()
            .OnAdd((DeferredWorld worldArg, Entity entityArg, in HookStatus component) => addCount++)
            .OnRemove((DeferredWorld worldArg, Entity entityArg, in HookStatus component) => removeCount++)
            .OnInsert((DeferredWorld worldArg, Entity entityArg, in HookStatus component) => insertCount++);

        world.Add(entity, new HookStatus { Value = 5 });
        world.Disable<HookStatus>(entity);
        world.Enable<HookStatus>(entity);

        Assert.Equal(1, addCount);
        Assert.Equal(0, removeCount);
        Assert.Equal(1, insertCount);
    }

    [Fact]
    public void DespawnHookRuns()
    {
        var world = new World();
        var entity = world.CreateEntity(new HookProbe { Value = 88 });
        int removeValue = -1;
        int despawnValue = -1;

        world.Hooks<HookProbe>()
            .OnRemove((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                removeValue = component.Value;
            })
            .OnDespawn((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
            {
                despawnValue = component.Value;
            });

        world.DestroyEntity(entity);

        Assert.Equal(88, removeValue);
        Assert.Equal(88, despawnValue);
        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void HookWritesDefer()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.Hooks<HookProbe>().OnAdd((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
        {
            worldArg.Commands().Replace(entityArg, new HookProbe { Value = 99 });
        });

        world.Add(entity, new HookProbe { Value = 1 });

        Assert.Equal(1, world.Read<HookProbe>(entity).Value);

        world.Flush();

        Assert.Equal(99, world.Read<HookProbe>(entity).Value);
    }

    [Fact]
    public void CommandRemoveSees()
    {
        var world = new World();
        var entity = world.CreateEntity(new HookProbe { Value = 12 });
        bool hadComponent = true;
        int observed = -1;

        world.Hooks<HookProbe>().OnRemove((DeferredWorld worldArg, Entity entityArg, in HookProbe component) =>
        {
            hadComponent = worldArg.Has<HookProbe>(entityArg);
            observed = component.Value;
        });

        world.Commands().Remove<HookProbe>(entity);
        world.Flush();

        Assert.False(hadComponent);
        Assert.Equal(12, observed);
        Assert.False(world.Has<HookProbe>(entity));
    }

    [Fact]
    public void DeferredWorldGuards()
    {
        var names = typeof(DeferredWorld)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain("Add", names);
        Assert.DoesNotContain("Set", names);
        Assert.DoesNotContain("Remove", names);
        Assert.DoesNotContain("DestroyEntity", names);
        Assert.DoesNotContain("Get", names);
        Assert.DoesNotContain("Hooks", names);
        Assert.DoesNotContain("Flush", names);
    }
}
