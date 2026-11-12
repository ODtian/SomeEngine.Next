using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled<T>(Entity entity) where T : struct, IEnableableComponent
        => Execution is null ? IsEnabledCore<T>(entity) : IsEnabledWithExecution<T>(entity);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool IsEnabledWithExecution<T>(Entity entity) where T : struct, IEnableableComponent
    {
        using WorldAccess access = OpenStructure(false);
        return IsEnabledCore<T>(entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEnabledCore<T>(Entity entity) where T : struct, IEnableableComponent
    {
        ref EntityRecord record = ref Resolve(entity);
        Chunk chunk = record.Chunk!;
        int type = ComponentType<T>.TypeId;
        return (uint)type < (uint)chunk.Columns.Length && chunk.Columns[type].Has(record.Row)
            && chunk.IsEnabled(type, record.Row);
    }

    public void Disable<T>(Entity entity) where T : struct, IEnableableComponent => SetEnabled<T>(entity, false);
    public void Enable<T>(Entity entity) where T : struct, IEnableableComponent => SetEnabled<T>(entity, true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetEnabled<T>(Entity entity, bool enabled) where T : struct, IEnableableComponent
    {
        if (Execution is not null) SetEnabledWithExecution<T>(entity, enabled);
        else SetEnabledCore<T>(entity, enabled);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SetEnabledWithExecution<T>(Entity entity, bool enabled) where T : struct, IEnableableComponent
    {
        using WorldAccess access = OpenStructure();
        SetEnabledCore<T>(entity, enabled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetEnabledCore<T>(Entity entity, bool enabled) where T : struct, IEnableableComponent
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        Chunk chunk = record.Chunk!;
        int type = ComponentType<T>.TypeId;
        if ((uint)type >= (uint)chunk.Columns.Length || !chunk.Columns[type].Has(record.Row))
            throw new ArgumentException("Entity does not have this component.");
        if (!enabled)
        {
            int groupType = ComponentType<EnableGroup<T>>.TypeId;
            if ((uint)groupType >= (uint)chunk.Columns.Length || chunk.Columns[groupType].ColumnPlusOne == 0)
            {
                AddKnown(entity, ref record, new EnableGroup<T>());
                chunk = record.Chunk!;
            }
        }
        if (!ComponentType<T>.IsIndexedType && !ComponentType<T>.IsSharedType && !ComponentType<T>.TracksWritesType)
        {
            SetEnabled(chunk, type, record.Row, enabled);
            return;
        }
        if (chunk.IsEnabled(type, record.Row) != enabled)
        {
            if (ComponentType<T>.IsIndexedType && !enabled) ActiveIndex<T>()?.Remove(entity.Id);
            if (ComponentType<T>.IsSharedType && !enabled) RemoveSharedMember(entity, chunk, type, record.Row);
            SetEnabled(chunk, type, record.Row, enabled);
            if (ComponentType<T>.IsSharedType && enabled) CopySharedMember(entity, chunk, type, record.Row);
            if (ComponentType<T>.IsIndexedType && enabled) ActiveIndex<T>()?.Added(entity, Read<T>(entity));
            if (ComponentType<T>.TracksWritesType) Stamp<T>(chunk, record.Row, NextVersion());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetEnabled(Chunk chunk, int type, int row, bool enabled)
    {
        int column = chunk.Columns[type].ColumnPlusOne - 1;
        ulong[]? words = chunk.Enabled?[column];
        if (words is null)
        {
            if (enabled) return;
            words = CreateEnabledWords(chunk, type, column);
        }
        ulong bit = 1UL << (row & 63);
        bool before = (words[row >> 6] & bit) != 0;
        if (before == enabled) return;
        if (enabled) { words[row >> 6] |= bit; chunk.DisabledCounts![column]--; }
        else { words[row >> 6] &= ~bit; chunk.DisabledCounts![column]++; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong[] CreateEnabledWords(Chunk chunk, int type, int column)
    {
        chunk.Enabled ??= new ulong[]?[chunk.Archetype.Components.Length];
        chunk.DisabledCounts ??= new int[chunk.Archetype.Components.Length];
        var words = new ulong[(chunk.Capacity + 63) >> 6];
        words.AsSpan(0, chunk.Count >> 6).Fill(ulong.MaxValue);
        if ((chunk.Count & 63) != 0) words[chunk.Count >> 6] = (1UL << (chunk.Count & 63)) - 1;
        chunk.Enabled[column] = words;
        chunk.Archetype.Optional.Add(type);
        chunk.Archetype.TransferKinds |= 4;
        QueryVersion++;
        RefreshQueryMatches();
        return words;
    }

    private void TransferEnabled(Chunk source, int sourceRow, Chunk target, int targetRow, int last)
    {
        if (source.Enabled is not { } enabled) return;
        for (int i = 0; i < enabled.Length; i++)
        {
            if (enabled[i] is not { } words) continue;
            int type = source.Archetype.Components[i].Id;
            if (source.Columns[type].Has(sourceRow) && (words[sourceRow >> 6] & (1UL << (sourceRow & 63))) == 0
                && (uint)type < (uint)target.Columns.Length && target.Columns[type].ColumnPlusOne != 0)
                SetEnabled(target, type, targetRow, false);
        }
        source.MoveEnabled(last, sourceRow);
    }

    // A persistent storage group brings components that have used enable/disable together.
    // The component's enabled word remains the current state.
    internal struct EnableGroup<T> : ITag where T : struct;
}
