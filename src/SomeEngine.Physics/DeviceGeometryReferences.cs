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
}

/// <summary>Converts original geometry parameters and body references; current poses are read on the device.</summary>
public interface IDeviceGeometrySource<TComponent> : IJobComponentResources<TComponent> where TComponent : struct, IComponent
{
    DeviceGeometryInput Read(in TComponent component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference);
    JobHandle Prepare(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency) => dependency;
}

public readonly struct DeviceComponentSource<TGeometry, TReader> : IDeviceGeometrySource<TGeometry>
    where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
    where TReader : struct, IDeviceGeometryReader<TGeometry>
{
    public static void Collect(in TGeometry component, ref JobResourceAccessBuilder resources) => TReader.Collect(in component, ref resources);
    public static long GetVersion(in TGeometry component) => TReader.GetVersion(in component);
    public JobHandle Prepare(in PhysicsStepContext context, int rowsPerPacket, JobHandle dependency) =>
        default(TReader).Prepare(in context, rowsPerPacket, dependency);
    public DeviceGeometryInput Read(in TGeometry component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = new() { BodyCount = 1, FirstBody = (uint)row.Read<DeviceBodyBinding>().Index };
        var origin = new BodyPose { Rotation = Quaternion.Identity };
        return default(TReader).Read(in component, in origin, destination);
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
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionParticipants _participants;
    private readonly TSource _source;
    private readonly uint _geometryType;
    private readonly int _rowsPerPacket;
    private readonly QueryHandle _access;
    private readonly JobResourceAccess[] _accesses;

    internal WriteDeviceGeometryReferences(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
        TSource source, uint geometryType, int rowsPerPacket)
    {
        _grid = grid; _participants = participants; _source = source; _geometryType = geometryType; _rowsPerPacket = rowsPerPacket;
        QueryDefinition reads = QueryDefinition.Empty.WithOptionalRead<DeviceBodyBinding>();
        _access = participants.World.Query(reads);
        var descriptions = new QueryReadResources();
        descriptions.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        var accesses = new List<JobResourceAccess>(participants.GetReadAccesses());
        var extra = new JobResourceAccess[QueryJobAccess.Count(reads)];
        QueryJobAccess.CopyAccesses(participants.World, reads, extra, descriptions);
        accesses.AddRange(extra);
        _accesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        dependency = _source.Prepare(in context, _rowsPerPacket, dependency);
        return new WriteDeviceGeometryReferenceJob<TComponent, TSource>(_grid, _participants, _access, _source, _geometryType)
            .ScheduleParallel(context.World, _accesses, new JobEntityScheduleOptions(rowsPerPacket: _rowsPerPacket,
                filter: context.Bodies), dependency);
    }
    public void Dispose() => _participants.World.ReleaseQuery(_access);
}

public readonly struct DeviceGeometryReferenceUploadResources : IJobComponentResources<DeviceGeometryBinding>
{
    public static void Collect(in DeviceGeometryBinding component, ref JobResourceAccessBuilder resources)
    {
        DeviceGeometryUploadResources.Collect(in component, ref resources);
        component.Geometry.CollectReferenceUpload(component.Index, ref resources);
    }
}

internal readonly partial struct WriteDeviceGeometryReferenceJob<TComponent, TSource>(DeviceCollisionGrid grid,
    DeviceCollisionParticipants participants, QueryHandle access, TSource source, uint geometryType) : IJobEntity
    where TComponent : struct, IComponent where TSource : struct, IDeviceGeometrySource<TComponent>
{
    public void Execute(Entity entity, [JobResources<DeviceCollisionUploadResources>] in DeviceCollisionBody body,
        [JobResources<DeviceGeometryReferenceUploadResources>] in DeviceGeometryBinding binding,
        [JobResources(nameof(TSource))] in TComponent component)
    {
        if (!ReferenceEquals(body.Grid, grid)) return;
        var read = new ReadSource(entity, component, source, binding.Geometry);
        participants.World.ExecuteQuery(access, ref read);
        read.Input.GeometryIndex = (uint)binding.Index;
        read.Input.GeometryType = geometryType;
        read.Reference.GridIndex = (uint)body.Index;
        binding.Geometry.WriteInput(binding.Index, in read.Input);
        binding.Geometry.WriteReference(binding.Index, in read.Reference);
        var input = new DeviceCollisionInput { EntityIndex = (uint)entity.Index, EntityGeneration = (uint)entity.Generation,
            GeometryType = geometryType, GeometryIndex = (uint)binding.Index, Members = participants.ReadMembers(entity) };
        grid.WriteInput(body.Index, in input);
    }

    private struct ReadSource(Entity entity, TComponent component, TSource source, DeviceCollisionGeometry destination) : IQueryExecution
    {
        internal DeviceGeometryInput Input;
        internal DeviceGeometryReference Reference;
        public void Execute(scoped QueryCursor access)
        {
            access.TryGetRow(entity, out QueryRow row);
            Input = source.Read(in component, row, access, destination, out Reference);
        }
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
