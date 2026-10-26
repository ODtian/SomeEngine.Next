using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal enum SceneInputChangeKind : byte { MoveBody, RemoveJoint }
internal readonly record struct SceneInputChange(long Number, Entity Subject, SceneInputChangeKind Kind,
    Vector3 Position = default) : IBufferElement;
internal struct SceneInputIndex : IComponent { public int NextChange; }

internal readonly struct ApplySceneInputChanges : IPhysicsStage, IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _changes;
    private readonly JobResourceAccess[] _accesses;
    private readonly bool _hasChanges;

    internal ApplySceneInputChanges(World world, bool hasChanges)
    {
        _world = world; _hasChanges = hasChanges;
        QueryDefinition definition = new QueryDefinitionBuilder().ReadWrite<SceneInputIndex>().ReadBuffer<SceneInputChange>()
            .Optional<BodyPose>(QueryAccess.Read).Build();
        _changes = world.Query(definition);
        _accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _accesses);
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!_hasChanges || context.Substep != 0) return dependency;
        var commands = new JobCommandBuffer(_world, 1);
        commands.Schedule(0, new Record(_world, _changes, context.Number), _accesses, dependency);
        JobHandle playback = commands.SchedulePlayback();
        return WorldJobs.GetScheduler(_world).ScheduleFinally(new ReleaseChanges(commands), JobScheduleOptions.Default, playback);
    }

    private readonly struct Record(World world, QueryHandle changes, long number) : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            var read = new Read(world, commands, number);
            world.ExecuteQuery(changes, ref read);
        }
    }

    private ref struct Read(World world, JobCommandWriter commands, long number) : IQueryExecution
    {
        private JobCommandWriter _commands = commands;
        public void Execute(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                ReadOnlySpan<SceneInputChange> changes = row.ReadBuffer<SceneInputChange>().AsSpan();
                ref SceneInputIndex index = ref row.ReadWrite<SceneInputIndex>();
                while (index.NextChange < changes.Length && changes[index.NextChange].Number <= number)
                {
                    ref readonly SceneInputChange change = ref changes[index.NextChange++];
                    if (change.Kind == SceneInputChangeKind.RemoveJoint) _commands.DestroyEntity(change.Subject);
                    else
                    {
                        BodyPose pose = world.Read<BodyPose>(change.Subject);
                        pose.Position = change.Position;
                        _commands.Replace(change.Subject, pose);
                    }
                }
            }
        }
    }

    private readonly record struct ReleaseChanges(JobCommandBuffer Commands) : IJob
    {
        public void Execute() => Commands.Dispose();
    }
    public void Dispose() => _world.ReleaseQuery(_changes);
}
