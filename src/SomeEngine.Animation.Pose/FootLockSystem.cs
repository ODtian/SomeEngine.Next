using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Updates contact targets after GPU source selection and before normal Game-world extraction.</summary>
public struct FootLockSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _instances;
    private List<FootLockStep> _steps;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _steps = [];
        _instances = context.World.Query(AnimationGraph.WithTimeAccess(context.World.QueryDefinition()
            .ReadWrite<Timeline>().Enabled<Timeline>().WriteBuffer<FootLockInstance>()));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _steps.Clear();
        var gather = (World: context.World, Steps: _steps);
        context.World.ExecuteQuery(_instances, ref gather, static (QueryCursor rows, ref (World World, List<FootLockStep> Steps) gather) =>
        {
            foreach (QueryRow row in rows.Rows)
                foreach (ref FootLockInstance instance in row.Buffer<FootLockInstance>().AsSpan())
                {
                    AnimationLeafTime time = AnimationGraph.ReadTime(gather.World, row.Entity, in instance.Placement);
                    if (!time.Active) continue;
                    if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Samples.Reset(); }
                    gather.Steps.Add(new(row.Entity, instance, time));
                }
        });
        foreach (FootLockStep step in _steps)
        {
            FootLockInstance instance = step.Instance;
            FootLockSamples samples = instance.Samples;
            float elapsed = Math.Min(step.Time.Time.Time, samples.Parameters.Duration);
            GpuPoseTarget target = context.World.Read<GpuPoseTarget>(instance.Target);
            float weight = step.Time.Weight;
            if (context.World.Has<AnimationLayers>(instance.Target))
            {
                AnimationLayers layers = context.World.Read<AnimationLayers>(instance.Target);
                if (instance.Placement.Layer < layers.Count) weight *= layers.Items[instance.Placement.Layer].Weight;
            }
            samples.Update(elapsed, target.CalculationTime, context.World.Read<WorldTransform>(instance.Actor).Qvvs.ToMatrix(), weight);
            context.World.ExecuteBufferWrite<GpuPoseGoal, FootLockSamples>(instance.Target, ref samples,
                static (goals, ref samples) =>
                {
                    for (int foot = 0; foot < samples.Goals.Length; foot++)
                        goals[samples.Parameters.Rig.Legs[foot].Goal] = samples.Goals[foot];
                });
            target.ModelOffset = samples.PelvisOffset;
            context.World.Replace(instance.Target, target);
            AnimationGraph.Submit(context.World, step.Animation, in instance.Placement, elapsed < samples.Parameters.Duration);
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_instances);

    private readonly record struct FootLockStep(Entity Animation, FootLockInstance Instance, AnimationLeafTime Time);
}
