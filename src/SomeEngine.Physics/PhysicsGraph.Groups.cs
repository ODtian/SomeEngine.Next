using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

internal readonly struct PhysicsActivityIsland(Entity[] bodies, int first, int count, Entity[] references, int referenceFirst, int referenceCount)
{
    internal ReadOnlySpan<Entity> DynamicBodies => bodies.AsSpan(first, count);
    internal ReadOnlySpan<Entity> References => references.AsSpan(referenceFirst, referenceCount);
}

internal sealed partial class PhysicsGraphData
{
    private int[] _scratchBodies = [], _incidenceOffsets = [], _incidenceRows = [], _bodyColors = [], _constraintColors = [], _colorMarks = [];
    private Entity[] _activityBodies = [], _activityReferences = [];
    private readonly List<ReferenceUse> _referenceUses = [];
    private readonly record struct ReferenceUse(int Island, Entity Body) : IComparable<ReferenceUse>
    {
        public int CompareTo(ReferenceUse other)
        {
            int order = Island.CompareTo(other.Island);
            if (order == 0) order = Body.Id.CompareTo(other.Body.Id);
            return order == 0 ? Body.Generation.CompareTo(other.Body.Generation) : order;
        }
    }
    private int DynamicIndex(Entity entity)
    {
        if (!_columns.Contains(entity)) return -1;
        int index = _columns.IndexOf(entity);
        return index < _dynamic.Length && _dynamic[index] ? index : -1;
    }
    private ReadOnlySpan<int> TouchedBodies(int rowIndex)
    {
        var key = _rows[rowIndex]; ConstraintSet set = _sets[key.Set]; ConstraintRow row = set.Rows[key.Row];
        int capacity = row.BodyCount + row.ReferenceCount;
        if (_scratchBodies.Length < capacity) Array.Resize(ref _scratchBodies, capacity);
        int count = 0;
        foreach (var body in CollectionsMarshal.AsSpan(set.Bodies).Slice(row.BodyStart, row.BodyCount))
        { int index = DynamicIndex(body.Body); if (index >= 0) _scratchBodies[count++] = index; }
        foreach (Entity body in CollectionsMarshal.AsSpan(set.References).Slice(row.ReferenceStart, row.ReferenceCount))
        { int index = DynamicIndex(body); if (index >= 0) _scratchBodies[count++] = index; }
        return _scratchBodies.AsSpan(0, count);
    }
    private static int Root(int[] parents, int index)
    {
        while (parents[index] != index) { parents[index] = parents[parents[index]]; index = parents[index]; }
        return index;
    }
    private static void Join(int[] parents, ReadOnlySpan<int> bodies)
    {
        if (bodies.Length == 0) return;
        int first = Root(parents, bodies[0]);
        foreach (int body in bodies[1..])
        {
            int second = Root(parents, body);
            if (first == second) continue;
            int smaller = Math.Min(first, second); parents[Math.Max(first, second)] = smaller; first = smaller;
        }
    }
    private void BuildGroups()
    {
        int count = _columns.Count;
        _rows.Clear();
        for (int set = 0; set < _sets.Length; set++)
            for (int row = 0; row < _sets[set].Rows.Count; row++) if (_sets[set].Rows[row].Active) _rows.Add((set, row));
        if (_baseParents.Length != count) { Array.Resize(ref _baseParents, count); Array.Resize(ref _parents, count); _persistentDirty = true; }
        if (_persistentDirty)
        {
            for (int i = 0; i < count; i++) _baseParents[i] = i;
            for (int row = 0; row < _rows.Count; row++)
            { var key = _rows[row]; if (_sets[key.Set].Rows[key.Row].Identity.Entity != Entity.Null) Join(_baseParents, TouchedBodies(row)); }
        }
        _baseParents.CopyTo(_parents, 0);
        for (int row = 0; row < _rows.Count; row++)
        { var key = _rows[row]; if (_sets[key.Set].Rows[key.Row].Identity.Entity == Entity.Null) Join(_parents, TouchedBodies(row)); }
        BuildIslands();
        BuildIncidence();
        BuildActivityIslands();
        if (_settings.Order == PhysicsGraphOrder.Colors) { BuildBodyColors(); BuildConstraintColors(); }
        else BuildIslandRanges();
    }
    private void BuildIslands()
    {
        int count = _columns.Count;
        Array.Resize(ref _islandOfBody, count); Array.Fill(_islandOfBody, -1);
        var rootIslands = new int[count]; Array.Fill(rootIslands, -1);
        int islands = 0, dynamicCount = 0;
        for (int body = 0; body < count; body++)
            if (_dynamic[body])
            {
                int root = Root(_parents, body);
                if (rootIslands[root] < 0) rootIslands[root] = islands++;
                _islandOfBody[body] = rootIslands[root]; dynamicCount++;
            }
        Array.Resize(ref _islandStarts, islands + 1); Array.Clear(_islandStarts);
        foreach (int island in _islandOfBody) if (island >= 0) _islandStarts[island + 1]++;
        for (int i = 1; i < _islandStarts.Length; i++) _islandStarts[i] += _islandStarts[i - 1];
        Array.Resize(ref _islandBodies, dynamicCount);
        var next = (int[])_islandStarts.Clone();
        for (int body = 0; body < count; body++) if (_islandOfBody[body] >= 0) _islandBodies[next[_islandOfBody[body]]++] = body;
    }
    private void BuildIncidence()
    {
        int count = _columns.Count;
        Array.Resize(ref _incidenceOffsets, count + 1); Array.Clear(_incidenceOffsets);
        for (int row = 0; row < _rows.Count; row++) foreach (int body in TouchedBodies(row)) _incidenceOffsets[body + 1]++;
        for (int i = 1; i < _incidenceOffsets.Length; i++) _incidenceOffsets[i] += _incidenceOffsets[i - 1];
        Array.Resize(ref _incidenceRows, _incidenceOffsets[count]);
        var next = (int[])_incidenceOffsets.Clone();
        for (int row = 0; row < _rows.Count; row++) foreach (int body in TouchedBodies(row)) _incidenceRows[next[body]++] = row;
    }
    private void BuildActivityIslands()
    {
        int islands = _islandStarts.Length - 1;
        Array.Resize(ref _activityBodies, _islandBodies.Length);
        for (int i = 0; i < _islandBodies.Length; i++) _activityBodies[i] = _columns.EntityAt(_islandBodies[i]);
        _referenceUses.Clear();
        for (int row = 0; row < _rows.Count; row++)
        {
            ReadOnlySpan<int> touched = TouchedBodies(row); if (touched.Length == 0) continue;
            int island = _islandOfBody[touched[0]];
            var key = _rows[row]; ConstraintSet set = _sets[key.Set]; ConstraintRow value = set.Rows[key.Row];
            foreach (var body in CollectionsMarshal.AsSpan(set.Bodies).Slice(value.BodyStart, value.BodyCount))
                if (DynamicIndex(body.Body) < 0) _referenceUses.Add(new(island, body.Body));
        }
        _referenceUses.Sort();
        var starts = new int[islands + 1];
        int references = 0;
        for (int i = 0; i < _referenceUses.Count; i++)
            if (i == 0 || _referenceUses[i] != _referenceUses[i - 1]) { starts[_referenceUses[i].Island + 1]++; references++; }
        for (int i = 1; i < starts.Length; i++) starts[i] += starts[i - 1];
        Array.Resize(ref _activityReferences, references); int output = 0;
        for (int i = 0; i < _referenceUses.Count; i++)
            if (i == 0 || _referenceUses[i] != _referenceUses[i - 1]) _activityReferences[output++] = _referenceUses[i].Body;
        var result = new PhysicsActivityIsland[islands];
        for (int i = 0; i < islands; i++) result[i] = new(_activityBodies, _islandStarts[i], _islandStarts[i + 1] - _islandStarts[i],
            _activityReferences, starts[i], starts[i + 1] - starts[i]);
        ActivityIslands = result;
    }
    private void BuildIslandRanges()
    {
        _bodyOrder = _islandBodies;
        int islands = _islandStarts.Length - 1;
        var bodies = new PhysicsBodyRange[islands];
        for (int island = 0; island < islands; island++) bodies[island] = new(_islandStarts[island], _islandStarts[island + 1] - _islandStarts[island]);
        BodyGroups = islands == 0 ? [] : [new(bodies, bodies.Length)];
        var counts = new int[islands + 1];
        for (int row = 0; row < _rows.Count; row++) { var touched = TouchedBodies(row); if (touched.Length != 0) counts[_islandOfBody[touched[0]] + 1]++; }
        for (int i = 1; i < counts.Length; i++) counts[i] += counts[i - 1];
        Array.Resize(ref _constraintOrder, counts[^1]); var next = (int[])counts.Clone();
        for (int row = 0; row < _rows.Count; row++)
        { var touched = TouchedBodies(row); if (touched.Length != 0) { var key = _rows[row]; _constraintOrder[next[_islandOfBody[touched[0]]]++] = _sets[key.Set].Rows[key.Row].Identity; } }
        var ranges = new PhysicsConstraintRange[islands];
        for (int i = 0; i < islands; i++) ranges[i] = new(counts[i], counts[i + 1] - counts[i]);
        ProjectionGroups = islands == 0 ? [] : [new(ranges, ranges.Length)];
    }
    private void BuildBodyColors()
    {
        int count = _columns.Count;
        Array.Resize(ref _bodyColors, count); Array.Fill(_bodyColors, -1);
        Array.Resize(ref _colorMarks, Math.Max(count, _rows.Count) + 1); Array.Clear(_colorMarks);
        int colors = 0;
        for (int body = 0; body < count; body++)
        {
            if (!_dynamic[body]) continue;
            int stamp = body + 1;
            foreach (int row in _incidenceRows.AsSpan(_incidenceOffsets[body], _incidenceOffsets[body + 1] - _incidenceOffsets[body]))
                foreach (int other in TouchedBodies(row)) if (_bodyColors[other] >= 0) _colorMarks[_bodyColors[other]] = stamp;
            int color = 0; while (_colorMarks[color] == stamp) color++;
            _bodyColors[body] = color; colors = Math.Max(colors, color + 1);
        }
        var starts = new int[colors + 1];
        foreach (int color in _bodyColors) if (color >= 0) starts[color + 1]++;
        for (int i = 1; i < starts.Length; i++) starts[i] += starts[i - 1];
        Array.Resize(ref _bodyOrder, starts[^1]); var next = (int[])starts.Clone();
        for (int body = 0; body < count; body++) if (_bodyColors[body] >= 0) _bodyOrder[next[_bodyColors[body]]++] = body;
        var groups = new PhysicsBodyGroup[colors];
        int width = Math.Max(1, _settings.BodiesPerBatch);
        for (int color = 0; color < colors; color++)
        {
            int length = starts[color + 1] - starts[color]; var ranges = new PhysicsBodyRange[(length + width - 1) / width];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = new(starts[color] + i * width, Math.Min(width, length - i * width));
            groups[color] = new(ranges, ranges.Length);
        }
        BodyGroups = groups;
    }
    private void BuildConstraintColors()
    {
        Array.Resize(ref _constraintColors, _rows.Count); Array.Fill(_constraintColors, -1); Array.Clear(_colorMarks);
        int colors = 0;
        for (int row = 0; row < _rows.Count; row++)
        {
            ReadOnlySpan<int> bodies = TouchedBodies(row); if (bodies.Length == 0) continue;
            int stamp = row + 1;
            foreach (int body in bodies)
                foreach (int other in _incidenceRows.AsSpan(_incidenceOffsets[body], _incidenceOffsets[body + 1] - _incidenceOffsets[body]))
                    if (_constraintColors[other] >= 0) _colorMarks[_constraintColors[other]] = stamp;
            int color = 0; while (_colorMarks[color] == stamp) color++;
            _constraintColors[row] = color; colors = Math.Max(colors, color + 1);
        }
        var starts = new int[colors + 1]; foreach (int color in _constraintColors) if (color >= 0) starts[color + 1]++;
        for (int i = 1; i < starts.Length; i++) starts[i] += starts[i - 1];
        Array.Resize(ref _constraintOrder, starts[^1]); var next = (int[])starts.Clone();
        for (int row = 0; row < _rows.Count; row++)
            if (_constraintColors[row] >= 0) { var key = _rows[row]; _constraintOrder[next[_constraintColors[row]]++] = _sets[key.Set].Rows[key.Row].Identity; }
        var groups = new PhysicsConstraintGroup[colors]; int width = Math.Max(1, _settings.RowsPerBlock);
        for (int color = 0; color < colors; color++)
        {
            int length = starts[color + 1] - starts[color]; var ranges = new PhysicsConstraintRange[(length + width - 1) / width];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = new(starts[color] + i * width, Math.Min(width, length - i * width), true);
            groups[color] = new(ranges, ranges.Length);
        }
        ProjectionGroups = groups;
    }
}
