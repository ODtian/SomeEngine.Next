using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    public TValue ReadField<TComponent, TValue, TAccess>(Entity entity)
        where TComponent : struct, IComponent where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
        => TAccess.Read(in ReadComponentReference<TComponent>(entity));

    public void WriteField<TComponent, TValue, TAccess>(Entity entity, in TValue value)
        where TComponent : struct, IComponent where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        using WorldAccess access = OpenValues([new(ComponentType<TComponent>.TypeId,
            ComponentType<TComponent>.IsSparseType ? WorldValueKind.Sparse : WorldValueKind.Table, true,
            ComponentType<TComponent>.TracksWritesType)]);
        if (ComponentType<TComponent>.IsSparseType) TAccess.Write(ref GetSparse<TComponent>(entity), in value);
        else TAccess.Write(ref Get<TComponent>(entity), in value);
    }

    public ref readonly byte ReadBytes(Entity entity, int component)
        => ref ComponentBytes(entity, component, 0, ComponentTypes.Get(component).Size, false);
    public ref readonly byte ReadBytes(Entity entity, int component, int offset, int length)
        => ref ComponentBytes(entity, component, offset, length, false);
    public ref byte WriteBytes(Entity entity, int component)
        => ref ComponentBytes(entity, component, 0, ComponentTypes.Get(component).Size, true);
    public ref byte WriteBytes(Entity entity, int component, int offset, int length)
        => ref ComponentBytes(entity, component, offset, length, true);

    private unsafe ref byte ComponentBytes(Entity entity, int component, int offset, int length, bool write)
    {
        ComponentType type = ComponentTypes.Get(component);
        DevelopmentCheck.Require(!type.ContainsReferences && !type.IsTag && !type.IsShared,
            "Component byte access requires a stored value without managed references.");
        DevelopmentCheck.Require(offset >= 0 && length >= 0 && offset <= type.Size && length <= type.Size - offset,
            "The byte region fits within the component value.");
        DevelopmentCheck.Require(!write || !type.UsesStructureWrites,
            "Component byte writes use values with direct field mutation.");
        using WorldAccess access = OpenValues([new(component,
            type.IsSparse ? WorldValueKind.Sparse : WorldValueKind.Table, write, type.TracksWrites)]);
        if (type.IsSparse)
        {
            SparseStore? store = _sparse is { } stores && (uint)component < (uint)stores.Types.Length ? stores.Types[component] : null;
            if (store is null || !store.Has(entity))
                throw new InvalidOperationException("Component byte access requires the entity to have the sparse component.");
            return ref Unsafe.Add(ref store.Bytes(entity, write && type.TracksWrites ? NextVersion() : 0), offset);
        }
        ref EntityRecord record = ref Resolve(entity);
        Chunk chunk = record.Chunk!;
        if ((uint)component >= (uint)chunk.Columns.Length || !chunk.Columns[component].Has(record.Row))
            throw new InvalidOperationException("Component byte access requires the entity to have the component.");
        ref ColumnEntry column = ref chunk.Columns[component];
        if (write)
        {
            RequireWriteRow(chunk, record.Row);
            if (type.TracksWrites)
                chunk.Changes![column.ColumnPlusOne - 1].Stamp(record.Row, NextVersion(), false, type.TracksAdded);
        }
        return ref Unsafe.AsRef<byte>((void*)(column.Rows + (nint)record.Row * type.Size + offset));
    }
}
