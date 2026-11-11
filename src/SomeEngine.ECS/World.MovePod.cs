using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveKnown(Entity entity, ref EntityRecord record, Chunk source, Transition edge)
    {
        Chunk target = edge.Target;
        if (target.Count == target.Capacity) target = edge.Target = target.EnsureRow();
        MoveRows(entity, ref record, source, target, edge.Pod, edge.Managed, edge.Removed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveKnown(Entity entity, ref EntityRecord record, Chunk source, Chunk target,
        PodColumn[] pod, ComponentType[] managed, ComponentType? removed)
        => MoveRows(entity, ref record, source, target.EnsureRow(), pod, managed, removed);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MoveRows(Entity entity, ref EntityRecord record, Chunk source, Chunk target,
        PodColumn[] pod, ComponentType[] managed, ComponentType? removed)
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        if ((source.Archetype.TransferKinds | target.Archetype.TransferKinds) == 0)
            MovePod(entity, ref record, source, target, pod, removed);
        else
            MoveWithFeatures(entity, ref record, source, target, pod, managed, removed);
        if (ChunkCapacity != 0) source.Archetype.MakeAvailable(source);
    }

    private void MovePod(Entity entity, ref EntityRecord record, Chunk source, Chunk target, PodColumn[] pod, ComponentType? removedType)
    {
        int sourceRow = record.Row;
        int targetRow = target.Count++;
        int last = --source.Count;
        target.Entities[targetRow] = entity;
        ref ColumnEntry sourceColumns = ref MemoryMarshal.GetArrayDataReference(source.Columns);
        ref ColumnEntry targetColumns = ref MemoryMarshal.GetArrayDataReference(target.Columns);
        if (sourceRow == last)
        {
            foreach (PodColumn description in pod)
                Chunk.CopyBytes(Unsafe.Add(ref sourceColumns, description.TypeId).Rows + (nint)sourceRow * description.Size,
                    Unsafe.Add(ref targetColumns, description.TypeId).Rows + (nint)targetRow * description.Size, description.Size);
        }
        else
        {
            foreach (PodColumn description in pod)
            {
                nint sourceBase = Unsafe.Add(ref sourceColumns, description.TypeId).Rows;
                nint sourceAddress = sourceBase + (nint)sourceRow * description.Size;
                Chunk.CopyBytes(sourceAddress,
                    Unsafe.Add(ref targetColumns, description.TypeId).Rows + (nint)targetRow * description.Size, description.Size);
                Chunk.CopyBytes(sourceBase + (nint)last * description.Size, sourceAddress, description.Size);
            }
            if (removedType is { IsTag: false, IsShared: false } removed)
            {
                nint values = Unsafe.Add(ref sourceColumns, removed.Id).Rows;
                Chunk.CopyBytes(values + (nint)last * removed.Size, values + (nint)sourceRow * removed.Size, removed.Size);
            }
            Entity moved = source.Entities[last];
            source.Entities[sourceRow] = moved;
            Records[moved.Id].Row = sourceRow;
        }
        record.Chunk = target;
        record.Row = targetRow;
    }
}
