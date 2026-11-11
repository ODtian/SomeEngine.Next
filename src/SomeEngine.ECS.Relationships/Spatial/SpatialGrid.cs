using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Spatial;

public readonly record struct GridPairRange(int Start, int Count);

/// <summary>Entity bounds indexed by occupied cells, with reusable storage for member and cell changes.</summary>
public sealed partial class SpatialGrid<TSettings>(TSettings settings)
    where TSettings : struct, ISpatialGridSettings
{
    private readonly Dictionary<Entity, GridMember> _members = [];
    private readonly List<Entity> _entities = [];
    private readonly Dictionary<GridCell, List<Entity>> _cells = [];
    private readonly Stack<List<Entity>> _emptyCells = [];
    private int _update;

    public TSettings Settings => settings;

    /// <summary>Declares the grid's member and derived-cell update through the existing object resource table.</summary>
    public JobResourceAccess GetWriteAccess(Scheduler scheduler) => JobResourceAccess.Write(scheduler, _members);

    public void BeginUpdate() => _update++;

    public void Update(Entity entity, in SpatialBounds bounds)
    {
        ref GridMember member = ref CollectionsMarshal.GetValueRefOrAddDefault(_members, entity, out bool exists);
        GridCells cells = bounds.IsFinite ? GetCells(in bounds, settings.GetLevel(in bounds)) : default;
        if (!exists)
        {
            _entities.Add(entity);
            if (bounds.IsFinite) AddCells(entity, cells);
        }
        else if (member.Bounds.IsFinite != bounds.IsFinite || member.Cells != cells)
        {
            if (member.Bounds.IsFinite) RemoveCells(entity, member.Cells);
            if (bounds.IsFinite) AddCells(entity, cells);
        }
        member = new(bounds, cells, _update);
    }

    public void EndUpdate()
    {
        for (int index = _entities.Count - 1; index >= 0; index--)
        {
            Entity entity = _entities[index];
            GridMember member = _members[entity];
            if (member.SeenUpdate == _update)
                continue;
            if (member.Bounds.IsFinite) RemoveCells(entity, member.Cells);
            _members.Remove(entity);
            _entities[index] = _entities[^1];
            _entities.RemoveAt(_entities.Count - 1);
        }
    }

    internal int CreatePartitions(Span<GridPairRange> partitions)
    {
        int count = Math.Min(partitions.Length, _entities.Count);
        for (int index = 0; index < count; index++)
        {
            int start = (int)((long)index * _entities.Count / count);
            int end = (int)((long)(index + 1) * _entities.Count / count);
            partitions[index] = new(start, end - start);
        }
        return count;
    }

    internal bool Enumerate<TPairing, TOutput>(GridPairRange partition, scoped RelationQueryViews queries,
        ref TOutput output)
        where TPairing : struct, ISpatialPairing
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        foreach (Entity entity in CollectionsMarshal.AsSpan(_entities).Slice(partition.Start, partition.Count))
        {
            GridMember first = _members[entity];
            if (TPairing.IncludesSelf)
            {
                var self = (entity, entity);
                if (!TPairing.Emit(in self, queries, ref output))
                    return false;
            }
            if (!first.Bounds.IsFinite)
            {
                foreach (Entity other in CollectionsMarshal.AsSpan(_entities))
                {
                    GridMember second = _members[other];
                    if (entity == other || !second.Bounds.IsFinite && !EntityOrder.Precedes(entity, other)
                        || !first.Bounds.Overlaps(in second.Bounds)) continue;
                    var pair = EntityOrder.OrderPair(entity, other);
                    if (!TPairing.Emit(in pair, queries, ref output)) return false;
                }
                continue;
            }
            for (int level = first.Cells.Min.Level; level < settings.LevelCount; level++)
            {
                GridCells range = GetCells(in first.Bounds, level);
                for (int z = range.Min.Z; z <= range.Max.Z; z++)
                for (int y = range.Min.Y; y <= range.Max.Y; y++)
                for (int x = range.Min.X; x <= range.Max.X; x++)
                {
                    var cell = new GridCell(x, y, z, level);
                    if (!_cells.TryGetValue(cell, out List<Entity>? members))
                        continue;
                    foreach (Entity other in CollectionsMarshal.AsSpan(members))
                    {
                        if (entity == other || level == first.Cells.Min.Level && !EntityOrder.Precedes(entity, other))
                            continue;
                        GridMember second = _members[other];
                        if (x != Math.Max(range.Min.X, second.Cells.Min.X) ||
                            y != Math.Max(range.Min.Y, second.Cells.Min.Y) ||
                            z != Math.Max(range.Min.Z, second.Cells.Min.Z) ||
                            !first.Bounds.Overlaps(in second.Bounds))
                            continue;
                        var pair = EntityOrder.OrderPair(entity, other);
                        if (!TPairing.Emit(in pair, queries, ref output))
                            return false;
                    }
                }
            }
        }
        return true;
    }

    internal GridPairRange AllMembers => new(0, _entities.Count);

    private GridCells GetCells(in SpatialBounds bounds, int level)
    {
        float size = settings.GetCellSize(level);
        return new(new((int)MathF.Floor(bounds.Min.X / size), (int)MathF.Floor(bounds.Min.Y / size),
                (int)MathF.Floor(bounds.Min.Z / size), level),
            new((int)MathF.Floor(bounds.Max.X / size), (int)MathF.Floor(bounds.Max.Y / size),
                (int)MathF.Floor(bounds.Max.Z / size), level));
    }

    private void AddCells(Entity entity, GridCells range)
    {
        for (int z = range.Min.Z; z <= range.Max.Z; z++)
        for (int y = range.Min.Y; y <= range.Max.Y; y++)
        for (int x = range.Min.X; x <= range.Max.X; x++)
        {
            var cell = new GridCell(x, y, z, range.Min.Level);
            if (!_cells.TryGetValue(cell, out List<Entity>? members))
            {
                members = _emptyCells.TryPop(out List<Entity>? reused) ? reused : [];
                _cells.Add(cell, members);
            }
            members.Add(entity);
        }
    }

    private void RemoveCells(Entity entity, GridCells range)
    {
        for (int z = range.Min.Z; z <= range.Max.Z; z++)
        for (int y = range.Min.Y; y <= range.Max.Y; y++)
        for (int x = range.Min.X; x <= range.Max.X; x++)
        {
            var cell = new GridCell(x, y, z, range.Min.Level);
            List<Entity> members = _cells[cell];
            members.Remove(entity);
            if (members.Count == 0)
            {
                _cells.Remove(cell);
                _emptyCells.Push(members);
            }
        }
    }

    private readonly record struct GridCell(int X, int Y, int Z, int Level);
    private readonly record struct GridCells(GridCell Min, GridCell Max);
    private readonly struct GridMember(SpatialBounds bounds, GridCells cells, int seenUpdate)
    {
        internal readonly SpatialBounds Bounds = bounds;
        internal readonly GridCells Cells = cells;
        internal readonly int SeenUpdate = seenUpdate;
    }
}
