using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
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
    static virtual ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<DeviceBodyBinding>(true)];
    DeviceGeometryInput Read(in TComponent component, SystemChunk chunk, int row, ReadOnlySpan<DeviceBodyBinding> bindings,
        scoped ReadLookup<DeviceBodyBinding> bodies, DeviceCollisionGeometry destination, out DeviceGeometryReference reference);
    JobHandle Prepare(in PhysicsStepContext context, Query geometries, int rowsPerChunk, JobHandle dependency) => dependency;
}
public readonly struct DeviceComponentSource<TGeometry, TReader>(TReader reader) : IDeviceGeometrySource<TGeometry>
    where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry> where TReader : struct, IDeviceGeometryReader<TGeometry>
{
    public static void Collect(in TGeometry component, ref JobResourceAccessBuilder resources) => TReader.Collect(in component, ref resources);
    public static long GetVersion(in TGeometry component) => TReader.GetVersion(in component);
    public JobHandle Prepare(in PhysicsStepContext context, Query geometries, int rowsPerChunk, JobHandle dependency) => reader.Prepare(in context, geometries, rowsPerChunk, dependency);
    public DeviceGeometryInput Read(in TGeometry component, SystemChunk chunk, int row, ReadOnlySpan<DeviceBodyBinding> bindings,
        scoped ReadLookup<DeviceBodyBinding> bodies, DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = new() { BodyCount = 1, FirstBody = (uint)bindings[row].Index };
        var origin = new BodyPose { Rotation = Quaternion.Identity };
        return reader.Read(in component, in origin, destination);
    }
}
public readonly struct DeviceDynamicCapsuleSource : IDeviceGeometrySource<DynamicCapsule>
{
    public static void Collect(in DynamicCapsule component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [];
    public DeviceGeometryInput Read(in DynamicCapsule component, SystemChunk chunk, int row, ReadOnlySpan<DeviceBodyBinding> bindings,
        scoped ReadLookup<DeviceBodyBinding> bodies, DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = DeviceGeometrySources.Segment(component.First, component.Second, bodies);
        return new() { ConvexRadius = component.Radius };
    }
}
public readonly struct DeviceDynamicFaceSource : IDeviceGeometrySource<DynamicFace>
{
    public static void Collect(in DynamicFace component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [];
    public DeviceGeometryInput Read(in DynamicFace component, SystemChunk chunk, int row, ReadOnlySpan<DeviceBodyBinding> bindings,
        scoped ReadLookup<DeviceBodyBinding> bodies, DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = new()
        {
            BodyCount = 3,
            Kind = 2,
            FirstBody = DeviceGeometrySources.BodyIndex(component.First, bodies),
            SecondBody = DeviceGeometrySources.BodyIndex(component.Second, bodies),
            ThirdBody = DeviceGeometrySources.BodyIndex(component.Third, bodies),
            FirstLocalPoint = component.First.LocalPoint,
            SecondLocalPoint = component.Second.LocalPoint,
            ThirdLocalPoint = component.Third.LocalPoint
        };
        return new() { ConvexRadius = component.Radius };
    }
}
public readonly struct DeviceDynamicEdgeSource : IDeviceGeometrySource<DynamicEdge>
{
    public static void Collect(in DynamicEdge component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [];
    public DeviceGeometryInput Read(in DynamicEdge component, SystemChunk chunk, int row, ReadOnlySpan<DeviceBodyBinding> bindings,
        scoped ReadLookup<DeviceBodyBinding> bodies, DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = DeviceGeometrySources.Segment(component.First, component.Second, bodies); return new();
    }
}
internal static class DeviceGeometrySources
{
    internal static uint BodyIndex(in GeometryEndpoint endpoint, scoped ReadLookup<DeviceBodyBinding> bodies) => (uint)bodies.Read(endpoint.Body).Index;
    internal static DeviceGeometryReference Segment(in GeometryEndpoint first, in GeometryEndpoint second, scoped ReadLookup<DeviceBodyBinding> bodies) =>
        new()
        {
            BodyCount = 2,
            Kind = 1,
            FirstBody = BodyIndex(first, bodies),
            SecondBody = BodyIndex(second, bodies),
            FirstLocalPoint = first.LocalPoint,
            SecondLocalPoint = second.LocalPoint
        };
}
/// <summary>Converts original geometry spans and body bindings into the existing device upload columns.</summary>
public readonly struct WriteDeviceGeometryReferences<TComponent, TSource> : IPhysicsStage, IDisposable
    where TComponent : struct, IComponent where TSource : struct, IDeviceGeometrySource<TComponent>
{
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionParticipants _participants;
    private readonly TSource _source;
    private readonly uint _geometryType;
    private readonly int _rowsPerChunk;
    private readonly Query _geometries;
    internal WriteDeviceGeometryReferences(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
        TSource source, uint geometryType, int rowsPerChunk)
    {
        _grid = grid; _participants = participants; _source = source; _geometryType = geometryType; _rowsPerChunk = rowsPerChunk;
        _geometries = participants.World.Query(new QueryDescription().With<TComponent>().With<DeviceCollisionBody>().With<DeviceGeometryBinding>());
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        dependency = _source.Prepare(in context, _geometries, _rowsPerChunk, dependency);
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobHandle captured = scheduler.Schedule(new BeginDeviceInputWrite(_grid, _participants, context),
            [.. _participants.GetReadAccesses(), JobResourceAccess.Write(scheduler.GetResourceToken(_grid.Upload))], dependency);
        return new SystemRunner(context.World, _rowsPerChunk == 0 ? 1024 : _rowsPerChunk).Schedule(_geometries,
            new Write(_grid, _participants, _source, _geometryType), captured);
    }
    private readonly struct Write(DeviceCollisionGrid grid, DeviceCollisionParticipants participants, TSource source, uint geometryType) : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<TComponent>(true),
            ChunkAccess.Read<DeviceCollisionBody>(true),ChunkAccess.Read<DeviceGeometryBinding>(true),
            ChunkAccess.ReadOptional<DeviceBodyBinding>(true),ChunkAccess.ReadOptional<ContactMaterial>(),..TSource.Components];
        public static SystemAccesses AdditionalAccesses { get; } = new SystemAccesses().ReadRandom<DeviceBodyBinding>();
        public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
        {
            ComponentResources.CollectRange<TComponent, TSource>(rows.Read<TComponent>(), ref resources);
            ComponentResources.CollectRange<DeviceCollisionBody, DeviceCollisionUploadResources>(rows.Read<DeviceCollisionBody>(), ref resources);
            ComponentResources.CollectRange<DeviceGeometryBinding, DeviceGeometryReferenceUploadResources>(rows.Read<DeviceGeometryBinding>(), ref resources);
        }
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        { foreach (JobResourceAccess access in participants.GetReadAccesses()) resources.Add(access); }
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<TComponent> components = rows.Read<TComponent>();
            var collision = rows.Read<DeviceCollisionBody>(); var geometry = rows.Read<DeviceGeometryBinding>();
            rows.TryRead<DeviceBodyBinding>(out var bodyBindings);
            rows.TryRead<ContactMaterial>(out var materials);
            using ReadLookup<DeviceBodyBinding> bodies = participants.World.OpenRead<DeviceBodyBinding>();
            using QueryScope first = participants.First.Since(grid.FirstMemberVersion).OpenRead();
            using QueryScope second = participants.Second.Since(grid.SecondMemberVersion).OpenRead();
            for (int row = 0; row < rows.Count; row++)
            {
                if (!ReferenceEquals(collision[row].Grid, grid)) continue;
                DeviceGeometryBinding binding = geometry[row];
                DeviceGeometryInput value = source.Read(in components[row], rows, row, bodyBindings, bodies, binding.Geometry, out DeviceGeometryReference reference);
                value.GeometryIndex = (uint)binding.Index; value.GeometryType = geometryType; reference.GridIndex = (uint)collision[row].Index;
                binding.Geometry.WriteInput(binding.Index, in value); binding.Geometry.WriteReference(binding.Index, in reference);
                if (rows.Has<ContactMaterial>(row)) binding.Geometry.WriteMaterial(binding.Index, in materials[row]);
                uint members = first.Contains(rows.Entities[row]) ? (uint)DevicePairMembership.First : 0;
                if (participants.Pairing != DevicePairing.Within && second.Contains(rows.Entities[row])) members |= (uint)DevicePairMembership.Second;
                var input = new DeviceCollisionInput
                {
                    EntityIndex = (uint)rows.Entities[row].Id,
                    EntityGeneration = (uint)rows.Entities[row].Generation,
                    GeometryType = geometryType,
                    GeometryIndex = (uint)binding.Index,
                    Members = members
                };
                grid.WriteInput(collision[row].Index, in input);
            }
        }
    }
    public void Dispose() { }
}

public readonly struct DeviceGeometryReferenceUploadResources : IJobComponentResources<DeviceGeometryBinding>
{
    public static void Collect(in DeviceGeometryBinding component, ref JobResourceAccessBuilder resources)
    {
        DeviceGeometryUploadResources.Collect(in component, ref resources);
        component.Geometry.CollectReferenceUpload(component.Index, ref resources);
        component.Geometry.CollectMaterialUpload(component.Index, ref resources);
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
