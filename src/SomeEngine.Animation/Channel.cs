using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Job;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

public delegate ref TValue FieldRef<TComponent, TValue>(ref TComponent component);

/// <summary>A component field identified by its component number and generated field number.</summary>
public readonly unsafe struct Channel<T> : IEquatable<Channel<T>> where T : unmanaged
{
    public readonly int Component;
    public readonly uint Field;
    public readonly ushort Offset;
    private readonly int _access;

    private Channel(int component, ushort offset,
        delegate* managed<World, Channel<T>, ReadOnlySpan<FieldWrite<T>>, void> writeBatch)
    {
        Component = component;
        Field = 0x80000000u | offset;
        Offset = offset;
        _access = FieldDescriptions<T>.Add(component, Field, new(null, null, null, null, writeBatch, false));
    }

    private Channel(int component, ushort field,
        delegate* managed<World, SomeEngine.ECS.Entities.Entity, T> read,
        delegate* managed<World, SomeEngine.ECS.Entities.Entity, in T, void> write,
        delegate* managed<ref byte, T> readRoot,
        delegate* managed<ref byte, in T, void> writeRoot,
        delegate* managed<World, Channel<T>, ReadOnlySpan<FieldWrite<T>>, void> writeBatch,
        bool throughReference, ushort offset)
    {
        Component = component;
        Field = field;
        Offset = offset;
        _access = FieldDescriptions<T>.Add(component, field, new FieldDescription<T>(read, write, readRoot, writeRoot, writeBatch, throughReference));
    }

    /// <summary>Connects a generated identity to the common typed ECS field access.</summary>
    public static Channel<T> Of<TComponent, TAccess>(ushort field, bool throughReference = true, ushort offset = ushort.MaxValue)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, T> =>
        new(ComponentId<TComponent>.Value, field,
            &ReadField<TComponent, TAccess>, &WriteField<TComponent, TAccess>,
            &ReadComponent<TComponent, TAccess>, &WriteComponent<TComponent, TAccess>, &WriteFields<TComponent, TAccess>, throughReference, offset);

    /// <summary>Connects a field to its actual referenced data so independent targets can write in parallel.</summary>
    public static Channel<T> Of<TComponent, TAccess, TResources>(ushort field, bool throughReference = true, ushort offset = ushort.MaxValue)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, T>
        where TResources : struct, IJobComponentResources<TComponent> =>
        new(ComponentId<TComponent>.Value, field,
            &ReadField<TComponent, TAccess>, &WriteField<TComponent, TAccess>,
            &ReadComponent<TComponent, TAccess>, &WriteComponent<TComponent, TAccess>,
            &WriteResourceFields<TComponent, TAccess, TResources>, throughReference, offset);

    public bool ThroughReference => _access != 0 && FieldDescriptions<T>.Items[_access - 1].ThroughReference;

    public T Read(World world, SomeEngine.ECS.Entities.Entity entity) => _access != 0 && FieldDescriptions<T>.Items[_access - 1].Read != null
        ? FieldDescriptions<T>.Items[_access - 1].Read(world, entity)
        : Unsafe.ReadUnaligned<T>(in world.ReadBytes(entity, Component, Offset, Unsafe.SizeOf<T>()));

    public void Write(World world, SomeEngine.ECS.Entities.Entity entity, in T value)
    {
        if (_access != 0 && FieldDescriptions<T>.Items[_access - 1].Write != null) FieldDescriptions<T>.Items[_access - 1].Write(world, entity, in value);
        else Unsafe.WriteUnaligned(ref world.WriteBytes(entity, Component, Offset, Unsafe.SizeOf<T>()), value);
    }

    /// <summary>Writes a batch through the concrete root and access types selected by this declaration.</summary>
    public void WriteBatch(World world, ReadOnlySpan<FieldWrite<T>> writes)
    {
        if (_access != 0) FieldDescriptions<T>.Items[_access - 1].WriteBatch(world, this, writes);
        else
            foreach (ref readonly FieldWrite<T> write in writes)
                if (write.Channel == this)
                    Unsafe.WriteUnaligned(ref world.WriteBytes(write.Target, Component, Offset, Unsafe.SizeOf<T>()), write.Value);
    }

    public T ReadRoot<TComponent>(in TComponent component) where TComponent : struct
    {
        ref byte root = ref Unsafe.As<TComponent, byte>(ref Unsafe.AsRef(in component));
        return _access != 0 && FieldDescriptions<T>.Items[_access - 1].ReadRoot != null ? FieldDescriptions<T>.Items[_access - 1].ReadRoot(ref root) : Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref root, Offset));
    }

    public void WriteRoot<TComponent>(ref TComponent component, in T value) where TComponent : struct
    {
        ref byte root = ref Unsafe.As<TComponent, byte>(ref component);
        if (_access != 0 && FieldDescriptions<T>.Items[_access - 1].WriteRoot != null) FieldDescriptions<T>.Items[_access - 1].WriteRoot(ref root, in value);
        else Unsafe.WriteUnaligned(ref Unsafe.Add(ref root, Offset), value);
    }

    private static T ReadField<TComponent, TAccess>(World world, SomeEngine.ECS.Entities.Entity entity)
        where TComponent : struct, IComponent where TAccess : struct, IFieldAccess<TComponent, T> =>
        world.ReadField<TComponent, T, TAccess>(entity);

    private static void WriteField<TComponent, TAccess>(World world, SomeEngine.ECS.Entities.Entity entity, in T value)
        where TComponent : struct, IComponent where TAccess : struct, IFieldAccess<TComponent, T> =>
        world.WriteField<TComponent, T, TAccess>(entity, in value);

    private static T ReadComponent<TComponent, TAccess>(ref byte root)
        where TComponent : struct, IComponent where TAccess : struct, IFieldAccess<TComponent, T> =>
        TAccess.Read(in Unsafe.As<byte, TComponent>(ref root));

    private static void WriteComponent<TComponent, TAccess>(ref byte root, in T value)
        where TComponent : struct, IComponent where TAccess : struct, IFieldAccess<TComponent, T> =>
        TAccess.Write(ref Unsafe.As<byte, TComponent>(ref root), in value);

    private static void WriteFields<TComponent, TAccess>(World world, Channel<T> channel, ReadOnlySpan<FieldWrite<T>> writes)
        where TComponent : struct, IComponent where TAccess : struct, IFieldAccess<TComponent, T>
    {
        if (typeof(ISparseComponent).IsAssignableFrom(typeof(TComponent)))
        {
            var sparse = world.OpenWrite<TComponent>();
            try
            {
                foreach (ref readonly FieldWrite<T> write in writes)
                    if (write.Channel == channel) TAccess.Write(ref sparse.Get(write.Target), in write.Value);
            }
            finally { sparse.Dispose(); }
            return;
        }
        var roots = world.OpenWrite<TComponent>();
        try
        {
            foreach (ref readonly FieldWrite<T> write in writes)
                if (write.Channel == channel) TAccess.Write(ref roots.Get(write.Target), in write.Value);
        }
        finally { roots.Dispose(); }
    }

    private static void WriteResourceFields<TComponent, TAccess, TResources>(World world, Channel<T> channel,
        ReadOnlySpan<FieldWrite<T>> writes)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, T>
        where TResources : struct, IJobComponentResources<TComponent>
    {
        if (!channel.ThroughReference)
        {
            WriteFields<TComponent, TAccess>(world, channel, writes);
            return;
        }
        int count = 0;
        foreach (ref readonly FieldWrite<T> write in writes) if (write.Channel == channel) count++;
        if (count == 0) return;
        TComponent[] components = ArrayPool<TComponent>.Shared.Rent(count);
        FieldWrite<T>[] values = ArrayPool<FieldWrite<T>>.Shared.Rent(count);
        Scheduler scheduler = world.Execution?.Scheduler ?? JobSystem.Current;
        int batchSize = Math.Max(1, (count + scheduler.WorkerCount * 4) / (scheduler.WorkerCount * 4 + 1));
        int batchCount = (count + batchSize - 1) / batchSize;
        JobHandle[] handles = ArrayPool<JobHandle>.Shared.Rent(batchCount);
        var roots = world.OpenWrite<TComponent>();
        try
        {
            int index = 0;
            foreach (ref readonly FieldWrite<T> write in writes)
            {
                if (write.Channel != channel) continue;
                components[index] = roots.Get(write.Target);
                values[index++] = write;
            }
            var accesses = new List<JobResourceAccess>();
            var resources = new JobResourceAccessBuilder(scheduler, accesses);
            for (int batch = 0; batch < batchCount; batch++)
            {
                int first = batch * batchSize, length = Math.Min(batchSize, count - first);
                accesses.Clear();
                ComponentResources.CollectRange<TComponent, TResources>(components.AsSpan(first, length), ref resources);
                handles[batch] = scheduler.Schedule(new ReferenceFieldWrites<TComponent, TAccess>(components, values, first, length),
                    CollectionsMarshal.AsSpan(accesses));
            }
            scheduler.CombineDependencies(handles.AsSpan(0, batchCount)).Complete();
        }
        finally
        {
            roots.Dispose();
            ArrayPool<JobHandle>.Shared.Return(handles);
            ArrayPool<TComponent>.Shared.Return(components, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<TComponent>());
            ArrayPool<FieldWrite<T>>.Shared.Return(values);
        }
    }

    private readonly struct ReferenceFieldWrites<TComponent, TAccess>(TComponent[] components, FieldWrite<T>[] values,
        int first, int count) : IJob
        where TComponent : struct, IComponent where TAccess : struct, IFieldAccess<TComponent, T>
    {
        public void Execute()
        {
            for (int index = first; index < first + count; index++) TAccess.Write(ref components[index], in values[index].Value);
        }
    }

    private static void WriteOffsetFields<TComponent>(World world, Channel<T> channel, ReadOnlySpan<FieldWrite<T>> writes)
        where TComponent : unmanaged, IComponent
    {
        if (typeof(ISparseComponent).IsAssignableFrom(typeof(TComponent)))
        {
            var sparse = world.OpenWrite<TComponent>();
            try
            {
                foreach (ref readonly FieldWrite<T> write in writes)
                    if (write.Channel == channel)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref Unsafe.As<TComponent, byte>(ref sparse.Get(write.Target)), channel.Offset), write.Value);
            }
            finally { sparse.Dispose(); }
            return;
        }
        var roots = world.OpenWrite<TComponent>();
        try
        {
            foreach (ref readonly FieldWrite<T> write in writes)
                if (write.Channel == channel)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref Unsafe.As<TComponent, byte>(ref roots.Get(write.Target)), channel.Offset), write.Value);
        }
        finally { roots.Dispose(); }
    }

    /// <summary>Computes a field location once; callers can keep the result in a static readonly field.</summary>
    public static Channel<T> Of<TComponent>(FieldRef<TComponent, T> field)
        where TComponent : unmanaged, IComponent
    {
        TComponent component = default;
        ref T value = ref field(ref component);
        nint offset = Unsafe.ByteOffset(ref Unsafe.As<TComponent, byte>(ref component), ref Unsafe.As<T, byte>(ref value));
        DevelopmentCheck.Require(offset >= 0 && offset <= ushort.MaxValue && offset <= Unsafe.SizeOf<TComponent>() - Unsafe.SizeOf<T>(),
            "A channel field must fit inside its component and its ushort byte offset.");
        return new Channel<T>(ComponentId<TComponent>.Value, (ushort)offset, &WriteOffsetFields<TComponent>);
    }

    public bool Equals(Channel<T> other) => Component == other.Component && Field == other.Field;
    public override bool Equals(object? obj) => obj is Channel<T> other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Component, Field);
    public static bool operator ==(Channel<T> left, Channel<T> right) => left.Equals(right);
    public static bool operator !=(Channel<T> left, Channel<T> right) => !left.Equals(right);
}

internal readonly unsafe struct FieldDescription<T>(
    delegate* managed<World, SomeEngine.ECS.Entities.Entity, T> read,
    delegate* managed<World, SomeEngine.ECS.Entities.Entity, in T, void> write,
    delegate* managed<ref byte, T> readRoot,
    delegate* managed<ref byte, in T, void> writeRoot,
    delegate* managed<World, Channel<T>, ReadOnlySpan<FieldWrite<T>>, void> writeBatch,
    bool throughReference) where T : unmanaged
{
    internal readonly delegate* managed<World, SomeEngine.ECS.Entities.Entity, T> Read = read;
    internal readonly delegate* managed<World, SomeEngine.ECS.Entities.Entity, in T, void> Write = write;
    internal readonly delegate* managed<ref byte, T> ReadRoot = readRoot;
    internal readonly delegate* managed<ref byte, in T, void> WriteRoot = writeRoot;
    internal readonly delegate* managed<World, Channel<T>, ReadOnlySpan<FieldWrite<T>>, void> WriteBatch = writeBatch;
    internal readonly bool ThroughReference = throughReference;
}

internal static class FieldDescriptions<T> where T : unmanaged
{
    internal static FieldDescription<T>[] Items = new FieldDescription<T>[16];
    private static (int Component, uint Field)[] _identities = new (int, uint)[16];
    private static int _count;
    private static readonly object Gate = new();

    internal static int Add(int component, uint field, FieldDescription<T> description)
    {
        lock (Gate)
        {
            for (int index = 0; index < _count; index++)
                if (_identities[index] == (component, field))
                    return index + 1;
            if (_count == Items.Length)
            {
                Array.Resize(ref Items, _count * 2);
                Array.Resize(ref _identities, _count * 2);
            }
            _identities[_count] = (component, field);
            Items[_count] = description;
            return ++_count;
        }
    }
}
