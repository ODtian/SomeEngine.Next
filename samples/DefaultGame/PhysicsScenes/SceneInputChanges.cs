using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
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
    private readonly Query _changes;
    private readonly bool _hasChanges;
    private readonly List<SceneInputChange> _due;
    internal ApplySceneInputChanges(World world, bool hasChanges)
    {
        _world = world; _hasChanges = hasChanges; _due = [];
        _changes = world.Query(new QueryDescription().With<SceneInputIndex>().With<SomeEngine.ECS.Buffer<SceneInputChange>>());
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!_hasChanges || context.Substep != 0) return dependency;
        WorldJobs jobs = (WorldJobs)_world.Execution!;
        return jobs.Scheduler.Schedule(new Apply(_world, _changes, _due, context.Number),
            [.. jobs.StructureWrite, JobResourceAccess.Write(jobs.Scheduler.GetResourceToken(_due))], dependency);
    }
    private readonly struct Apply(World world, Query changes, List<SceneInputChange> due, long number) : IJob
    {
        public void Execute()
        {
            due.Clear();
            using (QueryScope rows = changes.Open())
                foreach (QueryChunk chunk in rows)
                    for (int row = 0; row < chunk.Count; row++)
                    {
                        ReadOnlySpan<SceneInputChange> values = chunk.ReadBuffer<SceneInputChange>(row).AsSpan();
                        ref SceneInputIndex index = ref chunk.Write<SceneInputIndex>(row);
                        while (index.NextChange < values.Length && values[index.NextChange].Number <= number)
                            due.Add(values[index.NextChange++]);
                    }
            using var batch = world.OpenWriteBatch();
            foreach (SceneInputChange change in due)
                if (change.Kind == SceneInputChangeKind.RemoveJoint) world.Destroy(change.Subject);
                else
                {
                    BodyPose pose = world.Read<BodyPose>(change.Subject);
                    pose.Position = change.Position; world.Set(change.Subject, pose);
                }
            due.Clear();
        }
    }
    public void Dispose() => _due.Clear();
}
