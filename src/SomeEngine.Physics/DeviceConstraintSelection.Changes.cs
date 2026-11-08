using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public partial class DeviceConstraintSelection<TComponent, TData, TSelection>
    where TComponent : struct, IComponent where TData : unmanaged
    where TSelection : struct, IDeviceConstraintRowSelection
{
    private readonly QueryKey _sourceKey;
    private DeviceContactMatches? _contactChanges;
    private long _contactChangesNumber;
    private readonly List<int> _denseIndices = [];
    private int[] _rowPositions = [];

    /// <summary>Uses the matching contact pool's membership changes while retaining this selection's component conditions.</summary>
    public void ReadContactChanges(DeviceContactMatches changes)
    {
        _contactChanges = changes;
        _hasCapturedMembers = false;
    }

    private bool CaptureContactChanges(uint baseline, uint membership)
    {
        if (_identities is null || !_hasCapturedMembers || _window.UsesVersions
            || _contactChanges is null || _contactChangesNumber + 1 != _contactChanges.ChangesNumber
            || !_contactChanges.ProvidesLifecycleChanges(_world, _sourceKey, _data)) return false;
        foreach (int index in _contactChanges.RemovedParameterRows) RemoveRow(index);
        var read = new ReadChangedRows(this);
        _world.ExecuteReadQuery(_rows, baseline, ref read,
            static (scoped QueryCursor rows, ref ReadChangedRows input) => input.Read(rows));
        Count = _denseIndices.Count;
        MemoryMarshal.Write(_indices!.Row(0), (uint)Count);
        SubmitPagedInputs();
        _membershipVersion = membership; _window.Accept(read.Version);
        _contactChangesNumber = _contactChanges.ChangesNumber;
        return true;
    }

    private void RecordRow(int index)
    {
        if (_rowPositions.Length <= index)
            Array.Resize(ref _rowPositions, Math.Max(index + 1, Math.Max(16, _rowPositions.Length * 2)));
        _rowPositions[index] = _denseIndices.Count + 1;
        _denseIndices.Add(index);
    }

    private void RemoveRow(int index)
    {
        if ((uint)index >= (uint)_rowPositions.Length || _rowPositions[index] == 0) return;
        int position = _rowPositions[index] - 1;
        int last = _denseIndices.Count - 1;
        if (position != last)
        {
            int moved = _denseIndices[last];
            _denseIndices[position] = moved; _rowPositions[moved] = position + 1;
            MemoryMarshal.Write(_indices!.Row(position + 1), (uint)moved);
        }
        _denseIndices.RemoveAt(last); _rowPositions[index] = 0;
        _identities!.Row(index).Clear();
    }

    private void ReadRow(scoped QueryRow row)
    {
        if (!row.TryRead<DeviceConstraintBinding<TData>>(out var binding) || !ReferenceEquals(binding.Data, _data)) return;
        int index = binding.Index;
        if (!row.Has<TComponent>() || !_selection.Matches(row)) { RemoveRow(index); return; }
        if ((uint)index < (uint)_rowPositions.Length && _rowPositions[index] != 0) return;
        RecordRow(index);
        MemoryMarshal.Write(_indices!.Row(_denseIndices.Count), (uint)index);
        MemoryMarshal.Write(_identities!.Row(index), row.Entity);
    }

    private ref struct ReadChangedRows(DeviceConstraintSelection<TComponent, TData, TSelection> selection)
    {
        internal uint Version;
        internal void Read(scoped QueryCursor rows)
        {
            Version = rows.CurrentSystemVersion;
            foreach (QueryChunkView chunk in rows.Chunks)
                if (chunk.Has<DeviceConstraintBinding<TData>>()
                    && (!chunk.Has<TComponent>() || chunk.HasChangedSinceLastSystemVersion<TComponent>()
                        || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<TData>>() || selection._selection.HasChangedInputs(chunk)))
                    foreach (QueryRow row in chunk.Rows) selection.ReadRow(row);
        }
    }

    private void SubmitPagedInputs()
    {
        _backend.Begin(_commands!, new(0, 0, 8));
        foreach (Buffer buffer in new[] { _entities, _denseRows! })
            _backend.Barrier(_commands!, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
        _identities!.CopyWrittenPages(_commands!, _entities);
        _indices!.CopyWrittenPages(_commands!, _denseRows!);
        foreach (Buffer buffer in new[] { _entities, _denseRows! })
            _backend.Barrier(_commands!, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        using RecordedCommands recorded = _backend.End(_commands!);
        _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
}
