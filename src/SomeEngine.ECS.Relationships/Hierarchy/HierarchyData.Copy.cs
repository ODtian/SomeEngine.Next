using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Hierarchy;

internal sealed partial class HierarchyData<TDomain> : IWorldDataService
{
    void IWorldDataService.CompleteData() => Maintain();
    WorldServiceData IWorldDataService.CopyData(bool preserveProgress, WorldServiceData? target)
    {
        var data = target as SavedData ?? new SavedData();
        data.Capture(this, preserveProgress);
        return data;
    }

    internal DataView ReadData(bool preserve) => new(this, preserve);

    internal readonly ref struct DataView
    {
        internal readonly bool Ordered, Deferred;
        internal readonly ulong Version, ReaderPosition;
        internal readonly ReadOnlySpan<AppliedParent> Applied;
        internal readonly Dictionary<Entity, AppliedParent>? Retired;
        internal readonly Dictionary<Entity, Children<TDomain>>? RetiredChildren;
        internal readonly Dictionary<Entity, List<Entity>>? Future;
        internal readonly HashSet<Entity> EmptyOrdered;

        internal DataView(HierarchyData<TDomain> source, bool preserve)
        {
            Ordered = source._usesChildOrder; Deferred = preserve && source._hasDeferredChanges;
            Version = preserve ? source._version : 0; ReaderPosition = preserve ? source._reader.Position : 0;
            Applied = preserve ? source._applied.AsSpan(0, Math.Min(source._applied.Length, source._world.Used + 1)) : [];
            Retired = preserve ? source._retiredApplied : null;
            RetiredChildren = preserve ? source._retiredChildren : null;
            Future = preserve ? source._projected : null;
            EmptyOrdered = source._emptyOrdered;
        }
    }

    internal sealed class SavedData : WorldServiceData
    {
        internal bool Preserve, Deferred, Ordered;
        internal ulong Version, ReaderPosition;
        internal AppliedParent[] Applied = [];
        internal KeyValuePair<Entity, AppliedParent>[] Retired = [];
        internal KeyValuePair<Entity, Children<TDomain>>[] RetiredChildren = [];
        internal KeyValuePair<Entity, List<Entity>>[] Future = [];
        internal Entity[] EmptyOrdered = [];
        internal int AppliedCount, RetiredCount, RetiredChildCount, FutureCount, EmptyOrderedCount;

        internal SavedData() { }
        internal void Capture(HierarchyData<TDomain> source, bool preserve)
        {
            Preserve = preserve;
            Ordered = source._usesChildOrder;
            Deferred = preserve && source._hasDeferredChanges;
            Version = preserve ? source._version : 0;
            ReaderPosition = preserve ? source._reader.Position : 0;
            EmptyOrderedCount = source._emptyOrdered.Count;
            if (EmptyOrdered.Length < EmptyOrderedCount) EmptyOrdered = new Entity[EmptyOrderedCount];
            source._emptyOrdered.CopyTo(EmptyOrdered);
            if (!preserve) return;
            AppliedCount = Math.Min(source._applied.Length, source._world.Used + 1);
            if (Applied.Length < AppliedCount) Applied = new AppliedParent[AppliedCount];
            source._applied.AsSpan(0, AppliedCount).CopyTo(Applied);
            RetiredCount = source._retiredApplied.Count;
            if (Retired.Length < RetiredCount) Retired = new KeyValuePair<Entity, AppliedParent>[RetiredCount];
            int index = 0;
            foreach (var pair in source._retiredApplied) Retired[index++] = pair;
            RetiredChildCount = source._retiredChildren.Count;
            if (RetiredChildren.Length < RetiredChildCount) Array.Resize(ref RetiredChildren, RetiredChildCount);
            index = 0;
            foreach (var pair in source._retiredChildren)
            {
                Children<TDomain> previous = RetiredChildren[index].Value;
                Entity[]? items = previous.Items;
                if (items is null || items.Length < pair.Value.Count)
                {
                    Children<TDomain>.Release(ref previous);
                    items = pair.Value.Count == 0 ? null : Children<TDomain>.Rent(pair.Value.Count);
                }
                pair.Value.Entities.CopyTo(items);
                RetiredChildren[index++] = new(pair.Key, new(items, pair.Value.Count, pair.Value.IsOrdered));
            }
            FutureCount = source._projected.Count;
            if (Future.Length < FutureCount) Array.Resize(ref Future, FutureCount);
            index = 0;
            foreach (var pair in source._projected)
            {
                List<Entity> members = Future[index].Value ?? new();
                members.Clear(); members.AddRange(pair.Value);
                Future[index++] = new(pair.Key, members);
            }
        }

        internal override void Restore(World world) => Restore(world, false);

        internal void Restore(World world, bool transfer)
        {
            HierarchyData<TDomain>? target = world.FindLifecycle<HierarchyData<TDomain>>();
            if (target is null) { target = new(world); world.AddLifecycle(target); }
            target.EnsureCapacity();
            if (Preserve)
            {
                if (transfer) target._applied = Applied;
                else Applied.AsSpan(0, AppliedCount).CopyTo(target._applied);
                foreach (var pair in Retired.AsSpan(0, RetiredCount)) target._retiredApplied.Add(pair.Key, pair.Value);
                foreach (var pair in RetiredChildren.AsSpan(0, RetiredChildCount)) target._retiredChildren.Add(pair.Key, transfer ? pair.Value : Children<TDomain>.Copy(pair.Value));
                if (transfer) { RetiredChildren = []; RetiredChildCount = 0; }
                foreach (var pair in Future.AsSpan(0, FutureCount)) target._projected.Add(pair.Key, target.CopyChildOrder(CollectionsMarshal.AsSpan(pair.Value)));
                int type = ComponentType<Parent<TDomain>>.TypeId;
                for (int i = 0; i < world.ArchetypeCount; i++)
                    foreach (Chunk chunk in world.Archetypes[i].AllChunks)
                    {
                        if ((uint)type >= (uint)chunk.Columns.Length || chunk.Columns[type].ColumnPlusOne == 0) continue;
                        for (int row = 0; row < chunk.Count; row++)
                            if (chunk.Columns[type].Has(row))
                            {
                                Entity child = chunk.Entities[row];
                                Entity parent = ColumnEntry.At<Parent<TDomain>>(ref chunk.Columns[type], row).Value;
                                if (parent != target.Applied(child).Parent) target._differences.Set(child, parent);
                            }
                    }
            }
            else
            {
                int type = ComponentType<Children<TDomain>>.TypeId;
                for (int i = 0; i < world.ArchetypeCount; i++)
                    foreach (Chunk chunk in world.Archetypes[i].AllChunks)
                    {
                        if ((uint)type >= (uint)chunk.Columns.Length || chunk.Columns[type].ColumnPlusOne == 0) continue;
                        for (int row = 0; row < chunk.Count; row++)
                            if (chunk.Columns[type].Has(row))
                            {
                                ReadOnlySpan<Entity> children = ColumnEntry.At<Children<TDomain>>(ref chunk.Columns[type], row).Entities;
                                for (int place = 0; place < children.Length; place++)
                                {
                                    Entity child = children[place];
                                    target._applied[child.Id] = new() { Parent = chunk.Entities[row], Generation = child.Generation, Place = place };
                                }
                            }
                    }
            }
            foreach (Entity entity in EmptyOrdered.AsSpan(0, EmptyOrderedCount)) target._emptyOrdered.Add(entity);
            target._usesChildOrder = Ordered;
            target._hasDeferredChanges = Preserve && Deferred;
            target._version = Preserve ? Version : world.Version;
            target._reader.Reset(Preserve ? ReaderPosition : world.DestroyedSequence);
        }

        internal override void Clear(bool keepCapacity)
        {
            if (!keepCapacity)
                foreach (var pair in RetiredChildren) { Children<TDomain> value = pair.Value; Children<TDomain>.Release(ref value); }
            for (int i = 0; i < FutureCount; i++) Future[i].Value.Clear();
            AppliedCount = RetiredCount = RetiredChildCount = FutureCount = EmptyOrderedCount = 0;
            if (!keepCapacity) { Applied = []; Retired = []; RetiredChildren = []; Future = []; EmptyOrdered = []; }
        }
    }
}
