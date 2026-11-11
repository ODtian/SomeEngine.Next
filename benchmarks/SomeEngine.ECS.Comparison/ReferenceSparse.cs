using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

internal interface IReferenceSparseAccess<TEntity> where TEntity : struct
{
    int Resolve(TEntity entity, bool bind);
    int Id(TEntity entity);
    bool Matches(TEntity entity);
    TEntity Create(int ordinal);
    void Destroy(TEntity entity);
}

internal struct ReferenceSparseCell { internal int RowPlusOne, AttachmentPlusOne; }
internal struct ReferenceSparseAttachment { internal int Type, Previous, Next; }
internal static class ReferenceSparseTypes { private static int _next; internal static int Next() => Interlocked.Increment(ref _next) - 1; }
internal static class ReferenceSparseType<T> where T : struct
{
    internal static readonly int Id = ReferenceSparseTypes.Next();
    internal static readonly DisposeResource<T>? Release = typeof(IDisposable).IsAssignableFrom(typeof(T))
        ? ResourceDataType<T>.Dispose : null;
}

internal abstract class ReferenceSparseStore<TEntity, TAccess>
    where TEntity : struct where TAccess : struct, IReferenceSparseAccess<TEntity>
{
    internal TEntity[] Entities = new TEntity[16];
    internal int Count;
    protected ReferenceSparseCell[]?[] Pages = [];
    internal ref ReferenceSparseCell Find(int id)
    {
        int page = id >> 12;
        if ((uint)page < (uint)Pages.Length && Pages[page] is { } cells) return ref cells[id & 4095];
        return ref Unsafe.NullRef<ReferenceSparseCell>();
    }
    protected ref ReferenceSparseCell Ensure(int id)
    {
        int page = id >> 12;
        if (page >= Pages.Length) Array.Resize(ref Pages, Math.Max(page + 1, Math.Max(4, Pages.Length * 2)));
        return ref (Pages[page] ??= new ReferenceSparseCell[4096])[id & 4095];
    }
    internal bool Has(int id)
    {
        ref var cell = ref Find(id);
        return !Unsafe.IsNullRef(ref cell) && cell.RowPlusOne != 0;
    }
    internal abstract void RemoveKnown(ReferenceSparseStores<TEntity, TAccess> stores, int id);
    internal abstract void Clear(ReferenceSparseStores<TEntity, TAccess> stores);
}

internal sealed class ReferenceSparseStore<T, TEntity, TAccess> : ReferenceSparseStore<TEntity, TAccess>
    where T : struct where TEntity : struct where TAccess : struct, IReferenceSparseAccess<TEntity>
{
    internal T[] Values = new T[16];

    internal ref T Read(int id)
    {
        ref var cell = ref Find(id);
        if (Unsafe.IsNullRef(ref cell) || cell.RowPlusOne == 0) throw new ArgumentException("Sparse component is absent.");
        return ref Values[cell.RowPlusOne - 1];
    }
    internal void Set(ReferenceSparseStores<TEntity, TAccess> stores, TEntity entity, int id, in T value)
    {
        ref var cell = ref Find(id);
        if (!Unsafe.IsNullRef(ref cell) && cell.RowPlusOne != 0)
        {
            ReferenceSparseType<T>.Release?.Invoke(ref Values[cell.RowPlusOne - 1]);
            Values[cell.RowPlusOne - 1] = value;
            return;
        }
        stores.RequireStructure();
        if (Count == Values.Length)
        {
            Array.Resize(ref Values, Values.Length * 2);
            Array.Resize(ref Entities, Values.Length);
        }
        int attachment = stores.Attach(id, ReferenceSparseType<T>.Id);
        ref var added = ref Ensure(id);
        Entities[Count] = entity;
        Values[Count] = value;
        added = new() { RowPlusOne = ++Count, AttachmentPlusOne = attachment };
    }
    internal void Remove(ReferenceSparseStores<TEntity, TAccess> stores, int id)
    {
        ref var cell = ref Find(id);
        if (Unsafe.IsNullRef(ref cell) || cell.RowPlusOne == 0) return;
        stores.Detach(id, cell.AttachmentPlusOne);
        Drop(stores, ref cell);
    }
    internal override void RemoveKnown(ReferenceSparseStores<TEntity, TAccess> stores, int id)
    {
        ref var cell = ref Find(id);
        Drop(stores, ref cell);
    }
    private void Drop(ReferenceSparseStores<TEntity, TAccess> stores, ref ReferenceSparseCell cell)
    {
        int row = cell.RowPlusOne - 1;
        ReferenceSparseType<T>.Release?.Invoke(ref Values[row]);
        int last = --Count;
        if (row != last)
        {
            TEntity moved = Entities[last];
            Entities[row] = moved;
            Values[row] = Values[last];
            Find(stores.Access.Id(moved)).RowPlusOne = row + 1;
        }
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Values[last] = default;
        cell = default;
    }
    internal override void Clear(ReferenceSparseStores<TEntity, TAccess> stores)
    {
        for (int row = 0; row < Count; row++)
        {
            ReferenceSparseType<T>.Release?.Invoke(ref Values[row]);
            Find(stores.Access.Id(Entities[row])) = default;
        }
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(Values, 0, Count);
        Count = 0;
    }
}

internal sealed class ReferenceSparseStores<TEntity, TAccess> : IDisposable
    where TEntity : struct where TAccess : struct, IReferenceSparseAccess<TEntity>
{
    internal TAccess Access;
    private ReferenceSparseStore<TEntity, TAccess>?[] _types = [];
    private int[] _first;
    private ReferenceSparseAttachment[] _attachments = new ReferenceSparseAttachment[16];
    private int _used, _free, _readers;

    internal ReferenceSparseStores(TAccess access, int capacity) => (Access, _first) = (access, new int[capacity]);
    internal ReferenceSparseStore<T, TEntity, TAccess> Store<T>() where T : struct
    {
        int type = ReferenceSparseType<T>.Id;
        if ((uint)type < (uint)_types.Length && _types[type] is { } found)
            return Unsafe.As<ReferenceSparseStore<T, TEntity, TAccess>>(found);
        if (type >= _types.Length) Array.Resize(ref _types, Math.Max(type + 1, Math.Max(16, _types.Length * 2)));
        var store = new ReferenceSparseStore<T, TEntity, TAccess>();
        _types[type] = store;
        return store;
    }
    internal ref readonly T Read<T>(TEntity entity) where T : struct => ref Store<T>().Read(Access.Resolve(entity, false));
    internal void Set<T>(TEntity entity, in T value) where T : struct
    {
        int id = Access.Resolve(entity, true);
        Store<T>().Set(this, entity, id, value);
    }
    internal void Remove<T>(TEntity entity) where T : struct
    {
        RequireStructure();
        Store<T>().Remove(this, Access.Resolve(entity, false));
    }
    internal void RequireStructure()
    {
        if (_readers != 0) throw new InvalidOperationException("Finish the current sparse read scope before changing attachments.");
    }
    internal ReferenceSparseRead<T, TEntity, TAccess> OpenRead<T>() where T : struct
    {
        _readers++;
        return new(this, Store<T>());
    }
    internal void EndRead() => _readers--;
    internal int Attach(int id, int type)
    {
        if (id >= _first.Length) Array.Resize(ref _first, Math.Max(id + 1, _first.Length * 2));
        int index;
        if (_free != 0) { index = _free; _free = _attachments[index].Next; }
        else
        {
            index = ++_used;
            if (index == _attachments.Length) Array.Resize(ref _attachments, _attachments.Length * 2);
        }
        ref int first = ref _first[id];
        if (first != 0) _attachments[first].Previous = index;
        _attachments[index] = new() { Type = type, Next = first };
        first = index;
        return index;
    }
    internal void Detach(int id, int index)
    {
        ref var attachment = ref _attachments[index];
        if (attachment.Previous == 0) _first[id] = attachment.Next;
        else _attachments[attachment.Previous].Next = attachment.Next;
        if (attachment.Next != 0) _attachments[attachment.Next].Previous = attachment.Previous;
        attachment.Next = _free;
        _free = index;
    }
    internal void Destroy(TEntity entity)
    {
        RequireStructure();
        int id = Access.Resolve(entity, false);
        int first = (uint)id < (uint)_first.Length ? _first[id] : 0;
        if (first != 0)
        {
            int index = first, last;
            do
            {
                var attachment = _attachments[index];
                _types[attachment.Type]!.RemoveKnown(this, id);
                last = index;
                index = attachment.Next;
            } while (index != 0);
            _attachments[last].Next = _free;
            _free = first;
            _first[id] = 0;
        }
        Access.Destroy(entity);
    }
    internal int Mixed<T, TOther>(Span<int> ordinals) where T : struct, ISparseOrdinal where TOther : struct
    {
        using var scope = OpenRead<T>();
        var first = Store<T>();
        var other = Store<TOther>();
        ReferenceSparseStore<TEntity, TAccess> driver = first.Count <= other.Count ? first : other;
        ReferenceSparseStore<TEntity, TAccess> secondary = ReferenceEquals(driver, first) ? other : first;
        int count = 0;
        foreach (TEntity entity in driver.Entities.AsSpan(0, driver.Count))
        {
            int id = Access.Id(entity);
            if (!secondary.Has(id) || !Access.Matches(entity)) continue;
            ordinals[count++] = first.Read(id).Ordinal;
        }
        return count;
    }
    public void Dispose()
    {
        RequireStructure();
        foreach (var store in _types) store?.Clear(this);
        Array.Clear(_first);
        _used = _free = 0;
    }
}

internal readonly ref struct ReferenceSparseRead<T, TEntity, TAccess>
    where T : struct where TEntity : struct where TAccess : struct, IReferenceSparseAccess<TEntity>
{
    private readonly ReferenceSparseStores<TEntity, TAccess> _stores;
    private readonly ReferenceSparseStore<T, TEntity, TAccess> _store;
    internal ReferenceSparseRead(ReferenceSparseStores<TEntity, TAccess> stores, ReferenceSparseStore<T, TEntity, TAccess> store)
        => (_stores, _store) = (stores, store);
    internal ReadOnlySpan<TEntity> Entities => _store.Entities.AsSpan(0, _store.Count);
    internal ReadOnlySpan<T> Values => _store.Values.AsSpan(0, _store.Count);
    public void Dispose() => _stores.EndRead();
}

internal interface ISparseOrdinal { int Ordinal { get; } }
internal struct SparseNumber(ResourceCounter counter, int value, int ordinal) : IDisposable, ISparseOrdinal
{
    internal ResourceCounter Counter = counter;
    internal int Value = value;
    public int Ordinal { get; } = ordinal;
    public void Dispose() => Counter.Count++;
}
internal readonly record struct SparseWeight(int Value);

internal readonly struct EasySparseAccess(EasyEcs.World world) : IReferenceSparseAccess<EasyEcs.Entity>
{
    public int Id(EasyEcs.Entity entity) => entity.Id.Index;
    public int Resolve(EasyEcs.Entity entity, bool bind)
    {
        if (!entity.IsAlive) throw new ArgumentException("Entity generation has expired.");
        return entity.Id.Index;
    }
    public bool Matches(EasyEcs.Entity entity) => entity.Has<Position>() && entity.Has<Team>();
    public EasyEcs.Entity Create(int ordinal) => world.CreateEntity(new Position { X = ordinal }, new Velocity { X = 1, Y = 2 },
        new Health { Value = 100, Max = 100 }, new Team(), new Sleeping());
    public void Destroy(EasyEcs.Entity entity) => entity.Destroy();
}

internal readonly struct FrifloSparseAccess(Friflo.Engine.ECS.EntityStore world) : IReferenceSparseAccess<Friflo.Engine.ECS.Entity>
{
    public int Id(Friflo.Engine.ECS.Entity entity) => entity.Id;
    public int Resolve(Friflo.Engine.ECS.Entity entity, bool bind)
    {
        if (entity.IsNull) throw new ArgumentException("Entity generation has expired.");
        return entity.Id;
    }
    public bool Matches(Friflo.Engine.ECS.Entity entity) => entity.HasComponent<Position>() && entity.HasComponent<Team>();
    public Friflo.Engine.ECS.Entity Create(int ordinal) => world.CreateEntity(new Position { X = ordinal }, new Velocity { X = 1, Y = 2 },
        new Health { Value = 100, Max = 100 }, new Team(), new Sleeping());
    public void Destroy(Friflo.Engine.ECS.Entity entity) => entity.DeleteEntity();
}

internal readonly struct ArchSparseAccess(Arch.Core.World world) : IReferenceSparseAccess<Arch.Core.Entity>
{
    public int Id(Arch.Core.Entity entity) => entity.Id;
    public int Resolve(Arch.Core.Entity entity, bool bind)
    {
        if (!world.IsAlive(entity)) throw new ArgumentException("Entity generation has expired.");
        return entity.Id;
    }
    public bool Matches(Arch.Core.Entity entity) => world.Has<Position>(entity) && world.Has<Team>(entity);
    public Arch.Core.Entity Create(int ordinal) => world.Create(new Position { X = ordinal }, new Velocity { X = 1, Y = 2 },
        new Health { Value = 100, Max = 100 }, new Team(), new Sleeping());
    public void Destroy(Arch.Core.Entity entity) => world.Destroy(entity);
}

internal sealed class DefaultSparseIds
{
    private readonly Dictionary<DefaultEcs.Entity, int> _ids = new();
    private readonly Stack<int> _free = new();
    private int _used;
    internal int Read(DefaultEcs.Entity entity) => _ids.TryGetValue(entity, out int id) ? id : -1;
    internal int Bind(DefaultEcs.Entity entity)
    {
        if (_ids.TryGetValue(entity, out int id)) return id;
        id = _free.TryPop(out int reusable) ? reusable : _used++;
        _ids.Add(entity, id);
        return id;
    }
    internal void Remove(DefaultEcs.Entity entity)
    {
        if (_ids.Remove(entity, out int id)) _free.Push(id);
    }
}

internal readonly struct DefaultSparseAccess(DefaultEcs.World world, DefaultSparseIds ids) : IReferenceSparseAccess<DefaultEcs.Entity>
{
    public int Id(DefaultEcs.Entity entity) => ids.Read(entity);
    public int Resolve(DefaultEcs.Entity entity, bool bind)
    {
        if (!entity.IsAlive) throw new ArgumentException("Entity generation has expired.");
        return bind ? ids.Bind(entity) : ids.Read(entity);
    }
    public bool Matches(DefaultEcs.Entity entity) => entity.Has<Position>() && entity.Has<Team>();
    public DefaultEcs.Entity Create(int ordinal)
    {
        var entity = world.CreateEntity();
        entity.Set(new Position { X = ordinal });
        entity.Set(new Velocity { X = 1, Y = 2 });
        entity.Set(new Health { Value = 100, Max = 100 });
        entity.Set(new Team());
        entity.Set(new Sleeping());
        return entity;
    }
    public void Destroy(DefaultEcs.Entity entity) { ids.Remove(entity); entity.Dispose(); }
}
