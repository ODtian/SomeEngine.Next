using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Updates selected geometry bounds while reading referenced entities through the supplied access query.</summary>
public readonly struct UpdateGeometryBounds<TGeometry, TReader> : IPhysicsStage
    where TGeometry : struct, ICollisionGeometry<TGeometry>
    where TReader : struct, ICollisionGeometryReader<TGeometry>
{
    private readonly World _world;
    private readonly QueryDefinition _members;
    private readonly QueryHandle _access;
    private readonly TReader _reader;
    private readonly int _rowsPerPacket;
    private readonly JobResourceAccess[] _accesses;

    public UpdateGeometryBounds(World world, QueryHandle members, QueryHandle access, TReader reader, int rowsPerPacket)
    {
        _world = world;
        _members = world.GetQueryDefinition(members);
        _access = access;
        _reader = reader;
        _rowsPerPacket = rowsPerPacket;
        QueryDefinition definition = world.GetQueryDefinition(access);
        _accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _accesses);
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        new UpdateGeometryReaderBoundsJob<TGeometry, TReader>(_world, _access, _reader).ScheduleParallel(_world,
            _accesses, new JobEntityScheduleOptions(rowsPerPacket: _rowsPerPacket, filter: _members), dependency);
}

internal readonly partial struct UpdateGeometryReaderBoundsJob<TGeometry, TReader>(World world,
    QueryHandle access, TReader reader) : IJobEntity
    where TGeometry : struct, ICollisionGeometry<TGeometry>
    where TReader : struct, ICollisionGeometryReader<TGeometry>
{
    private readonly World _world = world;
    private readonly QueryHandle _access = access;
    private readonly TReader _reader = reader;

    public void Execute(Entity entity, ref CollisionBounds bounds)
    {
        var read = new ReadGeometryBounds<TGeometry, TReader>(entity, _reader);
        _world.ExecuteQuery(_access, ref read);
        bounds = read.Bounds;
    }
}

internal struct ReadGeometryBounds<TGeometry, TReader>(Entity entity, TReader reader) : IQueryExecution
    where TGeometry : struct, ICollisionGeometry<TGeometry>
    where TReader : struct, ICollisionGeometryReader<TGeometry>
{
    internal CollisionBounds Bounds;

    public void Execute(scoped QueryCursor access)
    {
        access.TryGetRow(entity, out QueryRow row);
        TGeometry geometry = reader.ReadGeometry(row, access, out BodyPose pose);
        Bounds = TGeometry.Bounds(in geometry, in pose);
    }
}
