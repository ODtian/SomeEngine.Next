namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    // Declarations use consecutive ordinals within each region and pass.
    private sealed class RegionPassRows
    {
        internal readonly List<int> Rows = [];
        internal int PassCount;
        internal GraphRegionEntry Interval;
        internal bool Enabled;
    }

    private readonly record struct RegionPassPosition(RegionPassRows Region, int Ordinal);
    private readonly record struct PassAccessPosition(int Pass, int Ordinal);

    private readonly Dictionary<GraphIdentity, RegionPassRows> _regionPassRows = [];
    private List<int>[] _frameAccessRows = [];
    private int[] _frameAccessCounts = [];
    private readonly List<int> _frameAccessPasses = [];
    private readonly Stack<int> _freePassRows = new();
    private readonly Stack<int> _freeAccessRows = new();
    private readonly List<GraphIdentity> _removedRegions = [];
    private int[] _dynamicPassRows = [], _dynamicAccessRows = [];
    private RegionPassPosition[] _dynamicPassPositions = [];
    private PassAccessPosition[] _dynamicAccessPositions = [];

    private void PreparePassRows()
    {
        GraphStructure structure = _frame.StructureIndex.Structure;
        foreach (GraphIdentity identity in _rowChanges.Passes)
        {
            if (structure.Passes.Contains(identity) || !_passIndices.Remove(identity, out int row)) continue;
            RemovePassRow(row);
        }

        foreach (RegionPassRows region in _regionPassRows.Values) region.PassCount = 0;
        EnsureCapacity(ref _dynamicPassPositions, _frame.DynamicPasses.Count);
        for (int index = 0; index < _frame.DynamicPasses.Count; index++)
        {
            FramePass pass = _frame.DynamicPasses[index];
            ref RegionPassRows? region = ref CollectionsMarshal.GetValueRefOrAddDefault(_regionPassRows, pass.Region, out _);
            region ??= new();
            if (region.PassCount == 0)
            {
                region.Interval = structure.GetRegionInterval(pass.Region);
                region.Enabled = structure.IsRegionEnabled(pass.Region);
            }
            _dynamicPassPositions[index] = new RegionPassPosition(region, region.PassCount++);
        }
        _removedRegions.Clear();
        foreach (var pair in _regionPassRows)
        {
            RegionPassRows region = pair.Value;
            int removed = region.Rows.Count - region.PassCount;
            if (removed <= 0) continue;
            for (int item = region.PassCount; item < region.Rows.Count; item++) RemovePassRow(region.Rows[item]);
            region.Rows.RemoveRange(region.PassCount, removed);
            if (region.PassCount == 0) _removedRegions.Add(pair.Key);
        }
        foreach (GraphIdentity region in _removedRegions) _regionPassRows.Remove(region);

        if (!_rowsReady)
            foreach (GraphIdentity identity in _frame.StructureIndex.PassIds) AddPersistentPassRow(identity);
        else
            foreach (GraphIdentity identity in _rowChanges.Passes)
                if (structure.Passes.Contains(identity)) AddPersistentPassRow(identity);

        EnsureCapacity(ref _dynamicPassRows, _frame.DynamicPasses.Count);
        for (int index = 0; index < _frame.DynamicPasses.Count; index++)
        {
            RegionPassPosition position = _dynamicPassPositions[index];
            List<int> rows = position.Region.Rows;
            if (position.Ordinal == rows.Count) rows.Add(AddPassRow());
            _dynamicPassRows[index] = rows[position.Ordinal];
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
        EnsureCapacity(ref _frameAccessRows, _passes.Count);
        EnsureCapacity(ref _frameAccessCounts, _passes.Count);
        foreach (int pass in _frameAccessPasses) _frameAccessCounts[pass] = 0;
        for (int index = 0; index < _frame.DynamicAccesses.Count; index++)
        {
            int pass = ResolvePass(_frame.DynamicAccesses[index].Pass);
            _dynamicAccessPositions[index] = new PassAccessPosition(pass, _frameAccessCounts[pass]++);
        }
        for (int index = _frameAccessPasses.Count - 1; index >= 0; index--)
        {
            int pass = _frameAccessPasses[index];
            List<int> rows = _frameAccessRows[pass];
            int count = _frameAccessCounts[pass];
            int removed = rows.Count - count;
            if (removed <= 0) continue;
            for (int item = count; item < rows.Count; item++) RemoveAccessRow(rows[item]);
            rows.RemoveRange(count, removed);
            if (count == 0) _frameAccessPasses.RemoveAt(index);
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
            List<int> rows = _frameAccessRows[position.Pass] ??= [];
            if (position.Ordinal == rows.Count)
            {
                if (rows.Count == 0) _frameAccessPasses.Add(position.Pass);
                rows.Add(AddAccessRow());
            }
            _dynamicAccessRows[index] = rows[position.Ordinal];
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
