using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public static partial class EntityQueryJobs
{
    /// <summary>Derived query rows and their component ranges held within one synchronous parent borrow.</summary>
    internal sealed class GroupQueries : IDisposable
    {
        private readonly World _world;
        private readonly QueryReadResources? _resources;
        private readonly uint _version;
        private readonly Workspaces _available;
        private readonly Workspace _workspace;
        private readonly Batch _storage = new();
        private readonly List<QueryRanges[]> _queryRanges = [];
        private readonly Dictionary<(int Start, int Count), CompatibleRanges> _compatible = [];
        private int _revision;

        private GroupQueries(World world, QueryReadResources? resources, uint version)
        {
            _world = world; _resources = resources; _version = version;
            _available = s_workspaces.GetValue(world, static _ => new());
            lock (_available.Gate) _workspace = _available.Items.Count == 0 ? new() : _available.Items.Pop();
            _workspace.Reset();
        }

        internal static GroupQueries Create<TWork>(World world, QueryReadResources? resources, uint version, in TWork work)
            where TWork : struct, IEntityQueryWork
        {
            var queries = new GroupQueries(world, resources, version);
            try { queries.Refresh(in work); return queries; }
            catch { queries.Dispose(); throw; }
        }

        internal int Count => _workspace.GroupQueries.Length;
        internal EntityQueryViews Get(int index) => new(_workspace.GroupQueries[index]);

        internal void Refresh<TWork>(in TWork work) where TWork : struct, IEntityQueryWork
        {
            Scheduler scheduler = WorldJobs.GetScheduler(_world);
            scheduler.RequireCurrentAccess(RelationshipJobAccess.TopologyRead(_world), requireSingleWorkItem: false);
            int count = work.Count, queryCount = work.QueryCount;
            if (_workspace.GroupQueries.Length != count) Array.Resize(ref _workspace.GroupQueries, count);
            for (int index = 0; index < count; index++)
            {
                while (_queryRanges.Count <= index) _queryRanges.Add([]);
                if (_queryRanges[index].Length != queryCount)
                    _queryRanges[index] = Enumerable.Range(0, queryCount).Select(static _ => new QueryRanges()).ToArray();
                Batch batch = _workspace.Get(index, queryCount);
                for (int query = 0; query < queryCount; query++)
                {
                    QueryRanges ranges = _queryRanges[index][query];
                    QueryHandle handle = work.GetQuery(index, query);
                    ReadOnlySpan<Entity> members = work.GetEntities(index, query);
                    EntityQueryData data = batch.Queries[query];
                    if (!ranges.Captured || !ranges.Query.Equals(handle) || !members.SequenceEqual(data.Entities.AsSpan(0, data.Count)))
                    {
                        data.Set(_world, handle, members);
                        _storage.Accesses.Clear();
                        _storage.AddRanges(_world, data, _resources);
                        ranges.Storage.Clear(); ranges.Storage.AddRange(_storage.Accesses);
                        ranges.Query = handle; ranges.Captured = true;
                    }
                    batch.Accesses.AddRange(ranges.Storage);
                }
                SetVersion(_world, _version, batch.Queries);
                _resources?.AddEntityQueryResources(_world, batch.Queries, batch.Accesses);
                foreach (JobResourceAccess access in batch.Accesses)
                    scheduler.RequireCurrentAccess(access, requireSingleWorkItem: false);
                _workspace.GroupQueries[index] = batch.Queries;
            }
            _revision++;
        }

        internal void ExecuteParallel<TJob>(in TJob job, int start, int count) where TJob : struct, IEntityQueryJob
        {
            if (!_compatible.TryGetValue((start, count), out CompatibleRanges? compatible))
            { compatible = new(); _compatible.Add((start, count), compatible); }
            GroupRanges ranges = compatible.Ranges;
            if (compatible.Revision != _revision)
            {
                ranges.CurrentAccesses.Clear(); ranges.CurrentStarts.Clear(); ranges.CurrentStarts.Add(0);
                for (int index = 0; index < count; index++)
                {
                    ranges.CurrentAccesses.AddRange(_workspace.Batches[start + index].Accesses);
                    ranges.CurrentStarts.Add(ranges.CurrentAccesses.Count);
                }
                ranges.Update(count);
                compatible.Revision = _revision;
            }
            WorldJobs.GetScheduler(_world).ExecuteParallel(
                new ExecuteCompatibleGroups<TJob>(_workspace.GroupQueries, ranges.Groups, job, start, start), ranges.Groups.Count);
        }

        public void Dispose() { lock (_available.Gate) _available.Items.Push(_workspace); }

        private sealed class QueryRanges
        {
            internal QueryHandle Query;
            internal bool Captured;
            internal readonly List<JobResourceAccess> Storage = [];
        }
        private sealed class CompatibleRanges
        {
            internal int Revision = -1;
            internal readonly GroupRanges Ranges = new();
        }
    }
}
