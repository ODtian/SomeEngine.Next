namespace SomeEngine.ECS.Entities;

/// <summary>Current-schema entity-slot restore state and final-record publication.</summary>
internal sealed partial class EntityStore
{
    private const int SerializationReservedSlot = int.MinValue;

    private int _serializationFreeListTail = -1;
    private int _serializationNextSlot;
    private bool _serializationRestoreActive;

    internal void BeginSerializationRestore(int slotCount)
    {
        if (slotCount < 0)
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        if (_serializationRestoreActive)
            throw new InvalidOperationException("A serialized entity-slot restore is already active.");

        _pages = CreatePages(Math.Max(64, slotCount + 1));
        MutableRecord(0).Generation = -1;
        _count = slotCount;
        _aliveCount = 0;
        _freeListHead = -1;
        _serializationFreeListTail = -1;
        _serializationNextSlot = 1;
        _serializationRestoreActive = true;
    }

    internal void AppendSerializationSlot(int index, int generation, bool isAlive)
    {
        if (!_serializationRestoreActive)
            throw new InvalidOperationException("No serialized entity-slot restore is active.");
        if (index != _serializationNextSlot || index > _count)
        {
            throw new InvalidOperationException(
                $"Expected serialized entity slot index {_serializationNextSlot}, found {index}.");
        }
        if (generation < 0)
            throw new InvalidOperationException($"Invalid serialized entity slot generation {generation}.");

        ref PersistentEntityRecord record = ref MutableRecord(index);
        record = default;
        record.Generation = generation;
        if (isAlive)
        {
            record.FreeListNext = SerializationReservedSlot;
        }
        else
        {
            record.FreeListNext = -1;
            if (_freeListHead == -1)
                _freeListHead = index;
            else
                MutableRecord(_serializationFreeListTail).FreeListNext = index;
            _serializationFreeListTail = index;
        }

        _serializationNextSlot++;
    }

    internal void CompleteSerializationRestore()
    {
        if (!_serializationRestoreActive)
            throw new InvalidOperationException("No serialized entity-slot restore is active.");
        if (_serializationNextSlot != _count + 1)
        {
            throw new InvalidOperationException(
                $"Serialized entity slots ended at {_serializationNextSlot - 1}; expected {_count}.");
        }

        _serializationRestoreActive = false;
        _serializationNextSlot = 0;
        _serializationFreeListTail = -1;
    }

    internal EntityRecordWriter AllocatePreserved(Entity id)
    {
        if (!IsAllocatedIndex(id.Index))
            throw new InvalidOperationException($"Cannot restore {id}: index is outside the prepared entity store.");

        ref PersistentEntityRecord record = ref MutableRecord(id.Index);
        if (record.ArchetypeIdentity != 0)
            throw new InvalidOperationException($"Cannot restore {id}: slot is already alive.");
        if (record.Generation != id.Generation)
        {
            throw new InvalidOperationException(
                $"Cannot restore {id}: serialized slot generation is {record.Generation}.");
        }
        if (record.FreeListNext != SerializationReservedSlot)
            throw new InvalidOperationException($"Cannot restore {id}: slot is not marked alive in serialized slots.");

        record = default;
        record.Generation = id.Generation;
        _aliveCount++;
        return new EntityRecordWriter(this, id.Index);
    }
}
