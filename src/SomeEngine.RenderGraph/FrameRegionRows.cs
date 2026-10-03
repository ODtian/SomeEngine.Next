namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private readonly record struct RegionPassPosition(GraphIdentity Region, int Ordinal);
    private readonly record struct PassAccessPosition(int Pass, int Ordinal);

    private readonly Dictionary<RegionPassPosition, int> _regionPassRows = [];
    private readonly Dictionary<PassAccessPosition, int> _frameAccessRows = [];
    private readonly HashSet<RegionPassPosition> _currentRegionPasses = [];
    private readonly HashSet<PassAccessPosition> _currentFrameAccesses = [];
    private readonly Stack<int> _freePassRows = new();
    private readonly Stack<int> _freeAccessRows = new();
    private readonly List<RegionPassPosition> _removedRegionPasses = [];
    private readonly List<PassAccessPosition> _removedFrameAccesses = [];
    private int[] _dynamicPassRows = [], _dynamicAccessRows = [];
    private RegionPassPosition[] _dynamicPassPositions = [];
    private readonly Dictionary<GraphIdentity, int> _regionPassCounts = [];
    private PassAccessPosition[] _dynamicAccessPositions = [];
    private readonly Dictionary<int, int> _dynamicAccessCounts = [];
    private int _accessRowCount;

    private void PreparePassRows()
    {
        GraphStructure structure = _frame.StructureIndex.Structure;
        foreach (GraphIdentity identity in _rowChanges.Passes)
        {
            if (structure.Passes.Contains(identity) || !_passIndices.Remove(identity, out int row)) continue;
            RemovePassRow(row);
        }

        _regionPassCounts.Clear();
        _currentRegionPasses.Clear();
        EnsureCapacity(ref _dynamicPassPositions, _frame.DynamicPasses.Count);
        for (int index = 0; index < _frame.DynamicPasses.Count; index++)
        {
            FramePass pass = _frame.DynamicPasses[index];
            int ordinal = _regionPassCounts.GetValueOrDefault(pass.Region);
            _regionPassCounts[pass.Region] = ordinal + 1;
            var position = new RegionPassPosition(pass.Region, ordinal);
            _dynamicPassPositions[index] = position;
            _currentRegionPasses.Add(position);
        }
        _removedRegionPasses.Clear();
        foreach (var pair in _regionPassRows)
            if (!_currentRegionPasses.Contains(pair.Key)) _removedRegionPasses.Add(pair.Key);
        foreach (RegionPassPosition position in _removedRegionPasses)
        {
            int row = _regionPassRows[position];
            _regionPassRows.Remove(position);
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

        _currentFrameAccesses.Clear();
        EnsureCapacity(ref _dynamicAccessPositions, _frame.DynamicAccesses.Count);
        _dynamicAccessCounts.Clear();
        for (int index = 0; index < _frame.DynamicAccesses.Count; index++)
        {
            int pass = ResolvePass(_frame.DynamicAccesses[index].Pass);
            int ordinal = _dynamicAccessCounts.GetValueOrDefault(pass);
            _dynamicAccessCounts[pass] = ordinal + 1;
            var position = new PassAccessPosition(pass, ordinal);
            _dynamicAccessPositions[index] = position;
            _currentFrameAccesses.Add(position);
        }
        _removedFrameAccesses.Clear();
        foreach (var pair in _frameAccessRows)
            if (!_currentFrameAccesses.Contains(pair.Key)) _removedFrameAccesses.Add(pair.Key);
        foreach (PassAccessPosition position in _removedFrameAccesses)
        {
            int row = _frameAccessRows[position];
            _frameAccessRows.Remove(position);
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
        int index = _accessRowCount++;
        if (index < _accesses.Count) DetachAccess(index);
        else _accesses.Add(default);
        return index;
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
                if (source.Contains(identity)) _rowIndexScratch.Add(source.DenseIndex(identity));
        return _rowIndexScratch;
    }
}
