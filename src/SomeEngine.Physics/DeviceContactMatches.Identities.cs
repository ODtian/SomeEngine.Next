using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceContactMatches
{
    private Entity[] _rowEntities = [];
    private int[] _entityRows = [], _rowReferences = [];
    private readonly List<int> _inputRows = [];
    private int _rowCount;
    internal int NewInputCount => _newInputs.Count;
    internal ReadOnlySpan<int> NewInputIndices => CollectionsMarshal.AsSpan(_newInputs);
    internal bool WarmStart => _settings.WarmStart;
    internal void ReadIdentityReferences() => _identityReferences = true;
    internal MappedBuffer MapNewReferences()
    {
        MappedBuffer mapping = _backend.Map(_changesReadback, MapType.Read,
            new((ulong)_newInputs.Count * 4, (ulong)_newInputs.Count * 16));
        mapping.Invalidate(mapping.Range);
        return mapping;
    }
    internal void ReserveIdentityRows(Span<int> rows) => _parameters.ReserveRows(rows);
    internal void RecordIdentityRow(int input, int row)
    {
        _rowCount = Math.Max(_rowCount, row + 1);
        if (!_identityReferences) WriteNewRow(_newInputs[input], row);
    }
    internal void StoreContactIdentity(int row, Entity point) => StoreIdentity(row, point);
    internal bool HasContactIdentity(int row, Entity point) => (uint)row < (uint)_rowEntities.Length && _rowEntities[row] == point;
    internal void AddIdentityBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        bindings.Add("ContactRemovedRows", [ResourceBinding.WritableBuffer(_removedRowsView)]);
        bindings.Add("ContactMatchingEntities", [ResourceBinding.WritableBuffer(_matchingEntitiesView)]);
        bindings.Add("DeviceContactGeometryRows", [ResourceBinding.WritableBuffer(_rowsView)]);
    }
    internal JobResourceAccess[] GetIdentityWriteAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler.GetResourceToken(_removedRows), 0, (long)_removedRows.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_matchingEntities), 0, (long)_matchingEntities.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_rows), 0, (long)_rows.Info.Size)];

    private int FindRow(Entity point)
    {
        if ((uint)point.Index >= (uint)_entityRows.Length) return -1;
        int row = _entityRows[point.Index] - 1;
        return row >= 0 && _rowEntities[row] == point ? row : -1;
    }

    private void StoreIdentity(int row, Entity point)
    {
        if (row >= _rowEntities.Length)
            Array.Resize(ref _rowEntities, Math.Max(row + 1, Math.Max(16, _rowEntities.Length * 3 / 2)));
        if (point.Index >= _entityRows.Length)
            Array.Resize(ref _entityRows, Math.Max(point.Index + 1, Math.Max(16, _entityRows.Length * 3 / 2)));
        _rowEntities[row] = point;
        _entityRows[point.Index] = row + 1;
        _rowCount = Math.Max(_rowCount, row + 1);
        _parameters.RetainRow(row);
        if (_identityReferences) return;
        MemoryMarshal.Write(_identityInputs.Row(row), in point);
        _identityRanges.Add(new((ulong)row * 8, 8));
    }

    private void ReleaseRow(int row)
    {
        RemoveIdentity(row);
        _parameters.ReleaseRow(row);
    }

    private void RemoveIdentity(int row)
    {
        Entity point = _rowEntities[row];
        _entityRows[point.Index] = 0;
        _rowEntities[row] = Entity.Null;
        if (_identityReferences) return;
        _identityInputs.Row(row).Clear();
        _identityRanges.Add(new((ulong)row * 8, 8));
    }
}
