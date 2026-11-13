using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose;

/// <summary>Updates contact targets after GPU source selection and before normal Game-world extraction.</summary>
public struct FootLockSystem : ISystem<ImmediateSystemContext>
{
    private Query _instances;
    private List<FootLockStep> _steps;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _steps = [];
        _instances = context.World.Query(AnimationGraph.WithTimeAccess(new QueryDescription()
            .WithStored<Timeline>().With<Timeline>().WithStored<Buffer<FootLockInstance>>()));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _steps.Clear();
        var gather = (World: context.World, Steps: _steps);
        {
            using QueryScope read = _instances.Open();
            Visit(in read, ref gather);

            static void Visit(scoped in QueryScope rows, ref (World World, List<FootLockStep> Steps) gather)
            {
                foreach (QueryChunk chunk in rows)
                {
                    var buffers0 = chunk.Has<Buffer<FootLockInstance>>() ? chunk.GetBuffers<FootLockInstance>() : default;
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        foreach (ref FootLockInstance instance in buffers0[rowIndex].AsSpan())
                        {
                            AnimationLeafTime time = AnimationGraph.ReadTime(gather.World, chunk.Entities[rowIndex], in instance.Placement);
                            if (!time.Active) continue;
                            if (instance.Starts != time.Starts) { instance.Starts = time.Starts; instance.Samples.Reset(); }
                            gather.Steps.Add(new(chunk.Entities[rowIndex], instance, time));
                        }
                    }
                }

            }
        }
        foreach (FootLockStep step in _steps)
        {
            FootLockInstance instance = step.Instance;
            FootLockSamples samples = instance.Samples;
            float elapsed = Math.Min(step.Time.Time.Time, samples.Parameters.Duration);
            GpuPoseTarget target = context.World.ReadValue<GpuPoseTarget>(instance.Target);
            float weight = step.Time.Weight;
            if (context.World.Has<AnimationLayers>(instance.Target))
            {
                AnimationLayers layers = context.World.ReadValue<AnimationLayers>(instance.Target);
                if (instance.Placement.Layer < layers.Count) weight *= layers.Items[instance.Placement.Layer].Weight;
            }
            samples.Update(elapsed, target.CalculationTime, context.World.ReadValue<WorldTransform>(instance.Actor).Qvvs.ToMatrix(), weight,
                in instance.Source, in instance.Movement);
            {
                using var goals = context.World.GetBuffer<GpuPoseGoal>(instance.Target);
                Visit(goals, ref samples);

                static void Visit(DynamicBuffer<GpuPoseGoal> goals, ref FootLockSamples samples)
                {
                    for (int foot = 0; foot < samples.Goals.Length; foot++)
                        goals[samples.Parameters.Rig.Legs[foot].Goal] = samples.Goals[foot];

                }
            }
            target.ModelOffset = samples.PelvisOffset;
            context.World.Set(instance.Target, target);
            AnimationGraph.Submit(context.World, step.Animation, in instance.Placement, elapsed < samples.Parameters.Duration);
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }

    private readonly record struct FootLockStep(Entity Animation, FootLockInstance Instance, AnimationLeafTime Time);
}
