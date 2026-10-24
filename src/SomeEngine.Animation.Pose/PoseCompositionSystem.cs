using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Publishes the selected local-pose result after concrete producers and consumers finish.</summary>
public struct PoseCompositionSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _targets;
    private double _time;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _targets = context.World.Query(context.World.QueryDefinition().ReadWrite<PoseTarget>()
            .ReadBuffer<LocalPose>().ReadBuffer<PoseResult>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _time += context.DeltaTime;
        context.World.ExecuteQuery(_targets, ref _time, static (QueryCursor rows, ref double time) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                ref PoseTarget target = ref row.ReadWrite<PoseTarget>();
                PoseResult result = row.ReadBuffer<PoseResult>()[target.Output.Index];
                if (target.Version == result.Version) continue;
                PoseRange range = target.Output.Range;
                target.Skeleton.SetLocalPose(LocalPose.Transforms(row.ReadBuffer<LocalPose>().AsSpan().Slice(range.Start, range.Count)), result.Time);
                target.Version = result.Version;
            }
            foreach (QueryRow row in rows.Rows) row.Read<PoseTarget>().Skeleton.Compose(time);
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_targets);
}

public static partial class PoseAnimation
{
    /// <summary>Adds frame setup, clip production, frame producers, dependency-ordered nodes, then skeleton output.</summary>
    public static void AddSystems(SystemGroup<ImmediateSystemContext> systems,
        Action<SystemGroup<ImmediateSystemContext>>? addNodes = null,
        Action<SystemGroup<PoseNodeContext>>? nodes = null)
    {
        AnimationSystems.Add(systems, new PoseFrameSystem(), AnimationPhase.Composition);
        AnimationSystems.Add(systems, new ClipSystem(), AnimationPhase.Composition);
        addNodes?.Invoke(systems);
        if (nodes is not null) AnimationSystems.Add(systems, new PoseNodeSystem(nodes), AnimationPhase.Composition);
        AnimationSystems.Add(systems, new PoseCompositionSystem(), AnimationPhase.Composition);
    }
}
