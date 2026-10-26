using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace DefaultGame;

internal sealed class DeviceScenePairExclusions : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly World _world;
    private readonly QueryHandle _relations;
    private readonly Dictionary<Entity, uint> _bodyIndices = [];
    private readonly List<IndexPair> _pairs = [];
    private readonly Buffer _buffer;
    private readonly BufferSrv _view;
    private readonly JobResourceAccess[] _accesses;
    private uint _membershipVersion;
    private bool _initialized;

    internal DeviceScenePairExclusions(IGraphicsBackend backend, Device device, World world,
        ReadOnlySpan<Entity> bodies, int capacity)
    {
        _backend = backend; _world = world;
        _relations = world.Query(new QueryDefinitionBuilder().All<SceneCollisionExclusion>()
            .Read<UndirectedRelationEndpoints<SceneCollisionExclusion>>());
        if (capacity > 0)
            for (int index = 0; index < bodies.Length; index++) _bodyIndices.Add(bodies[index], (uint)index);
        _buffer = backend.CreateBuffer(device, new((ulong)(capacity + 1) * 8, BufferUsages.ShaderRead,
            "Scene excluded body pairs"), MemoryType.Upload);
        _view = backend.CreateBufferSrv(device, new(_buffer, new(0, _buffer.Info.Size), StructureStride: 8));
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        QueryDefinition reads = world.GetQueryDefinition(_relations);
        _accesses = new JobResourceAccess[QueryJobAccess.Count(reads) + 2];
        QueryJobAccess.CopyAccesses(world, reads, _accesses);
        _accesses[^2] = JobResourceAccess.Write(scheduler.GetResourceToken(this));
        _accesses[^1] = JobResourceAccess.Write(scheduler.GetResourceToken(_buffer), 0, (long)_buffer.Info.Size);
    }

    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings) =>
        bindings.Add("SceneExcludedBodyPairs", [ResourceBinding.ReadOnlyBuffer(_view)]);
    internal JobResourceAccess ReadAccess(Scheduler scheduler) =>
        JobResourceAccess.Read(scheduler.GetResourceToken(_buffer), 0, (long)_buffer.Info.Size);
    internal UploadDeviceScenePairExclusions Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new Refresh(this), _accesses, dependency);

    private readonly struct Refresh(DeviceScenePairExclusions exclusions) : IJob
    {
        public void Execute()
        {
            uint version = exclusions._world.MembershipVersion(exclusions._relations);
            if (exclusions._initialized && version == exclusions._membershipVersion) return;
            exclusions._pairs.Clear();
            var read = new ReadPairs(exclusions);
            exclusions._world.ExecuteReadQuery(exclusions._relations, ref read,
                static (scoped QueryCursor rows, ref ReadPairs state) => state.Read(rows));
            exclusions._pairs.Sort(static (first, second) => first.First != second.First
                ? first.First.CompareTo(second.First) : first.Second.CompareTo(second.Second));
            using MappedBuffer mapping = exclusions._backend.Map(exclusions._buffer, MapType.Write, BufferRange.Whole);
            Span<IndexPair> values = MemoryMarshal.Cast<byte, IndexPair>(mapping.Bytes);
            values[0] = new((uint)exclusions._pairs.Count, 0);
            CollectionsMarshal.AsSpan(exclusions._pairs).CopyTo(values[1..]);
            mapping.Flush(new(0, (ulong)(exclusions._pairs.Count + 1) * 8));
            exclusions._membershipVersion = version;
            exclusions._initialized = true;
        }
    }

    private readonly struct ReadPairs(DeviceScenePairExclusions exclusions)
    {
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                var ends = row.Read<UndirectedRelationEndpoints<SceneCollisionExclusion>>();
                uint first = exclusions._bodyIndices[ends.EndpointA], second = exclusions._bodyIndices[ends.EndpointB];
                exclusions._pairs.Add(new(Math.Min(first, second), Math.Max(first, second)));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct IndexPair(uint First, uint Second);

    public void Dispose()
    {
        _world.ReleaseQuery(_relations);
        _view.Dispose();
        _buffer.Dispose();
    }
}

internal readonly struct UploadDeviceScenePairExclusions(DeviceScenePairExclusions exclusions) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => exclusions.Schedule(in context, dependency);
}
