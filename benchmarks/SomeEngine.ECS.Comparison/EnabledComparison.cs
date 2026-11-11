using Arch.Core;
using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

public struct Signal : IEnableableComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
{
    public int Value, Other;
}

public struct DisabledSignal : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;

internal struct SignalPass(int value) : IComponentAction<Signal>, EasyEcs.IForEachBatch<Signal>
{
    internal int Count;
    public void Execute(Span<Signal> values)
    {
        Count += values.Length;
        for (int i = 0; i < values.Length; i++) values[i].Value = value;
    }
    public void Execute(EasyEcs.EntitySpan entities, Span<Signal> values) => Execute(values);
}

internal sealed partial class SomeEngineWorld
{
    private Query? _enabledSignals, _disabledSignals;

    private static class SignalQueries
    {
        internal static readonly QueryDescription Disabled = new QueryDescription().WithDisabled<Signal>();
    }

    private void PrepareEnabled()
    {
        if (_enabledSignals is not null) return;
        if (!SignalInInitialLayout) foreach (var entity in _entities) _world.Add(entity, new Signal());
        _enabledSignals = _world.Query<Signal>();
        _disabledSignals = _world.Query(SignalQueries.Disabled);
    }

    internal override void EnableableCycle()
    {
        long total = 0;
        int stored = 0;
        for (int i = 0; i < Count; i += 4)
        {
            var entity = _entities[i];
            _world.Disable<Signal>(entity);
            _world.Set(entity, new Signal { Value = 42 });
            if (_world.Has<Signal>(entity)) stored++;
            total += _world.Read<Signal>(entity).Value;
        }
        var active = new SignalPass(7);
        var disabled = new SignalPass(43);
        _enabledSignals.GetValueOrDefault().ForEach<Signal, SignalPass>(ref active);
        _disabledSignals.GetValueOrDefault().ForEach<Signal, SignalPass>(ref disabled);
        EnabledActiveCount = active.Count;
        EnabledDisabledCount = disabled.Count;
        EnabledStoredCount = stored;
        ReadTotal = total;
        for (int i = 0; i < Count; i += 4) _world.Enable<Signal>(_entities[i]);
    }

    internal override bool CheckEnabledValues() => _world.IsEnabled<Signal>(_entities[0])
        && _world.Read<Signal>(_entities[0]).Value == 43 && _world.Read<Signal>(_entities[1]).Value == 7;
}

internal sealed partial class EasyEcsWorld
{
    private bool _signalsReady;
    private static readonly EasyEcs.QueryFilter ActiveSignals = EasyEcs.QueryFilter.Create().Without<DisabledSignal>();
    private static readonly EasyEcs.QueryFilter DisabledSignals = EasyEcs.QueryFilter.Create().With<DisabledSignal>();

    internal override void Prepare(string workload)
    {
        if (workload.StartsWith("relation-")) { PrepareRelations(workload); return; }
        if (workload.StartsWith("hierarchy-")) { PrepareHierarchy(workload); return; }
        if (workload.StartsWith("query-")) { PrepareQueryAppend(workload); return; }
        if (workload.StartsWith("changes-")) { PrepareChanges(workload); return; }
        if (workload.StartsWith("buffer-")) { PrepareBuffers(workload); return; }
        if (workload == "add-remove")
        {
            _world.EnsureCapacity<Position, Velocity, Health, Extra>(Changes / 2);
            _world.EnsureCapacity<Position, Velocity, Health, Extra, Team>(Changes / 4);
            _world.EnsureCapacity<Position, Velocity, Health, Extra, Team, Sleeping>(Changes / 4);
            return;
        }
        if (workload == "resources") { PrepareResources(); return; }
        if (workload.StartsWith("group-")) { PrepareGroups(workload); return; }
        if (workload.StartsWith("sparse-")) { PrepareSparse(workload); return; }
        if (workload.StartsWith("index-")) { PrepareIndex(workload); return; }
        if (workload.StartsWith("shared-")) { PrepareShared(workload); return; }
        if (workload != "enableable" || _signalsReady) return;
        if (!SignalInInitialLayout) foreach (var entity in _entities) entity.Set(new Signal());
        _signalsReady = true;
    }

    internal override void EnableableCycle()
    {
        long total = 0;
        int stored = 0;
        for (int i = 0; i < Count; i += 4)
        {
            var entity = _entities[i];
            entity.Set(new DisabledSignal());
            entity.Set(new Signal { Value = 42 });
            if (entity.Has<Signal>()) stored++;
            total += entity.Read<Signal>().Value;
        }
        var active = new SignalPass(7);
        var disabled = new SignalPass(43);
        _world.ForEachBatch<Signal, SignalPass>(ref active, in ActiveSignals);
        _world.ForEachBatch<Signal, SignalPass>(ref disabled, in DisabledSignals);
        EnabledActiveCount = active.Count;
        EnabledDisabledCount = disabled.Count;
        EnabledStoredCount = stored;
        ReadTotal = total;
        for (int i = 0; i < Count; i += 4) _entities[i].Remove<DisabledSignal>();
    }

    internal override bool CheckEnabledValues() => !_entities[0].Has<DisabledSignal>()
        && _entities[0].Read<Signal>().Value == 43 && _entities[1].Read<Signal>().Value == 7;
}

internal sealed partial class FrifloWorld
{
    private ArchetypeQuery<Signal>? _enabledSignals, _disabledSignals;

    private void PrepareEnabled()
    {
        if (_enabledSignals is not null) return;
        if (!SignalInInitialLayout) foreach (var entity in _entities) entity.AddComponent(new Signal());
        _enabledSignals = _world.Query<Signal>().WithoutAnyComponents(ComponentTypes.Get<DisabledSignal>());
        _disabledSignals = _world.Query<Signal>().AllComponents(ComponentTypes.Get<DisabledSignal>());
    }

    internal override void EnableableCycle()
    {
        long total = 0;
        int stored = 0;
        for (int i = 0; i < Count; i += 4)
        {
            var entity = _entities[i];
            entity.AddComponent(new DisabledSignal());
            entity.AddComponent(new Signal { Value = 42 });
            if (entity.HasComponent<Signal>()) stored++;
            total += entity.GetComponent<Signal>().Value;
        }
        var active = new SignalPass(7);
        var disabled = new SignalPass(43);
        foreach (var (signals, _) in _enabledSignals!.Chunks) active.Execute(signals.Span);
        foreach (var (signals, _) in _disabledSignals!.Chunks) disabled.Execute(signals.Span);
        EnabledActiveCount = active.Count;
        EnabledDisabledCount = disabled.Count;
        EnabledStoredCount = stored;
        ReadTotal = total;
        for (int i = 0; i < Count; i += 4) _entities[i].RemoveComponent<DisabledSignal>();
    }

    internal override bool CheckEnabledValues() => !_entities[0].HasComponent<DisabledSignal>()
        && _entities[0].GetComponent<Signal>().Value == 43 && _entities[1].GetComponent<Signal>().Value == 7;
}

internal sealed partial class ArchWorld
{
    private bool _signalsReady;
    private Arch.Core.QueryDescription _enabledSignals, _disabledSignals;

    internal override void Prepare(string workload)
    {
        if (workload.StartsWith("relation-")) { PrepareRelations(workload); return; }
        if (workload.StartsWith("hierarchy-")) { PrepareHierarchy(workload); return; }
        if (workload.StartsWith("query-")) { PrepareQueryAppend(workload); return; }
        if (workload.StartsWith("changes-")) { PrepareChanges(workload); return; }
        if (workload.StartsWith("buffer-")) { PrepareBuffers(workload); return; }
        if (workload == "add-remove")
        {
            _world.EnsureCapacity(new Arch.Core.Signature(Arch.Core.Component<Position>.ComponentType, Arch.Core.Component<Velocity>.ComponentType,
                Arch.Core.Component<Health>.ComponentType, Arch.Core.Component<Extra>.ComponentType), Changes / 2);
            _world.EnsureCapacity(new Arch.Core.Signature(Arch.Core.Component<Position>.ComponentType, Arch.Core.Component<Velocity>.ComponentType,
                Arch.Core.Component<Health>.ComponentType, Arch.Core.Component<Extra>.ComponentType, Arch.Core.Component<Team>.ComponentType), Changes / 4);
            _world.EnsureCapacity(new Arch.Core.Signature(Arch.Core.Component<Position>.ComponentType, Arch.Core.Component<Velocity>.ComponentType,
                Arch.Core.Component<Health>.ComponentType, Arch.Core.Component<Extra>.ComponentType, Arch.Core.Component<Team>.ComponentType,
                Arch.Core.Component<Sleeping>.ComponentType), Changes / 4);
            return;
        }
        if (workload == "resources") { PrepareResources(); return; }
        if (workload.StartsWith("group-")) { PrepareGroups(workload); return; }
        if (workload.StartsWith("sparse-")) { PrepareSparse(workload); return; }
        if (workload.StartsWith("index-")) { PrepareIndex(workload); return; }
        if (workload.StartsWith("shared-")) { PrepareShared(workload); return; }
        if (workload != "enableable" || _signalsReady) return;
        if (!SignalInInitialLayout) foreach (var entity in _entities) _world.Add(entity, new Signal());
        _enabledSignals = new Arch.Core.QueryDescription().WithAll<Signal>().WithNone<DisabledSignal>();
        _disabledSignals = new Arch.Core.QueryDescription().WithAll<Signal, DisabledSignal>();
        _signalsReady = true;
    }

    internal override void EnableableCycle()
    {
        long total = 0;
        int stored = 0;
        for (int i = 0; i < Count; i += 4)
        {
            var entity = _entities[i];
            _world.Add(entity, new DisabledSignal());
            _world.Set(entity, new Signal { Value = 42 });
            if (_world.Has<Signal>(entity)) stored++;
            total += _world.Get<Signal>(entity).Value;
        }
        var active = new SignalPass(7);
        var disabled = new SignalPass(43);
        foreach (ref var chunk in _world.Query(in _enabledSignals).GetChunkIterator()) active.Execute(chunk.GetSpan<Signal>()[..chunk.Count]);
        foreach (ref var chunk in _world.Query(in _disabledSignals).GetChunkIterator()) disabled.Execute(chunk.GetSpan<Signal>()[..chunk.Count]);
        EnabledActiveCount = active.Count;
        EnabledDisabledCount = disabled.Count;
        EnabledStoredCount = stored;
        ReadTotal = total;
        for (int i = 0; i < Count; i += 4) _world.Remove<DisabledSignal>(_entities[i]);
    }

    internal override bool CheckEnabledValues() => !_world.Has<DisabledSignal>(_entities[0])
        && _world.Get<Signal>(_entities[0]).Value == 43 && _world.Get<Signal>(_entities[1]).Value == 7;
}

internal sealed partial class DefaultEcsWorld
{
    private DefaultEcs.EntitySet? _enabledSignals, _disabledSignals;

    private void PrepareEnabled()
    {
        if (_enabledSignals is not null) return;
        foreach (var entity in _entities) entity.Set(new Signal());
        _enabledSignals = _world.GetEntities().With<Signal>().AsSet();
        _disabledSignals = _world.GetEntities().Without<Signal>().AsSet();
    }

    internal override void EnableableCycle()
    {
        long total = 0;
        int stored = 0;
        for (int i = 0; i < Count; i += 4)
        {
            var entity = _entities[i];
            entity.Disable<Signal>();
            entity.Get<Signal>() = new Signal { Value = 42 };
            if (entity.Has<Signal>()) stored++;
            total += entity.Get<Signal>().Value;
        }
        var signals = _world.GetComponents<Signal>();
        int active = 0, disabled = 0;
        foreach (ref readonly var entity in _enabledSignals!.GetEntities())
        {
            signals[entity].Value = 7;
            active++;
        }
        foreach (ref readonly var entity in _disabledSignals!.GetEntities())
        {
            if (!entity.Has<Signal>()) continue;
            signals[entity].Value = 43;
            disabled++;
        }
        EnabledActiveCount = active;
        EnabledDisabledCount = disabled;
        EnabledStoredCount = stored;
        ReadTotal = total;
        for (int i = 0; i < Count; i += 4) _entities[i].Enable<Signal>();
    }

    internal override bool CheckEnabledValues() => _entities[0].IsEnabled<Signal>()
        && _entities[0].Get<Signal>().Value == 43 && _entities[1].Get<Signal>().Value == 7;
}
