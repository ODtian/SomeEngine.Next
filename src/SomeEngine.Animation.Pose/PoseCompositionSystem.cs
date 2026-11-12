using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Publishes the selected local-pose result after concrete producers and consumers finish.</summary>
public struct PoseCompositionSystem : ISystem<ImmediateSystemContext>
{
    private Query _targets;
    private double _time;
    private SkeletonLevels _models;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _models = new();
        _targets = context.World.Query(new QueryDescription().WithStored<PoseTarget>()
            .WithStored<Buffer<LocalPose>>().WithStored<Buffer<PoseResult>>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _time += context.DeltaTime;
        Compose(context.World, _time);
    }

    internal void Compose(World world, double time)
    {
        CopyInputs(world);
        ComposeModels(world, time);
    }

    internal void CopyInputs(World world)
    {
        {
            using QueryScope rows = _targets.Open();
            foreach (QueryChunk chunk in rows)
            {
                var values0 = chunk.Has<PoseTarget>() ? chunk.Write<PoseTarget>() : default;
                for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                {

                    ref PoseTarget target = ref values0[rowIndex];
                    if (target.Skeleton.CpuWrittenBones.Length != 0) continue;
                    PoseResult result = chunk.ReadBuffer<PoseResult>(rowIndex)[target.Output.Index];
                    if (target.Version == result.Version) continue;
                    PoseRange range = target.Output.Range;
                    target.Skeleton.SetLocalPose(LocalPose.Transforms(chunk.ReadBuffer<LocalPose>(rowIndex).AsSpan().Slice(range.Start, range.Count)), result.Time);
                    target.Version = result.Version;

                }
            }

        }
    }

    internal void ComposeModels(World world, double time)
    {
        var frame = (World: world, Time: time, Models: _models);
        {
            using QueryScope read = _targets.Open();
            Visit(in read, ref frame);

            static void Visit(scoped in QueryScope rows, ref (World World, double Time, SkeletonLevels Models) frame)
            {
                foreach (QueryChunk chunk in rows)
                {
                    chunk.TryRead<PoseTarget>(out var values0);
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {

                        SkeletonInstance skeleton = values0[rowIndex].Skeleton;
                        if (skeleton.CpuWrittenBones.Length == 0) frame.Models.Add(skeleton);

                    }
                }
                frame.Models.Compose(PoseAnimation.GetScheduler(frame.World), frame.Time);

            }
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {

        _models.Clear();
    }
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
