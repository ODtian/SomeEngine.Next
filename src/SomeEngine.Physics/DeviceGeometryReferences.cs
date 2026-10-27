using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct DeviceGeometryReference
{
    public uint BodyCount, FirstBody, SecondBody, ThirdBody;
    public Vector3 FirstLocalPoint;
    public uint Kind;
    public Vector3 SecondLocalPoint;
    public uint GridIndex;
    public Vector3 ThirdLocalPoint;
    public uint FieldIndex;
}

/// <summary>Converts original geometry parameters and body references; current poses are read on the device.</summary>
public interface IDeviceGeometrySource<TComponent> : IJobComponentResources<TComponent> where TComponent : struct, IComponent
{
    QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceBodyBinding>();
    void AddReadResources(QueryReadResources resources) => resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
    DeviceGeometryInput Read(in TComponent component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference);
    JobHandle Prepare(in PhysicsStepContext context, QueryDefinition geometries, int rowsPerPacket, JobHandle dependency) => dependency;
}

public readonly struct DeviceComponentSource<TGeometry, TReader>(TReader reader) : IDeviceGeometrySource<TGeometry>
    where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
    where TReader : struct, IDeviceGeometryReader<TGeometry>
{
    public static void Collect(in TGeometry component, ref JobResourceAccessBuilder resources) => TReader.Collect(in component, ref resources);
    public static long GetVersion(in TGeometry component) => TReader.GetVersion(in component);
    public JobHandle Prepare(in PhysicsStepContext context, QueryDefinition geometries, int rowsPerPacket, JobHandle dependency) =>
        reader.Prepare(in context, geometries, rowsPerPacket, dependency);
    public DeviceGeometryInput Read(in TGeometry component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = new() { BodyCount = 1, FirstBody = (uint)row.Read<DeviceBodyBinding>().Index };
        var origin = new BodyPose { Rotation = Quaternion.Identity };
        return reader.Read(in component, in origin, destination);
    }
}

public readonly struct DeviceDynamicCapsuleSource : IDeviceGeometrySource<DynamicCapsule>
{
    public static void Collect(in DynamicCapsule component, ref JobResourceAccessBuilder resources) { }
    public DeviceGeometryInput Read(in DynamicCapsule component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = DeviceGeometrySources.Segment(component.First, component.Second, access);
        return new() { ConvexRadius = component.Radius };
    }
}

public readonly struct DeviceDynamicFaceSource : IDeviceGeometrySource<DynamicFace>
{
    public static void Collect(in DynamicFace component, ref JobResourceAccessBuilder resources) { }
    public DeviceGeometryInput Read(in DynamicFace component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = new() { BodyCount = 3, Kind = 2,
            FirstBody = DeviceGeometrySources.BodyIndex(component.First, access),
            SecondBody = DeviceGeometrySources.BodyIndex(component.Second, access),
            ThirdBody = DeviceGeometrySources.BodyIndex(component.Third, access),
            FirstLocalPoint = component.First.LocalPoint, SecondLocalPoint = component.Second.LocalPoint,
            ThirdLocalPoint = component.Third.LocalPoint };
        return new() { ConvexRadius = component.Radius };
    }
}

public readonly struct DeviceDynamicEdgeSource : IDeviceGeometrySource<DynamicEdge>
{
    public static void Collect(in DynamicEdge component, ref JobResourceAccessBuilder resources) { }
    public DeviceGeometryInput Read(in DynamicEdge component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = DeviceGeometrySources.Segment(component.First, component.Second, access);
        return new();
    }
}

internal static class DeviceGeometrySources
{
    internal static uint BodyIndex(in GeometryEndpoint endpoint, scoped QueryCursor access)
    {
        access.TryGetRow(endpoint.Body, out QueryRow row);
        return (uint)row.Read<DeviceBodyBinding>().Index;
    }
    internal static DeviceGeometryReference Segment(in GeometryEndpoint first, in GeometryEndpoint second, scoped QueryCursor access) =>
        new() { BodyCount = 2, Kind = 1, FirstBody = BodyIndex(in first, access), SecondBody = BodyIndex(in second, access),
            FirstLocalPoint = first.LocalPoint, SecondLocalPoint = second.LocalPoint };
}

public readonly struct WriteDeviceGeometryReferences<TComponent, TSource> : IPhysicsStage, IDisposable
    where TComponent : struct, IComponent where TSource : struct, IDeviceGeometrySource<TComponent>
{
    private readonly GeometryInputRows _rows;

    internal WriteDeviceGeometryReferences(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
        TSource source, uint geometryType, int rowsPerPacket) =>
        _rows = new(grid, participants, source, geometryType, rowsPerPacket);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => _rows.Schedule(in context, dependency);
    public void Dispose() => _rows.Dispose();

    private sealed class GeometryInputRows : IQueryReadEntities, IDisposable
    {
        private readonly DeviceCollisionGrid _grid;
        private readonly DeviceCollisionParticipants _participants;
        private readonly TSource _source;
        private readonly uint _geometryType;
        private readonly int _rowsPerPacket;
        private readonly QueryHandle _geometries, _references;
        private readonly QueryReadResources _resources = new();
        private readonly JobResourceAccess[] _accesses;
        private readonly List<Entity> _entities = [];
        private World World => _participants.World;
        public int Count => _entities.Count;
        public Entity GetEntity(int index) => _entities[index];

        internal GeometryInputRows(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
            TSource source, uint geometryType, int rowsPerPacket)
        {
            _grid = grid; _participants = participants; _source = source;
            _geometryType = geometryType; _rowsPerPacket = rowsPerPacket;
            QueryDefinition references = source.AddReads(QueryDefinition.Empty);
            QueryDefinition geometries = source.AddReads(new QueryDefinitionBuilder().Read<DeviceCollisionBody>()
                .Read<DeviceGeometryBinding>().Read<TComponent>().Build());
            _references = World.Query(references);
            _geometries = World.Query(geometries);
            source.AddReadResources(_resources);
            _resources.Add<DeviceCollisionBody, DeviceCollisionUploadResources>();
            _resources.Add<DeviceGeometryBinding, DeviceGeometryReferenceUploadResources>();
            _resources.Add<TComponent, TSource>();
            var accesses = new List<JobResourceAccess>(participants.GetReadAccesses());
            foreach (QueryDefinition definition in new[] { references, geometries })
            {
                var declared = new JobResourceAccess[QueryJobAccess.Count(definition)];
                QueryJobAccess.CopyAccesses(World, definition, declared, _resources);
                accesses.AddRange(declared);
            }
            accesses.Add(JobResourceAccess.Write(WorldJobs.GetScheduler(World).GetResourceToken(this)));
            _accesses = accesses.ToArray();
        }

        internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            dependency = _source.Prepare(in context, QueryDefinition.Empty, _rowsPerPacket, dependency);
            return WorldJobs.GetScheduler(World).Schedule(new ReadMembers(this), _accesses, dependency);
        }

        public void Dispose()
        {
            World.ReleaseQuery(_geometries);
            World.ReleaseQuery(_references);
        }

        private readonly struct ReadMembers(GeometryInputRows inputs) : IJob
        {
            public void Execute()
            {
                inputs._entities.Clear();
                GeometryInputRows state = inputs;
                inputs.World.ExecuteReadQuery(inputs._geometries, ref state,
                    static (scoped QueryCursor rows, ref GeometryInputRows output) =>
                    {
                        foreach (QueryRow row in rows.Rows)
                            if (ReferenceEquals(row.ReadRef<DeviceCollisionBody>().Grid, output._grid))
                                output._entities.Add(row.Entity);
                    });
                inputs._resources.ScheduleEntities(inputs.World, inputs._geometries, inputs,
                    new WriteInputs(inputs), inputs._accesses);
            }
        }

        private readonly struct WriteInputs(GeometryInputRows inputs) : IJob
        {
            public void Execute()
            {
                GeometryInputRows state = inputs;
                inputs.World.ExecuteReadQuery(inputs._references, ref state,
                    static (scoped QueryCursor references, ref GeometryInputRows output) =>
                    {
                        var write = new WriteGeometryRows(output, references);
                        output.World.ExecuteReadQuery(output._geometries, ref write,
                            static (scoped QueryCursor rows, ref WriteGeometryRows state) => state.Execute(rows));
                    });
            }
        }

        private readonly ref struct WriteGeometryRows(GeometryInputRows inputs, QueryCursor references)
        {
            private readonly QueryCursor _references = references;

            internal void Execute(scoped QueryCursor rows)
            {
                foreach (QueryRow row in rows.Rows)
                {
                    ref readonly DeviceCollisionBody body = ref row.ReadRef<DeviceCollisionBody>();
                    if (!ReferenceEquals(body.Grid, inputs._grid)) continue;
                    ref readonly DeviceGeometryBinding binding = ref row.ReadRef<DeviceGeometryBinding>();
                    ref readonly TComponent component = ref row.ReadRef<TComponent>();
                    DeviceGeometryInput value = inputs._source.Read(in component, row, _references,
                        binding.Geometry, out DeviceGeometryReference reference);
                    value.GeometryIndex = (uint)binding.Index;
                    value.GeometryType = inputs._geometryType;
                    reference.GridIndex = (uint)body.Index;
                    binding.Geometry.WriteInput(binding.Index, in value);
                    binding.Geometry.WriteReference(binding.Index, in reference);
                    var input = new DeviceCollisionInput { EntityIndex = (uint)row.Entity.Index,
                        EntityGeneration = (uint)row.Entity.Generation, GeometryType = inputs._geometryType,
                        GeometryIndex = (uint)binding.Index, Members = inputs._participants.ReadMembers(row.Entity) };
                    inputs._grid.WriteInput(body.Index, in input);
                }
            }
        }
    }
}

public readonly struct DeviceGeometryReferenceUploadResources : IJobComponentResources<DeviceGeometryBinding>
{
    public static void Collect(in DeviceGeometryBinding component, ref JobResourceAccessBuilder resources)
    {
        DeviceGeometryUploadResources.Collect(in component, ref resources);
        component.Geometry.CollectReferenceUpload(component.Index, ref resources);
    }
}

public sealed partial class DeviceCollisionGrid
{
    public WriteDeviceGeometryReferences<TComponent, TSource> WriteGeometryReferences<TComponent, TSource>(
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, TSource source, uint geometryType, int rowsPerPacket)
        where TComponent : struct, IComponent where TSource : struct, IDeviceGeometrySource<TComponent> =>
        new(this, new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(0), DevicePairing.Within),
            source, geometryType, rowsPerPacket);

    public WriteDeviceGeometryReferences<TComponent, TSource> WriteGeometryReferences<TComponent, TSource>(
        RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs, TSource source, uint geometryType, int rowsPerPacket)
        where TComponent : struct, IComponent where TSource : struct, IDeviceGeometrySource<TComponent> =>
        new(this, new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Between),
            source, geometryType, rowsPerPacket);

    public WriteDeviceGeometryReferences<TComponent, TSource> WriteGeometryReferences<TComponent, TSource>(
        RelationQuery<EntityProduct2, (Entity, Entity)> pairs, TSource source, uint geometryType, int rowsPerPacket)
        where TComponent : struct, IComponent where TSource : struct, IDeviceGeometrySource<TComponent> =>
        new(this, new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Product),
            source, geometryType, rowsPerPacket);
}
