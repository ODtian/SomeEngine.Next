namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly record struct RegionPassPosition(GraphIdentity Region, int Ordinal);
    private readonly record struct PassAccessPosition(int Pass, int Ordinal);

    private readonly Dictionary<RegionPassPosition, int> _regionPassRows = [];
    private readonly Dictionary<PassAccessPosition, int> _frameAccessRows = [];
    private readonly Stack<int> _freePassRows = new();
    private readonly Stack<int> _freeAccessRows = new();
    private readonly List<RegionPassPosition> _removedRegionPasses = [];
    private readonly List<PassAccessPosition> _removedFrameAccesses = [];
    private int[] _dynamicPassRows = [], _dynamicAccessRows = [];
    private RegionPassPosition[] _dynamicPassPositions = [];
    private readonly Dictionary<GraphIdentity, int> _regionPassCounts = [];
    private PassAccessPosition[] _dynamicAccessPositions = [];
    private readonly Dictionary<int, int> _dynamicAccessCounts = [];

    private void PreparePassRows()
    {
        GraphStructure structure = _frame.StructureIndex.Structure;
        foreach (GraphIdentity identity in _rowChanges.Passes)
        {
            if (structure.Passes.Contains(identity) || !_passIndices.Remove(identity, out int row)) continue;
            RemovePassRow(row);
        }

        _regionPassCounts.Clear();
        EnsureCapacity(ref _dynamicPassPositions, _frame.DynamicPasses.Count);
        for (int index = 0; index < _frame.DynamicPasses.Count; index++)
        {
            FramePass pass = _frame.DynamicPasses[index];
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_regionPassCounts, pass.Region, out _);
            int ordinal = count++;
            var position = new RegionPassPosition(pass.Region, ordinal);
            _dynamicPassPositions[index] = position;
        }
        _removedRegionPasses.Clear();
        // Each region uses consecutive ordinals, so its count also defines membership.
        foreach (var pair in _regionPassRows)
            if (pair.Key.Ordinal >= _regionPassCounts.GetValueOrDefault(pair.Key.Region))
                _removedRegionPasses.Add(pair.Key);
        foreach (RegionPassPosition position in _removedRegionPasses)
        {
            _regionPassRows.Remove(position, out int row);
            RemovePassRow(row);
        }

        if (!_rowsReady)
            foreach (GraphIdentity identity in _frame.StructureIndex.PassIds) AddPersistentPassRow(identity);
        else
            foreach (GraphIdentity identity in _rowChanges.Passes)
                if (structure.Passes.Contains(identity)) AddPersistentPassRow(identity);

        EnsureCapacity(ref _dynamicPassRows, _frame.DynamicPasses.Count);
        for (int index = 0; index < _frame.DynamicPasses.Count; index++)
        {
            RegionPassPosition position = _dynamicPassPositions[index];
            if (!_regionPassRows.TryGetValue(position, out int row))
            {
                row = AddPassRow();
                _regionPassRows.Add(position, row);
            }
            _dynamicPassRows[index] = row;
        }
    }

    private void AddPersistentPassRow(GraphIdentity identity)
    {
        if (!_passIndices.ContainsKey(identity)) _passIndices.Add(identity, AddPassRow());
    }

    private int AddPassRow()
    {
        if (_freePassRows.TryPop(out int row)) return row;
        _passes.Add(default);
        return _passes.Count - 1;
    }

    private void RemovePassRow(int row)
    {
        SetPassRow(row, default);
        _freePassRows.Push(row);
    }

    private void PrepareAccessRows()
    {
        GraphStructure structure = _frame.StructureIndex.Structure;
        foreach (GraphIdentity identity in _rowChanges.Accesses)
            if (!structure.Accesses.Contains(identity) && _accessIndices.Remove(identity, out int row))
                RemoveAccessRow(row);

        EnsureCapacity(ref _dynamicAccessPositions, _frame.DynamicAccesses.Count);
        _dynamicAccessCounts.Clear();
        for (int index = 0; index < _frame.DynamicAccesses.Count; index++)
        {
            int pass = ResolvePass(_frame.DynamicAccesses[index].Pass);
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_dynamicAccessCounts, pass, out _);
            int ordinal = count++;
            var position = new PassAccessPosition(pass, ordinal);
            _dynamicAccessPositions[index] = position;
        }
        _removedFrameAccesses.Clear();
        // Access ordinals are consecutive within each pass.
        foreach (var pair in _frameAccessRows)
            if (pair.Key.Ordinal >= _dynamicAccessCounts.GetValueOrDefault(pair.Key.Pass))
                _removedFrameAccesses.Add(pair.Key);
        foreach (PassAccessPosition position in _removedFrameAccesses)
        {
            _frameAccessRows.Remove(position, out int row);
            RemoveAccessRow(row);
        }

        if (!_rowsReady)
            foreach (GraphIdentity identity in _frame.StructureIndex.AccessIds) AddPersistentAccessRow(identity);
        else
            foreach (GraphIdentity identity in _rowChanges.Accesses)
                if (structure.Accesses.Contains(identity)) AddPersistentAccessRow(identity);

        EnsureCapacity(ref _dynamicAccessRows, _frame.DynamicAccesses.Count);
        for (int index = 0; index < _frame.DynamicAccesses.Count; index++)
        {
            PassAccessPosition position = _dynamicAccessPositions[index];
            if (!_frameAccessRows.TryGetValue(position, out int row))
            {
                row = AddAccessRow();
                _frameAccessRows.Add(position, row);
            }
            _dynamicAccessRows[index] = row;
        }
    }

    private void AddPersistentAccessRow(GraphIdentity identity)
    {
        if (!_accessIndices.ContainsKey(identity)) _accessIndices.Add(identity, AddAccessRow());
    }

    private int AddAccessRow()
    {
        if (_freeAccessRows.TryPop(out int row)) return row;
        _accesses.Add(default);
        return _accesses.Count - 1;
    }

    private void RemoveAccessRow(int row)
    {
        DetachAccess(row);
        _accesses[row] = default;
        _freeAccessRows.Push(row);
    }

    private List<int> CollectChangedRows<T>(GraphRows<T> source, HashSet<GraphIdentity> changes)
    {
        _rowIndexScratch.Clear();
        if (!_rowsReady)
            for (int index = 0; index < source.Count; index++) _rowIndexScratch.Add(index);
        else
            foreach (GraphIdentity identity in changes)
                if (source.TryGetDenseIndex(identity, out int current)) _rowIndexScratch.Add(current);
        return _rowIndexScratch;
    }
}
