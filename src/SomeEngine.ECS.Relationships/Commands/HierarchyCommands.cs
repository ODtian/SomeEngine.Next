using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Commands;

public static class HierarchyCommandsExtensions
{
    public static void SetHierarchyOrder<TDomain>(this CommandBuffer commands, CommandEntity parent, bool ordered)
        where TDomain : struct, IHierarchyDomain
        => Record<TDomain>(commands, HierarchyCommandKind.Order, parent, ordered ? 1 : 0);

    public static void ReorderChild<TDomain>(this CommandBuffer commands, CommandEntity child, int insertAt)
        where TDomain : struct, IHierarchyDomain
        => Record<TDomain>(commands, HierarchyCommandKind.Reorder, child, insertAt);

    public static void DestroySubtree<TDomain>(this CommandBuffer commands, CommandEntity root)
        where TDomain : struct, IHierarchyDomain
        => Record<TDomain>(commands, HierarchyCommandKind.Destroy, root, 0);

    private static void Record<TDomain>(CommandBuffer commands, HierarchyCommandKind kind,
        CommandEntity entity, int value) where TDomain : struct, IHierarchyDomain
    {
        commands.RequireRecording();
        var bucket = commands.Bucket<HierarchyCommands<TDomain>>(out int type);
        if (bucket.Count == 0) commands.Use(type);
        int index = bucket.Add(kind, commands.Encode(entity), value);
        commands.Append(CommandKind.Composite, default, type, index);
    }
}

internal enum HierarchyCommandKind : byte { Order, Reorder, Destroy }

internal sealed class HierarchyCommands<TDomain> : CommandBucket where TDomain : struct, IHierarchyDomain
{
    private readonly record struct Row(HierarchyCommandKind Kind, Entity Entity, int Value);
    private Row[] _rows = [];
    internal int Count;
    public HierarchyCommands() { }
    internal int Add(HierarchyCommandKind kind, Entity entity, int value)
    {
        if (Count == _rows.Length) Array.Resize(ref _rows, Math.Max(8, Count * 2));
        _rows[Count] = new(kind, entity, value);
        return Count++;
    }
    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        Row row = _rows[command.DataIndex];
        Entity entity = row.Entity.Id >= 0 ? row.Entity : results!.Decode(row.Entity);
        switch (row.Kind)
        {
            case HierarchyCommandKind.Order: Hierarchy<TDomain>.SetOrdered(world, entity, row.Value != 0); break;
            case HierarchyCommandKind.Reorder:
                Hierarchy<TDomain>.SetParent(world, entity, Hierarchy<TDomain>.GetParent(world, entity), row.Value);
                break;
            case HierarchyCommandKind.Destroy: Hierarchy<TDomain>.DestroySubtree(world, entity); break;
        }
        return default;
    }
    internal override void Clear(World world) => Count = 0;
}
