using System.Buffers;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal static class ArchiveSequence
{
    internal static void Write<T, TCodec>(ref DataWriter writer, IEnumerable<T> values, int count, TCodec codec)
        where TCodec : struct, IValueCodec<T>
    {
        writer.WriteInt32(count);
        foreach (T value in values) { TCodec item = codec; item.Write(ref writer, value); }
    }
    internal static void Read<T, TCodec>(ref DataReader reader, ICollection<T> values, TCodec codec)
        where TCodec : struct, IValueCodec<T>
    {
        values.Clear(); int count = reader.ReadInt32();
        for (int i = 0; i < count; i++) { TCodec item = codec; item.Read(ref reader, out T value); values.Add(value); }
    }
}

internal sealed partial class ReferenceHierarchy<TEntity, TValues>
    where TEntity : struct where TValues : struct, IHierarchyValues<TEntity>
{
    internal bool ArchiveIsOrdered(TEntity parent) => _ordered.Contains(parent);
    internal void ClearProgress()
    {
        Array.Clear(_applied); Array.Clear(_targets);
        _pending.Clear(); _destroyed.Clear(); _ordered.Clear(); _oldParents.Clear(); _oldChildren.Clear();
        _differences.Clear(); _differenceSets.Clear(); _edits.Clear(); _forestSeen.Clear(); _headCounts.Clear();
        _headKeys.Clear(); _incoming.Clear(); _subtree.Clear(); _version = Version;
    }
    internal ReferenceHierarchy<TEntity, TValues> CopyProgress(int entityLength)
    {
        var copy = new ReferenceHierarchy<TEntity, TValues>(default, []);
        copy.RestoreProgress(this, entityLength); return copy;
    }
    internal void RestoreProgress(ReferenceHierarchy<TEntity, TValues> source)
        => RestoreProgress(source, int.MaxValue);

    private void RestoreProgress(ReferenceHierarchy<TEntity, TValues> source, int entityLength)
    {
        _version = source.Version;
        _applied = source._applied.AsSpan(0, Math.Min(entityLength, source._applied.Length)).ToArray();
        _targets = source._targets.AsSpan(0, Math.Min(entityLength, source._targets.Length)).ToArray();
        _pending.Clear(); _pending.UnionWith(source._pending); _destroyed.Clear(); _destroyed.UnionWith(source._destroyed);
        _oldParents.Clear(); _oldParents.AddRange(source._oldParents); _oldChildren.Clear(); _oldChildren.AddRange(source._oldChildren);
        _ordered.Clear(); _ordered.UnionWith(source._ordered); _differences.Clear();
        for (int i = 0; i < _targets.Length; i++) if (!Empty(_targets[i]))
        {
            if (!_differences.TryGetValue(_targets[i], out var set)) _differences.Add(_targets[i], set = new());
            set.Add(_applied[i].Entity);
        }
        _edits.Clear(); _forestSeen.Clear(); _headCounts.Clear(); _headKeys.Clear(); _incoming.Clear(); _subtree.Clear();
    }
    internal void WriteProgress<TCodec>(ref DataWriter writer, TCodec codec) where TCodec : struct, IValueCodec<TEntity>
    {
        writer.WriteUInt64(Version); writer.WriteInt32(_applied.Length);
        foreach (Applied applied in _applied)
        { codec.Write(ref writer, applied.Entity); codec.Write(ref writer, applied.Parent); writer.WriteInt32(applied.Place); }
        ArchiveSequence.Write(ref writer, _targets, _targets.Length, codec);
        ArchiveSequence.Write(ref writer, _pending, _pending.Count, codec);
        ArchiveSequence.Write(ref writer, _destroyed, _destroyed.Count, codec);
        ArchiveSequence.Write(ref writer, _oldParents, _oldParents.Count, codec);
        ArchiveSequence.Write(ref writer, _oldChildren, _oldChildren.Count, codec);
        ArchiveSequence.Write(ref writer, _ordered, _ordered.Count, codec);
    }
    internal void ReadProgress<TCodec>(ref DataReader reader, TCodec codec) where TCodec : struct, IValueCodec<TEntity>
    {
        _version = reader.ReadUInt64(); _applied = new Applied[reader.ReadInt32()];
        for (int i = 0; i < _applied.Length; i++)
        { codec.Read(ref reader, out _applied[i].Entity); codec.Read(ref reader, out _applied[i].Parent); _applied[i].Place = reader.ReadInt32(); }
        _targets = new TEntity[reader.ReadInt32()];
        for (int i = 0; i < _targets.Length; i++) codec.Read(ref reader, out _targets[i]);
        ArchiveSequence.Read(ref reader, _pending, codec); ArchiveSequence.Read(ref reader, _destroyed, codec);
        ArchiveSequence.Read(ref reader, _oldParents, codec); ArchiveSequence.Read(ref reader, _oldChildren, codec);
        ArchiveSequence.Read(ref reader, _ordered, codec);
        _differences.Clear();
        for (int i = 0; i < _targets.Length; i++)
            if (!Empty(_targets[i]))
            {
                if (!_differences.TryGetValue(_targets[i], out var set)) _differences.Add(_targets[i], set = new());
                set.Add(_applied[i].Entity);
            }
        _edits.Clear(); _forestSeen.Clear(); _headCounts.Clear(); _headKeys.Clear(); _incoming.Clear(); _subtree.Clear();
    }
}

internal sealed partial class ReferenceRelations<TEntity, TIdentity, TValues, TPayload>
    where TEntity : struct where TIdentity : struct, IEquatable<TIdentity>
    where TValues : struct, IRelationValues<TEntity, TIdentity, TPayload> where TPayload : struct, IRelationPayload<TPayload>
{
    internal (int Count, TEntity First, bool Ordered) ArchiveHead(TEntity endpoint, bool incoming)
    { Head head = ReadHead(endpoint, incoming); return (head.Count, head.Count == 0 ? default : head.Items![0].Edge, head.Ordered); }
    private static Head CopyHead(Head source)
    {
        var copy = source;
        if (source.Items is not null)
        {
            copy.Items = ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Rent(Math.Max(1, source.Count));
            source.Items.AsSpan(0, source.Count).CopyTo(copy.Items);
        }
        return copy;
    }
    internal ReferenceRelations<TEntity, TIdentity, TValues, TPayload> CopyProgress(int entityLength)
    {
        var copy = new ReferenceRelations<TEntity, TIdentity, TValues, TPayload>(default, 0);
        copy.RestoreProgress(this, entityLength); return copy;
    }
    internal void RestoreProgress(ReferenceRelations<TEntity, TIdentity, TValues, TPayload> source)
        => RestoreProgress(source, int.MaxValue);

    private void RestoreProgress(ReferenceRelations<TEntity, TIdentity, TValues, TPayload> source, int entityLength)
    {
        _version = source.Version; _usesOrder = source._usesOrder;
        _outgoing = new Head[Math.Min(entityLength, source._outgoing.Length)]; for (int i = 0; i < _outgoing.Length; i++) _outgoing[i] = CopyHead(source._outgoing[i]);
        _incoming = new Head[Math.Min(entityLength, source._incoming.Length)]; for (int i = 0; i < _incoming.Length; i++) _incoming[i] = CopyHead(source._incoming[i]);
        _applied = source._applied.AsSpan(0, Math.Min(entityLength, source._applied.Length)).ToArray();
        _sourceClaims = source._sourceClaims is null ? null : source._sourceClaims.AsSpan(0, Math.Min(entityLength, source._sourceClaims.Length)).ToArray();
        _targetClaims = source._targetClaims is null ? null : source._targetClaims.AsSpan(0, Math.Min(entityLength, source._targetClaims.Length)).ToArray();
        _pairs?.Clear(); if (source._pairs is not null) foreach (var pair in source._pairs) _pairs!.Add(pair.Key, pair.Value);
        _retiredHeads.Clear(); foreach (var pair in source._retiredHeads) _retiredHeads.Add(pair.Key, CopyHead(pair.Value));
        _retiredApplied.Clear(); foreach (var pair in source._retiredApplied) _retiredApplied.Add(pair.Key, pair.Value);
        _pending.Clear(); _pending.UnionWith(source._pending); _destroyed.Clear(); _destroyed.UnionWith(source._destroyed);
        _destroyedWork.Clear(); _destroyedWork.AddRange(source._destroyedWork); _removed.Clear(); _removed.AddRange(source._removed);
        _future.Clear(); foreach (var pair in source._future) _future.Add(pair.Key, new(pair.Value));
        _futureOrders.Clear(); foreach (var pair in source._futureOrders) _futureOrders.Add(pair.Key, new(pair.Value));
    }
    private static void WriteApplied<TECodec, TICodec>(ref DataWriter writer, Applied value, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        entity.Write(ref writer, value.Source); entity.Write(ref writer, value.Target); identity.Write(ref writer, value.Identity);
        writer.WriteInt32(value.SourcePlace); writer.WriteInt32(value.TargetPlace);
    }
    private static Applied ReadApplied<TECodec, TICodec>(ref DataReader reader, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        Applied value = default; entity.Read(ref reader, out value.Source); entity.Read(ref reader, out value.Target);
        identity.Read(ref reader, out value.Identity); value.SourcePlace = reader.ReadInt32(); value.TargetPlace = reader.ReadInt32(); return value;
    }
    private static void WriteHead<TECodec, TICodec>(ref DataWriter writer, Head head, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        identity.Write(ref writer, head.Identity); writer.WriteInt32(head.CountFlags);
        for (int i = 0; i < head.Count; i++)
        { entity.Write(ref writer, head.Items![i].Other); entity.Write(ref writer, head.Items[i].Edge); }
    }
    private static Head ReadHead<TECodec, TICodec>(ref DataReader reader, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        Head head = default; identity.Read(ref reader, out head.Identity); head.CountFlags = reader.ReadInt32();
        if (head.Count > 0) head.Items = ArrayPool<ReferenceRelationEntry<TEntity>>.Shared.Rent(head.Count);
        for (int i = 0; i < head.Count; i++)
        { entity.Read(ref reader, out TEntity other); entity.Read(ref reader, out TEntity edge); head.Items![i] = new(other, edge); }
        return head;
    }
    internal void WriteProgress<TECodec, TICodec>(ref DataWriter writer, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        writer.WriteUInt64(Version); writer.WriteBoolean(_usesOrder);
        writer.WriteInt32(_outgoing.Length); foreach (Head head in _outgoing) WriteHead(ref writer, head, entity, identity);
        writer.WriteInt32(_incoming.Length); foreach (Head head in _incoming) WriteHead(ref writer, head, entity, identity);
        writer.WriteInt32(_applied.Length); foreach (Applied applied in _applied) WriteApplied(ref writer, applied, entity, identity);
        WriteClaims(ref writer, _sourceClaims, entity, identity); WriteClaims(ref writer, _targetClaims, entity, identity);
        writer.WriteInt32(_pairs?.Count ?? 0);
        if (_pairs is not null) foreach (var pair in _pairs)
        { entity.Write(ref writer, pair.Key.Source); entity.Write(ref writer, pair.Key.Target); entity.Write(ref writer, pair.Value); }
        writer.WriteInt32(_retiredHeads.Count);
        foreach (var pair in _retiredHeads)
        {
            writer.WriteInt32(pair.Key.Id); identity.Write(ref writer, pair.Key.Identity); writer.WriteBoolean(pair.Key.Incoming);
            WriteHead(ref writer, pair.Value, entity, identity);
        }
        writer.WriteInt32(_retiredApplied.Count);
        foreach (var pair in _retiredApplied)
        { writer.WriteInt32(pair.Key.Id); identity.Write(ref writer, pair.Key.Identity); WriteApplied(ref writer, pair.Value, entity, identity); }
        ArchiveSequence.Write(ref writer, _pending, _pending.Count, entity); ArchiveSequence.Write(ref writer, _destroyed, _destroyed.Count, entity);
        ArchiveSequence.Write(ref writer, _destroyedWork, _destroyedWork.Count, entity); ArchiveSequence.Write(ref writer, _removed, _removed.Count, entity);
        writer.WriteInt32(_future.Count);
        foreach (var pair in _future)
        { entity.Write(ref writer, pair.Key); ArchiveSequence.Write(ref writer, pair.Value, pair.Value.Count, entity); }
        writer.WriteInt32(_futureOrders.Count);
        foreach (var pair in _futureOrders)
        { entity.Write(ref writer, pair.Key.Entity); writer.WriteBoolean(pair.Key.Incoming); ArchiveSequence.Write(ref writer, pair.Value, pair.Value.Count, entity); }
    }
    private static void WriteClaims<TECodec, TICodec>(ref DataWriter writer, EndpointClaim[]? claims, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        writer.WriteInt32(claims?.Length ?? 0);
        if (claims is not null) foreach (EndpointClaim claim in claims)
        { identity.Write(ref writer, claim.Identity); entity.Write(ref writer, claim.Edge); }
    }
    private static EndpointClaim[]? ReadClaims<TECodec, TICodec>(ref DataReader reader, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        int count = reader.ReadInt32(); if (count == 0) return null;
        var claims = new EndpointClaim[count];
        for (int i = 0; i < count; i++)
        { identity.Read(ref reader, out TIdentity key); entity.Read(ref reader, out TEntity edge); claims[i] = new(key, edge); }
        return claims;
    }
    internal void ReadProgress<TECodec, TICodec>(ref DataReader reader, TECodec entity, TICodec identity)
        where TECodec : struct, IValueCodec<TEntity> where TICodec : struct, IValueCodec<TIdentity>
    {
        _version = reader.ReadUInt64(); _usesOrder = reader.ReadBoolean();
        _outgoing = new Head[reader.ReadInt32()]; for (int i = 0; i < _outgoing.Length; i++) _outgoing[i] = ReadHead(ref reader, entity, identity);
        _incoming = new Head[reader.ReadInt32()]; for (int i = 0; i < _incoming.Length; i++) _incoming[i] = ReadHead(ref reader, entity, identity);
        _applied = new Applied[reader.ReadInt32()]; for (int i = 0; i < _applied.Length; i++) _applied[i] = ReadApplied(ref reader, entity, identity);
        _sourceClaims = ReadClaims(ref reader, entity, identity); _targetClaims = ReadClaims(ref reader, entity, identity);
        _pairs?.Clear(); int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        { entity.Read(ref reader, out TEntity source); entity.Read(ref reader, out TEntity target); entity.Read(ref reader, out TEntity edge); _pairs!.Add(new(source, target), edge); }
        _retiredHeads.Clear(); count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        { int id = reader.ReadInt32(); identity.Read(ref reader, out TIdentity key); bool incoming = reader.ReadBoolean(); _retiredHeads.Add(new(id, key, incoming), ReadHead(ref reader, entity, identity)); }
        _retiredApplied.Clear(); count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        { int id = reader.ReadInt32(); identity.Read(ref reader, out TIdentity key); _retiredApplied.Add(new(id, key), ReadApplied(ref reader, entity, identity)); }
        ArchiveSequence.Read(ref reader, _pending, entity); ArchiveSequence.Read(ref reader, _destroyed, entity);
        ArchiveSequence.Read(ref reader, _destroyedWork, entity); ArchiveSequence.Read(ref reader, _removed, entity);
        _future.Clear(); count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        { entity.Read(ref reader, out TEntity key); var members = new HashSet<TEntity>(); ArchiveSequence.Read(ref reader, members, entity); _future.Add(key, members); }
        _futureOrders.Clear(); count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        { entity.Read(ref reader, out TEntity key); bool incoming = reader.ReadBoolean(); var order = new List<TEntity>(); ArchiveSequence.Read(ref reader, order, entity); _futureOrders.Add(new(key, incoming), order); }
    }
}
