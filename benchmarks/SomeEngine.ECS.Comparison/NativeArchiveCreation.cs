using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Serialization;
using Entity = SomeEngine.ECS.Entities.Entity;

namespace SomeEngine.ECS.Comparison;

internal static class NativeArchiveColumns
{
    internal static void Read<T, TCodec>(Span<T> values, ref DataReader reader, TCodec codec, bool raw)
        where T : struct where TCodec : struct, IValueCodec<T>
    {
        if (raw) reader.ReadRawBytes(MemoryMarshal.AsBytes(values));
        else
        {
            long start = reader.Position;
            for (int i = 0; i < values.Length; i++) { TCodec item = codec; item.Read(ref reader, out values[i]); }
            long consumed = reader.Position - start;
            if (reader.ReadInt32() != consumed)
                throw new InvalidDataException("Native component codec length differs from its column footer.");
        }
    }
    internal static void Copy<T>(ReadOnlySpan<T> values, Span<T> target, Func<T, T>? clone) where T : struct
    {
        if (clone is null) values.CopyTo(target);
        else for (int i = 0; i < values.Length; i++) target[i] = clone(values[i]);
    }
}

internal sealed partial class EasyArchiveStore
{
    private readonly Dictionary<ulong, EasyEcs.Prefab> _restoreTypes = new();
    private EasyEcs.Prefab ComponentPrefab(ulong componentTypes, NativeArchiveType[] types)
    {
        if (!_restoreTypes.TryGetValue(componentTypes, out var prefab))
        {
            prefab = new(); prefab.Set(new ArchiveIdentity());
            for (int i = 0; i < types.Length; i++) if ((componentTypes & (1UL << i)) != 0) types[i].Declare(prefab);
            _restoreTypes.Add(componentTypes, prefab);
        }
        return prefab;
    }
    internal override void CreateNative(Entity entity, ulong componentTypes)
    {
        var native = _world.Instantiate(ComponentPrefab(componentTypes, RowTypes));
        native.Get<ArchiveIdentity>() = new(entity); _entities[entity.Id] = native;
    }
    protected override void CreateGroup(NativeArchiveGroup group, NativeArchiveType[] types)
    {
        var prefab = ComponentPrefab(group.Types, types);
        var created = ArrayPool<EasyEcs.Entity>.Shared.Rent(group.Identities.Length);
        try
        {
            _world.Instantiate(prefab, created.AsSpan(0, group.Identities.Length));
            for (int i = 0; i < group.Identities.Length; i++)
            {
                ArchiveIdentity identity = group.Identities[i]; var native = created[i];
                native.Get<ArchiveIdentity>() = identity; _entities[identity.Value.Id] = native; Created(identity.Value);
            }
        }
        finally { ArrayPool<EasyEcs.Entity>.Shared.Return(created, true); }
    }
    internal override void WriteColumn<T>(NativeArchiveGroup group, ReadOnlySpan<T> values, Func<T, T>? clone)
    {
        if (!DirectRestore || typeof(T) == typeof(IndexedNumber) || typeof(T) == typeof(SharedNumberId))
        { base.WriteColumn(group, values, clone); return; }
        Span<T> target = MemoryMarshal.CreateSpan(ref _entities[group.Identities[0].Value.Id].Get<T>(), values.Length);
        NativeArchiveColumns.Copy(values, target, clone);
    }
    internal override void ReadColumn<T, TCodec>(NativeArchiveGroup group, int count, ref DataReader reader, TCodec codec, bool raw)
    {
        if (!DirectRestore || typeof(T) == typeof(IndexedNumber) || typeof(T) == typeof(SharedNumberId))
        { base.ReadColumn<T, TCodec>(group, count, ref reader, codec, raw); return; }
        Span<T> target = MemoryMarshal.CreateSpan(ref _entities[group.Identities[0].Value.Id].Get<T>(), count);
        NativeArchiveColumns.Read(target, ref reader, codec, raw);
    }
}

internal sealed partial class FrifloArchiveStore
{
    private readonly Dictionary<ulong, Friflo.Engine.ECS.Archetype> _restoreTypes = new();
    private Friflo.Engine.ECS.Archetype ComponentArchetype(ulong componentTypes, NativeArchiveType[] types)
    {
        if (!_restoreTypes.TryGetValue(componentTypes, out var archetype))
        {
            var components = Friflo.Engine.ECS.ComponentTypes.Get<ArchiveIdentity>();
            for (int i = 0; i < types.Length; i++) if ((componentTypes & (1UL << i)) != 0) types[i].Declare(ref components);
            archetype = _world.GetArchetype(components, default); _restoreTypes.Add(componentTypes, archetype);
        }
        return archetype;
    }
    internal override void CreateNative(Entity entity, ulong componentTypes)
    {
        var native = ComponentArchetype(componentTypes, RowTypes).CreateEntity();
        native.GetComponent<ArchiveIdentity>() = new(entity); _entities[entity.Id] = native;
    }
    protected override void CreateGroup(NativeArchiveGroup group, NativeArchiveType[] types)
    {
        var archetype = ComponentArchetype(group.Types, types);
        archetype.EnsureCapacity(archetype.Count + group.Identities.Length);
        foreach (ArchiveIdentity identity in group.Identities)
        {
            var native = archetype.CreateEntity(); native.GetComponent<ArchiveIdentity>() = identity;
            _entities[identity.Value.Id] = native; Created(identity.Value);
        }
    }
    internal override void WriteColumn<T>(NativeArchiveGroup group, ReadOnlySpan<T> values, Func<T, T>? clone)
    {
        if (!DirectRestore || typeof(T) == typeof(IndexedNumber) || typeof(T) == typeof(SharedNumberId))
        { base.WriteColumn(group, values, clone); return; }
        Span<T> target = MemoryMarshal.CreateSpan(ref _entities[group.Identities[0].Value.Id].GetComponent<T>(), values.Length);
        NativeArchiveColumns.Copy(values, target, clone);
    }
    internal override void ReadColumn<T, TCodec>(NativeArchiveGroup group, int count, ref DataReader reader, TCodec codec, bool raw)
    {
        if (!DirectRestore || typeof(T) == typeof(IndexedNumber) || typeof(T) == typeof(SharedNumberId))
        { base.ReadColumn<T, TCodec>(group, count, ref reader, codec, raw); return; }
        Span<T> target = MemoryMarshal.CreateSpan(ref _entities[group.Identities[0].Value.Id].GetComponent<T>(), count);
        NativeArchiveColumns.Read(target, ref reader, codec, raw);
    }
}

internal sealed partial class ArchArchiveStore
{
    private readonly Dictionary<ulong, Arch.Core.Signature> _restoreTypes = new();
    private Arch.Core.Signature ComponentSignature(ulong componentTypes, NativeArchiveType[] types)
    {
        if (!_restoreTypes.TryGetValue(componentTypes, out var signature))
        {
            var components = new List<Arch.Core.ComponentType> { Arch.Core.Component<ArchiveIdentity>.ComponentType };
            for (int i = 0; i < types.Length; i++) if ((componentTypes & (1UL << i)) != 0) types[i].Declare(components);
            signature = new(components.ToArray()); _restoreTypes.Add(componentTypes, signature);
        }
        return signature;
    }
    internal override void CreateNative(Entity entity, ulong componentTypes)
    {
        var signature = ComponentSignature(componentTypes, RowTypes);
        var native = _world.Create(in signature);
        _world.Get<ArchiveIdentity>(native) = new(entity); _entities[entity.Id] = native;
    }
    protected override void CreateGroup(NativeArchiveGroup group, NativeArchiveType[] types)
    {
        var signature = ComponentSignature(group.Types, types);
        var created = ArrayPool<Arch.Core.Entity>.Shared.Rent(group.Identities.Length);
        try
        {
            _world.Create(created.AsSpan(0, group.Identities.Length), in signature, group.Identities.Length);
            for (int i = 0; i < group.Identities.Length; i++)
            {
                ArchiveIdentity identity = group.Identities[i]; var native = created[i];
                _world.Get<ArchiveIdentity>(native) = identity; _entities[identity.Value.Id] = native; Created(identity.Value);
            }
        }
        finally { ArrayPool<Arch.Core.Entity>.Shared.Return(created); }
    }
    internal override void WriteColumn<T>(NativeArchiveGroup group, ReadOnlySpan<T> values, Func<T, T>? clone)
    {
        if (!DirectRestore) { base.WriteColumn(group, values, clone); return; }
        for (int i = 0; i < values.Length; i++)
            _world.Get<T>(_entities[group.Identities[i].Value.Id]) = clone is null ? values[i] : clone(values[i]);
    }
}
