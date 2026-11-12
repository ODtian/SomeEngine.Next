using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Serialization;

internal static unsafe class ColumnData
{
    internal static void Copy(World values, ComponentType type, ref ColumnEntry source, ref ColumnEntry target, int count)
    {
        if (type.IsTag) return;
        if (type.IsShared)
            Unsafe.As<int[]>(source.Values!).AsSpan(0, count).CopyTo(Unsafe.As<int[]>(target.Values!));
        else if (type.HasValueOperations)
        {
            for (int row = 0; row < count; row++)
                if (source.Has(row)) type.Clone(values, ref source, row, ref target, row);
        }
        else if (type.ContainsReferences) Array.Copy(source.Values!, target.Values!, count);
        else new ReadOnlySpan<byte>((void*)source.Rows, count * type.Size).CopyTo(new Span<byte>((void*)target.Rows, count * type.Size));
    }

    internal static ComponentChanges CopyChanges(in ComponentChanges source, int count, bool added)
    {
        return source.Copy(count, added);
    }

    internal static void RestoreChanges(in ComponentChanges source, ref ComponentChanges target, int count)
    {
        source.CopyTo(ref target, count);
    }
}

internal sealed class ChunkData
{
    internal readonly TypeSet Types;
    internal readonly int[] Groups;
    internal readonly ComponentType[] Components;
    internal readonly ColumnEntry[] Columns;
    internal Entity[] Entities = [];
    internal ComponentChanges[]? Changes;
    private readonly ulong[]?[] _presence;
    private ulong[]?[]? _enabledWords, _enabled;
    private int[]? _disabled;
    private bool _hasEnabled;
    internal ulong[]?[]? Enabled => _hasEnabled ? _enabled : null;
    internal int[]? Disabled => _hasEnabled ? _disabled : null;
    internal int Count;

    internal ChunkData(World values, Chunk source)
    {
        Types = source.Archetype.Types;
        Groups = source.Archetype.GroupKeys;
        Components = source.Archetype.Components;
        Columns = new ColumnEntry[Components.Length];
        _presence = new ulong[]?[Components.Length];
        Capture(values, source);
    }

    internal void Capture(World values, Chunk source)
    {
        int count = source.Count;
        if (Entities.Length < count)
        {
            Entities = new Entity[count];
            for (int i = 0; i < Components.Length; i++) Columns[i] = Components[i].Allocate(count, i + 1);
        }
        source.Entities.AsSpan(0, count).CopyTo(Entities);
        _hasEnabled = source.Enabled is not null;
        if (source.Enabled is { } enabled)
        {
            _enabled ??= new ulong[]?[enabled.Length];
            _enabledWords ??= new ulong[]?[enabled.Length];
            _disabled ??= new int[enabled.Length];
            for (int i = 0; i < enabled.Length; i++)
            {
                if (enabled[i] is { } words)
                {
                    CopyWords(words, ref _enabledWords[i], (count + 63) >> 6);
                    _enabled[i] = _enabledWords[i];
                }
                else _enabled[i] = null;
            }
            source.DisabledCounts!.AsSpan().CopyTo(_disabled);
        }
        if (source.Changes is not null) Changes ??= new ComponentChanges[Components.Length];
        for (int i = 0; i < Components.Length; i++)
        {
            ComponentType type = Components[i];
            ref ColumnEntry column = ref source.Columns[type.Id];
            if (Columns[i].ColumnPlusOne == 0) Columns[i] = type.Allocate(Entities.Length, i + 1);
            Columns[i].GroupPlusOne = column.GroupPlusOne;
            if (column.Presence is { } presence)
            {
                CopyWords(presence, ref _presence[i], (count + 63) >> 6);
                Columns[i].Presence = _presence[i];
            }
            else Columns[i].Presence = null;
            ColumnData.Copy(values, type, ref column, ref Columns[i], count);
            if (type.TracksWrites)
            {
                ref ComponentChanges target = ref Changes![i];
                if (target.Rows is null || target.Rows.Length < count) target = new ComponentChanges(Entities.Length, type.TracksAdded);
                source.Changes![i].CopyTo(ref target, count);
            }
        }
        Count = count;
    }

    private static void CopyWords(ulong[] source, ref ulong[]? target, int count)
    {
        if (target is null || target.Length < count) target = new ulong[count];
        source.AsSpan(0, count).CopyTo(target);
    }

    internal void Release(World values, bool clearArrays = false)
    {
        for (int i = 0; i < Components.Length; i++)
        {
            if (Count != 0 && Components[i].HasValueOperations && !Components[i].IsShared)
                Components[i].ReleaseRows(values, ref Columns[i], 0, Count);
            if (Count != 0 && Components[i].ContainsReferences && !Components[i].IsShared
                && (!Components[i].HasColumnRelease || Columns[i].Presence is not null))
                Array.Clear(Columns[i].Values!, 0, Count);
        }
        Count = 0;
        if (clearArrays)
        {
            Entities = [];
            Array.Clear(Columns); Array.Clear(_presence);
            _enabled = _enabledWords = null; _disabled = null; _hasEnabled = false; Changes = null;
        }
    }
}
