using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Publishes the selected local-pose result after concrete producers and consumers finish.</summary>
public struct PoseCompositionSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _targets;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _targets = context.World.Query(context.World.QueryDefinition().ReadWrite<PoseTarget>()
            .ReadBuffer<LocalPose>().ReadBuffer<PoseResult>());

    public void OnUpdate(ref ImmediateSystemContext context) =>
        context.World.ExecuteQuery(_targets, static rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                ref PoseTarget target = ref row.ReadWrite<PoseTarget>();
                PoseResult result = row.ReadBuffer<PoseResult>()[target.Output.Index];
                if (target.Version == result.Version) continue;
                PoseRange range = target.Output.Range;
                target.Skeleton.Update(LocalPose.Transforms(row.ReadBuffer<LocalPose>().AsSpan().Slice(range.Start, range.Count)), result.Time);
                target.Version = result.Version;
            }
        });

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_targets);
}

public static partial class PoseAnimation
{
    /// <summary>Adds frame setup, clip production, caller-selected nodes, then final skeleton output.</summary>
    public static void AddSystems(SystemGroup<ImmediateSystemContext> systems,
        Action<SystemGroup<ImmediateSystemContext>>? addNodes = null)
    {
        AnimationSystems.Add(systems, new PoseFrameSystem(), AnimationPhase.Composition);
        AnimationSystems.Add(systems, new ClipSystem(), AnimationPhase.Composition);
        addNodes?.Invoke(systems);
        AnimationSystems.Add(systems, new PoseCompositionSystem(), AnimationPhase.Composition);
    }
}
