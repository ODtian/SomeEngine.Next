using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Comparison;

internal interface IHierarchyScenario : IDisposable
{
    void Cycle();
    void Grow();
    bool Check();
}

internal readonly record struct HierarchyEdit<TEntity>(TEntity Child, TEntity Parent) where TEntity : struct;

internal interface IHierarchyComparison<TEntity> : IDisposable where TEntity : struct
{
    ulong Version { get; }
    bool Active(TEntity entity);
    TEntity Parent(TEntity entity);
    void ReadTopology(HierarchyObservation<TEntity> result);
    int ChildCount(TEntity entity);
    void CopyChildren(TEntity entity, Span<TEntity> children);
    void SetOrdered(TEntity entity);
    void SetParents(ReadOnlySpan<HierarchyEdit<TEntity>> edits, bool deferred);
    void Detach(TEntity entity);
    void Maintain();
    void Destroy(TEntity entity);
    void DestroySubtree(TEntity entity);
    TEntity Create(int ordinal);
    void ReadFacts(ulong since, HierarchyObservation<TEntity> result);
    void ClearFacts();
}

internal sealed class HierarchyObservation<TEntity> where TEntity : struct
{
    internal readonly Dictionary<TEntity, int> Ordinals;
    internal readonly int[] Parents, ChildParents, OldParents, OldChildParents, OldChildCounts;
    internal readonly byte[] Facts;
    internal readonly int[]? ChildPlaces;
    internal int ActiveCount;
    internal bool Unique;

    internal HierarchyObservation(int count, Dictionary<TEntity, int> ordinals, bool ordered = false)
    {
        Ordinals = ordinals;
        Parents = new int[count]; ChildParents = new int[count];
        OldParents = new int[count]; OldChildParents = new int[count]; OldChildCounts = new int[count];
        Facts = new byte[count];
        if (ordered) ChildPlaces = new int[count];
    }

    internal void Reset()
    {
        Array.Fill(Parents, -2); Array.Fill(ChildParents, -1);
        Array.Fill(OldParents, -1); Array.Fill(OldChildParents, -1);
        Array.Clear(OldChildCounts); Array.Clear(Facts);
        if (ChildPlaces is not null) Array.Fill(ChildPlaces, -1);
        ActiveCount = 0; Unique = true;
    }

    internal int Ordinal(TEntity entity) => EqualityComparer<TEntity>.Default.Equals(entity, default) ? -1 : Ordinals[entity];
    internal void Parent(TEntity entity, TEntity parent) => Parents[Ordinal(entity)] = Ordinal(parent);
    internal void Children(TEntity parent, ReadOnlySpan<TEntity> children)
    {
        int p = Ordinal(parent);
        for (int i = 0; i < children.Length; i++)
        {
            int c = Ordinal(children[i]);
            Unique &= ChildParents[c] == -1;
            ChildParents[c] = p;
            if (ChildPlaces is not null) ChildPlaces[c] = i;
        }
    }
    internal void Fact(TEntity entity, byte flag) => Facts[Ordinal(entity)] |= flag;
    internal void OldParent(TEntity entity, TEntity parent)
    {
        int ordinal = Ordinal(entity);
        Facts[ordinal] |= 4;
        OldParents[ordinal] = Ordinal(parent);
    }
    internal void OldChildren(TEntity entity, ReadOnlySpan<TEntity> children)
    {
        int ordinal = Ordinal(entity);
        Facts[ordinal] |= 32;
        OldChildCounts[ordinal] = children.Length;
        foreach (TEntity child in children) OldChildParents[Ordinal(child)] = ordinal;
    }
}

internal sealed class HierarchyScenario<TEntity, TApi> : IHierarchyScenario
    where TEntity : struct where TApi : IHierarchyComparison<TEntity>
{
    private readonly TApi _api;
    private readonly TEntity[] _entities;
    private readonly bool _deep, _ordered;
    private readonly TEntity[]? _growthOrder;
    private readonly HierarchyEdit<TEntity>[] _edits, _restore, _growthEdits;
    private readonly TEntity[] _growing;
    private readonly Dictionary<TEntity, int> _ordinals;
    private readonly HierarchyObservation<TEntity> _deferred, _moved, _deleted;
    private bool _growthValid = true;

    internal HierarchyScenario(TApi api, TEntity[] entities, bool deep, bool ordered = false)
    {
        _api = api; _entities = entities; _deep = deep; _ordered = ordered;
        if (ordered) _growthOrder = new TEntity[entities.Length];
        _ordinals = new(entities.Length);
        for (int i = 0; i < entities.Length; i++) _ordinals.Add(entities[i], i);
        _edits = new HierarchyEdit<TEntity>[(entities.Length - 1) / 4];
        _restore = new HierarchyEdit<TEntity>[entities.Length - (deep ? 1 : 2)];
        _growing = new TEntity[entities.Length]; _growthEdits = new HierarchyEdit<TEntity>[entities.Length];
        _deferred = new(entities.Length, _ordinals, ordered); _moved = new(entities.Length, _ordinals, ordered); _deleted = new(entities.Length, _ordinals, ordered);
        RestoreLinks();
        _api.Maintain(); _api.ClearFacts();
    }

    private void RestoreLinks()
    {
        if (_ordered) { _api.SetOrdered(_entities[0]); _api.SetOrdered(_entities[1]); }
        int first = _deep ? 1 : 2;
        for (int i = first; i < _entities.Length; i++)
            _restore[i - first] = new(_entities[i], _entities[_deep ? i - 1 : 0]);
        _api.SetParents(_restore, false);
    }

    private void Observe(HierarchyObservation<TEntity> output, ulong since)
    {
        output.Reset();
        for (int i = 0; i < _entities.Length; i++)
        {
            TEntity entity = _entities[i];
            if (!_api.Active(entity)) continue;
            output.ActiveCount++;
            output.Parents[i] = -1;
        }
        _api.ReadTopology(output);
        _api.ReadFacts(since, output);
    }

    public void Cycle()
    {
        ulong before = _api.Version;
        int count = 0;
        for (int i = 4; i < _entities.Length; i += 4)
            _edits[count++] = new(_entities[i], _entities[_deep ? 0 : 1]);
        _api.SetParents(_edits, true);
        Observe(_deferred, before);
        _api.Maintain();
        _api.Detach(_entities[4]);
        Observe(_moved, before);
        _api.ClearFacts();
        before = _api.Version;
        if (_deep) _api.DestroySubtree(_entities[1]); else _api.Destroy(_entities[0]);
        Observe(_deleted, before);
        _api.ClearFacts();
        int start = _deep ? 1 : 0, end = _deep ? 4 : 1;
        for (int i = start; i < end; i++)
        {
            _ordinals.Remove(_entities[i]);
            _entities[i] = _api.Create(i);
            _ordinals.Add(_entities[i], i);
        }
        RestoreLinks();
        _api.Maintain(); _api.ClearFacts();
    }

    public void Grow()
    {
        for (int i = 0; i < _growing.Length; i++) _growing[i] = _api.Create(_entities.Length + i);
        if (_ordered) _api.SetOrdered(_growing[0]);
        for (int i = 0; i < _growing.Length; i++)
            _growthEdits[i] = new(_growing[i], i == 0 ? _entities[0] : _growing[_deep ? i - 1 : 0]);
        _api.SetParents(_growthEdits, false);
        bool valid = true;
        for (int i = 0; i < _growing.Length; i++)
        {
            int children = _deep ? (i + 1 < _growing.Length ? 1 : 0) : (i == 0 ? _growing.Length - 1 : 0);
            valid &= EqualityComparer<TEntity>.Default.Equals(_api.Parent(_growing[i]), _growthEdits[i].Parent)
                && _api.ChildCount(_growing[i]) == children;
        }
        if (_ordered)
        {
            int count = _deep ? 1 : _growing.Length - 1;
            _api.CopyChildren(_growing[0], _growthOrder.AsSpan(0, count));
            for (int i = 0; i < count; i++) valid &= EqualityComparer<TEntity>.Default.Equals(_growthOrder![i], _growing[i + 1]);
        }
        _api.DestroySubtree(_growing[0]);
        foreach (TEntity entity in _growing) valid &= !_api.Active(entity);
        _api.ClearFacts(); _api.Maintain();
        _growthValid = valid;
    }

    private int InitialParent(int ordinal) => ordinal < (_deep ? 1 : 2) ? -1 : _deep ? ordinal - 1 : 0;
    private int MovedParent(int ordinal) => ordinal == 4 ? -1
        : ordinal >= 4 && ordinal % 4 == 0 ? (_deep ? 0 : 1) : InitialParent(ordinal);

    public bool Check()
    {
        bool valid = _growthValid && _deferred.Unique && _moved.Unique && _deleted.Unique
            && _deferred.ActiveCount == _entities.Length && _moved.ActiveCount == _entities.Length
            && _deleted.ActiveCount == _entities.Length - (_deep ? 3 : 1)
            && CheckOrder(_deferred, 0) && CheckOrder(_moved, 1) && CheckOrder(_deleted, 2);
        for (int i = 0; i < _entities.Length; i++)
        {
            bool selected = i >= 4 && i % 4 == 0;
            int main = selected ? (_deep ? 0 : 1) : InitialParent(i);
            valid &= _deferred.Parents[i] == main && _deferred.ChildParents[i] == InitialParent(i)
                && _deferred.Facts[i] == (selected ? 2 : 0);
            int moved = MovedParent(i);
            valid &= _moved.Parents[i] == moved && _moved.ChildParents[i] == moved;
            byte movedFacts = selected ? (byte)(i == 4 ? 4 : 2) : (byte)0;
            if (i == 0) movedFacts |= 16;
            if (!_deep && i == 1) movedFacts |= 24;
            if (_deep && i >= 3 && i % 4 == 3 && i + 1 < _entities.Length) movedFacts |= 32;
            valid &= _moved.Facts[i] == movedFacts;
            if (i == 4) valid &= _moved.OldParents[i] == (_deep ? 0 : 1);
            if (_deep && selected) valid &= _moved.OldChildParents[i] == i - 1 && _moved.OldChildCounts[i - 1] == 1;
            bool dead = _deep ? i is >= 1 and <= 3 : i == 0;
            int final = dead ? -2 : !_deep && moved == 0 ? -1 : moved;
            valid &= _deleted.Parents[i] == final && _deleted.ChildParents[i] == (dead ? -1 : final);
            byte deletedFacts = 0;
            if (_deep)
            {
                if (i is >= 1 and <= 3) deletedFacts |= 4;
                if (i is 1 or 2) deletedFacts |= 32;
                if (i == 0) deletedFacts |= 16;
                if (i is >= 1 and <= 3) valid &= _deleted.OldParents[i] == i - 1;
                if (i is 2 or 3) valid &= _deleted.OldChildParents[i] == i - 1 && _deleted.OldChildCounts[i - 1] == 1;
            }
            else
            {
                if (moved == 0) deletedFacts |= 4;
                if (i == 0) deletedFacts |= 32;
                if (moved == 0) valid &= _deleted.OldParents[i] == 0;
            }
            valid &= _deleted.Facts[i] == deletedFacts;
            valid &= _api.Active(_entities[i]) && _ordinals.GetValueOrDefault(_api.Parent(_entities[i]), -1) == InitialParent(i);
        }
        if (!_deep) valid &= _deleted.OldChildCounts[0] == 1;
        return valid;
    }

    private bool CheckOrder(HierarchyObservation<TEntity> result, int stage)
    {
        if (!_ordered) return true;
        for (int i = 0; i < _entities.Length; i++)
        {
            int parent = result.ChildParents[i], place = -1;
            if (parent >= 0)
            {
                if (_deep) place = parent != 0 || stage == 0 || i == 1 ? 0 : i / 4 - stage;
                else place = stage == 0 ? i - 2 : parent == 0 ? i - 2 - i / 4 : i / 4 - 2;
            }
            if (result.ChildPlaces![i] != place) return false;
        }
        return true;
    }

    public void Dispose() => _api.Dispose();
}

internal readonly struct ComparisonHierarchyDomain : IHierarchyDomain;

internal sealed class SomeHierarchyComparison : IHierarchyComparison<Entity>
{
    private readonly World _world;
    private readonly ParentEdit[] _edits;
    private readonly Query[] _facts;
    private readonly Query _parents, _children;
    internal SomeHierarchyComparison(World world, int count)
    {
        _world = world; _edits = new ParentEdit[count];
        _parents = world.Query<Parent<ComparisonHierarchyDomain>>();
        _children = world.Query<Children<ComparisonHierarchyDomain>>();
        _facts = [
            world.Query(new QueryDescription().WithAdded<Parent<ComparisonHierarchyDomain>>(0)),
            world.Query(new QueryDescription().WithChanged<Parent<ComparisonHierarchyDomain>>(0)),
            world.Query<Removed<Parent<ComparisonHierarchyDomain>>>(),
            world.Query(new QueryDescription().WithAdded<Children<ComparisonHierarchyDomain>>(0)),
            world.Query(new QueryDescription().WithChanged<Children<ComparisonHierarchyDomain>>(0)),
            world.Query<Removed<Children<ComparisonHierarchyDomain>>>()];
    }
    public ulong Version => _world.Version;
    public bool Active(Entity entity) => _world.IsAlive(entity) && !_world.IsPendingCleanup(entity);
    public Entity Parent(Entity entity) => Hierarchy<ComparisonHierarchyDomain>.GetParent(_world, entity);
    public void ReadTopology(HierarchyObservation<Entity> result)
    {
        using (var scope = _parents.Open())
            foreach (var rows in scope)
            {
                var parents = rows.Read<Parent<ComparisonHierarchyDomain>>();
                var entities = rows.Entities;
                for (int i = 0; i < parents.Length; i++) result.Parent(entities[i], parents[i].Value);
            }
        using (var scope = _children.Open())
            foreach (var rows in scope)
            {
                var children = rows.Read<Children<ComparisonHierarchyDomain>>();
                var entities = rows.Entities;
                for (int i = 0; i < children.Length; i++) result.Children(entities[i], children[i].Entities);
            }
    }
    public int ChildCount(Entity entity)
    {
        using var children = Hierarchy<ComparisonHierarchyDomain>.GetChildren(_world, entity);
        return children.Count;
    }
    public void CopyChildren(Entity entity, Span<Entity> children)
    { using var values = Hierarchy<ComparisonHierarchyDomain>.GetChildren(_world, entity); values.Entities.CopyTo(children); }
    public void SetOrdered(Entity entity) => Hierarchy<ComparisonHierarchyDomain>.SetOrdered(_world, entity, true);
    public void SetParents(ReadOnlySpan<HierarchyEdit<Entity>> edits, bool deferred)
    {
        for (int i = 0; i < edits.Length; i++) _edits[i] = new(edits[i].Child, edits[i].Parent);
        Hierarchy<ComparisonHierarchyDomain>.SetParents(_world, _edits.AsSpan(0, edits.Length), deferred);
    }
    public void Detach(Entity entity) => Hierarchy<ComparisonHierarchyDomain>.Detach(_world, entity);
    public void Maintain() => Hierarchy<ComparisonHierarchyDomain>.Maintain(_world);
    public void Destroy(Entity entity) => _world.Destroy(entity);
    public void DestroySubtree(Entity entity) => Hierarchy<ComparisonHierarchyDomain>.DestroySubtree(_world, entity);
    public Entity Create(int ordinal)
    {
        Body body = Body.Moving(new Position { X = ordinal });
        return ordinal % 4 == 0 ? _world.Spawn(new SleepingTeamBody { Body = body })
            : ordinal % 2 == 0 ? _world.Spawn(new TeamBody { Body = body }) : _world.Spawn(body);
    }
    public void ReadFacts(ulong since, HierarchyObservation<Entity> result)
    {
        for (int kind = 0; kind < _facts.Length; kind++)
        {
            using var scope = _facts[kind].Since(since).Open();
            foreach (var rows in scope)
            {
                if (kind == 2)
                {
                    var values = rows.Read<Removed<Parent<ComparisonHierarchyDomain>>>();
                    var entities = rows.Entities;
                    for (int i = 0; i < values.Length; i++)
                        if (values[i].Version > since) result.OldParent(entities[i], values[i].Value.Value);
                }
                else if (kind == 5)
                {
                    var values = rows.Read<Removed<Children<ComparisonHierarchyDomain>>>();
                    var entities = rows.Entities;
                    for (int i = 0; i < values.Length; i++)
                        if (values[i].Version > since) result.OldChildren(entities[i], values[i].Value.Entities);
                }
                else foreach (Entity entity in rows.Entities) result.Fact(entity, (byte)(1 << kind));
            }
        }
    }
    public void ClearFacts()
    {
        _world.ClearRemoved<Parent<ComparisonHierarchyDomain>>();
        _world.ClearRemoved<Children<ComparisonHierarchyDomain>>();
    }
    public void Dispose() { }
}

internal abstract partial class ComparisonWorld
{
    internal IHierarchyScenario? HierarchyScenario;
    internal void HierarchyCycle() => HierarchyScenario!.Cycle();
}

internal sealed partial class SomeEngineWorld
{
    private void PrepareHierarchy(string workload)
        => HierarchyScenario ??= new HierarchyScenario<Entity, SomeHierarchyComparison>(new(_world, Count), _entities,
            workload.Contains("deep"), workload.EndsWith("ordered"));
}
