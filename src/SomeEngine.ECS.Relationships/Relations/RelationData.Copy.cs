using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationData<T> : IWorldDataService
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
        internal readonly ReadOnlySpan<AdjacencyHead> Outgoing, Incoming;
        internal readonly ReadOnlySpan<AppliedEdge> Applied;
        internal readonly Dictionary<Entity, AppliedEdge>? Retired;
        internal readonly Dictionary<AdjacencyKey, AdjacencyHead>? RetiredHeads;
        internal readonly Dictionary<AdjacencyKey, List<Entity>>? Future;

        internal DataView(RelationData<T> source, bool preserve)
        {
            Ordered = source._usesOrder; Deferred = preserve && source._hasDeferredChanges;
            Version = preserve ? source._version : 0; ReaderPosition = preserve ? source._reader.Position : 0;
            int count = source._world.Used + 1;
            Outgoing = source._outgoing.AsSpan(0, Math.Min(source._outgoing.Length, count));
            Incoming = source._directed ? source._incoming.AsSpan(0, Math.Min(source._incoming.Length, count)) : [];
            Applied = preserve ? source._applied.AsSpan(0, Math.Min(source._applied.Length, count)) : [];
            Retired = preserve ? source._retiredApplied : null;
            RetiredHeads = preserve ? source._retiredHeads : null;
            Future = preserve ? source._futureOrders : null;
        }
    }

    internal sealed class SavedData : WorldServiceData
    {
        internal bool Preserve, Deferred, Ordered;
        internal ulong Version, ReaderPosition;
        internal AdjacencyHead[] Outgoing = [], Incoming = [];
        internal AppliedEdge[] Applied = [];
        internal KeyValuePair<Entity, AppliedEdge>[] Retired = [];
        internal KeyValuePair<AdjacencyKey, AdjacencyHead>[] RetiredHeads = [];
        internal KeyValuePair<AdjacencyKey, List<Entity>>[] Future = [];
        internal int OutgoingCount, IncomingCount, AppliedCount, RetiredCount, RetiredHeadCount, FutureCount;

        internal SavedData() { }
        internal void Capture(RelationData<T> source, bool preserve)
        {
            Preserve = preserve;
            Ordered = source._usesOrder;
            Deferred = preserve && source._hasDeferredChanges;
            Version = preserve ? source._version : 0;
            ReaderPosition = preserve ? source._reader.Position : 0;
            OutgoingCount = Math.Min(source._outgoing.Length, source._world.Used + 1);
            CopyHeads(source._outgoing.AsSpan(0, OutgoingCount), ref Outgoing);
            IncomingCount = source._directed ? Math.Min(source._incoming.Length, source._world.Used + 1) : 0;
            CopyHeads(source._incoming.AsSpan(0, IncomingCount), ref Incoming);
            if (!preserve) return;
            AppliedCount = Math.Min(source._applied.Length, source._world.Used + 1);
            if (Applied.Length < AppliedCount) Applied = new AppliedEdge[AppliedCount];
            source._applied.AsSpan(0, AppliedCount).CopyTo(Applied);
            RetiredCount = source._retiredApplied.Count;
            if (Retired.Length < RetiredCount) Retired = new KeyValuePair<Entity, AppliedEdge>[RetiredCount];
            int index = 0;
            foreach (var pair in source._retiredApplied) Retired[index++] = pair;
            RetiredHeadCount = source._retiredHeads.Count;
            if (RetiredHeads.Length < RetiredHeadCount) Array.Resize(ref RetiredHeads, RetiredHeadCount);
            index = 0;
            foreach (var pair in source._retiredHeads)
            {
                RetiredHeads[index] = new(pair.Key, CopyHead(pair.Value, RetiredHeads[index].Value));
                index++;
            }
            FutureCount = source._futureOrders.Count;
            if (Future.Length < FutureCount) Array.Resize(ref Future, FutureCount);
            index = 0;
            foreach (var pair in source._futureOrders)
            {
                List<Entity> members = Future[index].Value ?? new();
                members.Clear(); members.AddRange(pair.Value);
                Future[index++] = new(pair.Key, members);
            }
        }

        private static AdjacencyHead CopyHead(in AdjacencyHead source, in AdjacencyHead target = default)
        {
            AdjacencyHead copy = source;
            copy.Items = target.Items;
            if (copy.Items is null || copy.Items.Length < source.Count)
            {
                if (copy.Items is { } items) AdjacencyHead.Return(items);
                copy.Items = source.Count == 0 ? null : AdjacencyHead.Rent(source.Count);
            }
            source.Entries.CopyTo(copy.Items);
            return copy;
        }
        private static void CopyHeads(ReadOnlySpan<AdjacencyHead> source, ref AdjacencyHead[] values)
        {
            if (values.Length < source.Length) { values = CopyHeads(source, values); return; }
            for (int i = 0; i < source.Length; i++) values[i] = CopyHead(source[i], values[i]);
        }
        private static AdjacencyHead[] CopyHeads(ReadOnlySpan<AdjacencyHead> source, AdjacencyHead[] previous)
        {
            var values = new AdjacencyHead[source.Length];
            for (int i = 0; i < previous.Length; i++) values[i] = CopyHead(source[i], previous[i]);
            for (int i = previous.Length; i < source.Length; i++) values[i] = CopyHead(source[i]);
            return values;
        }
        private static void RestoreHeads(ref AdjacencyHead[] target, ReadOnlySpan<AdjacencyHead> source)
        {
            if (target.Length < source.Length) Array.Resize(ref target, source.Length);
            for (int i = 0; i < source.Length; i++) target[i] = CopyHead(source[i]);
        }

        internal override void Restore(World world) => Restore(world, false);

        internal void Restore(World world, bool transfer)
        {
            RelationData<T>? target = world.FindLifecycle<RelationData<T>>();
            if (target is null) { target = new(world); world.AddLifecycle(target); }
            target.EnsureCapacity();
            if (transfer)
            { target._outgoing = Outgoing; target._incoming = Incoming; Outgoing = []; Incoming = []; OutgoingCount = IncomingCount = 0; }
            else
            { RestoreHeads(ref target._outgoing, Outgoing.AsSpan(0, OutgoingCount)); RestoreHeads(ref target._incoming, Incoming.AsSpan(0, IncomingCount)); }
            if (Preserve)
            {
                if (transfer) target._applied = Applied;
                else Applied.AsSpan(0, AppliedCount).CopyTo(target._applied);
                foreach (var pair in Retired.AsSpan(0, RetiredCount)) target._retiredApplied.Add(pair.Key, pair.Value);
                foreach (var pair in RetiredHeads.AsSpan(0, RetiredHeadCount)) target._retiredHeads.Add(pair.Key, transfer ? pair.Value : CopyHead(pair.Value));
                if (transfer) { RetiredHeads = []; RetiredHeadCount = 0; }
                foreach (var pair in Future.AsSpan(0, FutureCount)) target._futureOrders.Add(pair.Key, new(pair.Value));
            }
            int type = ComponentType<RelationEndpoints<T>>.TypeId;
            for (int i = 0; i < world.ArchetypeCount; i++)
                foreach (Chunk chunk in world.Archetypes[i].AllChunks)
                {
                    if ((uint)type >= (uint)chunk.Columns.Length || chunk.Columns[type].ColumnPlusOne == 0) continue;
                    for (int row = 0; row < chunk.Count; row++)
                        if (chunk.Columns[type].Has(row) && !World.HasDestroyed(in world.Records[chunk.Entities[row].Id]))
                        {
                            Entity edge = chunk.Entities[row];
                            RelationEndpoints<T> value = ColumnEntry.At<RelationEndpoints<T>>(ref chunk.Columns[type], row);
                            target.Claim(edge, value);
                            if (Preserve)
                            {
                                AppliedEdge applied = target.Applied(edge);
                                if (value.Source != applied.Source) target._sourceDifferences.Set(edge, value.Source);
                                if (value.Target != applied.Target) target._targetDifferences.Set(edge, value.Target);
                            }
                            else target._applied[edge.Id] = new() { Source = value.Source, Target = value.Target, Generation = edge.Generation };
                        }
                }
            if (!Preserve)
            {
                RestorePlaces(target, target._outgoing, target._directed ? RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident);
                if (target._directed) RestorePlaces(target, target._incoming, RelationAdjacencyRole.Incoming);
            }
            target._usesOrder = Ordered;
            target._hasDeferredChanges = Preserve && Deferred;
            target._version = Preserve ? Version : world.Version;
            target._reader.Reset(Preserve ? ReaderPosition : world.DestroyedSequence);
        }

        private static void RestorePlaces(RelationData<T> target, ReadOnlySpan<AdjacencyHead> heads, RelationAdjacencyRole role)
        {
            for (int id = 1; id < heads.Length; id++)
                for (int place = 0; place < heads[id].Count; place++)
                    target.WritePlace(heads[id].Items![place].Edge, new(id, heads[id].Generation), role, place);
        }

        internal override void Clear(bool keepCapacity)
        {
            if (!keepCapacity)
            {
                foreach (AdjacencyHead head in Outgoing) if (head.Items is { } items) AdjacencyHead.Return(items);
                foreach (AdjacencyHead head in Incoming) if (head.Items is { } items) AdjacencyHead.Return(items);
                foreach (var pair in RetiredHeads) if (pair.Value.Items is { } items) AdjacencyHead.Return(items);
            }
            for (int i = 0; i < FutureCount; i++) Future[i].Value.Clear();
            OutgoingCount = IncomingCount = AppliedCount = RetiredCount = RetiredHeadCount = FutureCount = 0;
            if (!keepCapacity) { Outgoing = []; Incoming = []; Applied = []; Retired = []; RetiredHeads = []; Future = []; }
        }
    }
}
