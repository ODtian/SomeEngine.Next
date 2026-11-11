using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Advances state and blend sample phases after all value evaluators have run.</summary>
public struct GraphEndSystem : ISystem<ImmediateSystemContext>
{
    private Query _graphs;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _graphs = context.World.Query(new QueryDescription().WithStored<GraphInstance>().WithStored<Timeline>()
            .With<Timeline>().Optional<Buffer<GraphMotion>>());

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        {
            using QueryScope read = _graphs.Since(context.LastSystemVersion).Open();
            Visit(in read, ref world);

            static void Visit(scoped in QueryScope rows, ref World world)
            {
                foreach (QueryChunk chunk in rows)
                {
                    chunk.TryRead<GraphInstance>(out var values0);
                    chunk.TryRead<Timeline>(out var values1);
                    for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                    {
                        GraphInstance instance = values0[rowIndex];
                        if (instance.Data is null || instance.TimeSource == default || !world.IsAlive(instance.TimeSource))
                            continue;
                        Timeline time = world.ReadComponentData<Timeline>(instance.TimeSource);
                        time.Running |= values1[rowIndex].Running;
                        world.Set(instance.TimeSource, time);
                        if (instance.Data[0].GroupCount > 0)
                        {
                            ReadOnlyBuffer<GraphMotion> motions = chunk.ReadBuffer<GraphMotion>(rowIndex);
                            GraphMotion[] values = ArrayPool<GraphMotion>.Shared.Rent(motions.Count);
                            try
                            {
                                motions.AsSpan().CopyTo(values);
                                var source = (Values: values, Count: motions.Count);
                                {
                                    using var destination = world.GetBuffer<GraphMotion>(instance.TimeSource);
                                    for (int i = 0; i < source.Count; i++)
                                        destination[i].Running |= source.Values[i].Running;
                                }
                            }
                            finally { ArrayPool<GraphMotion>.Shared.Return(values); }
                        }

                    }
                }

            }
        }
        {
            using QueryScope rows = _graphs.Since(context.LastSystemVersion).Open();
            foreach (QueryChunk chunk in rows)
            {
                var values0 = chunk.Has<Timeline>() ? chunk.Write<Timeline>() : default;
                chunk.TryRead<GraphInstance>(out var values1);
                var buffers0 = chunk.Has<Buffer<GraphMotion>>() ? chunk.GetBuffers<GraphMotion>() : default;
                for (int rowIndex = 0; rowIndex < chunk.Count; rowIndex++)
                {
                    GraphInstance instance = values1[rowIndex];
                    if (instance.Data is null || instance.TimeSource != default || instance.Data[0].GroupCount == 0)
                        continue;
                    ref GraphData data = ref instance.Data[0];
                    DynamicBuffer<GraphMotion> motions = buffers0[rowIndex];
                    for (int i = motions.Count - 1; i >= 0; i--)
                    {
                        ref GraphMotion motion = ref motions[i];
                        if (motion.Weight <= 0)
                            continue;
                        GraphMotionData source = data.Motions![i];
                        var timeline = new Timeline
                        {
                            Time = motion.Time,
                            Stage = motion.Stage,
                            Paused = values0[rowIndex].Paused,
                            Running = motion.Running ? 1u : 0,
                        };
                        TimelineEndSystem.ContinueStages(ref timeline, source.Stages.AsSpan(0, source.StageCount));
                        motion.Stage = timeline.Stage;
                        motion.Time = timeline.Time;
                        GraphGroupData group = data.Groups![source.Group];
                        if (data.Nodes![group.Node].Kind is GraphNodeKind.Parallel or GraphNodeKind.Stagger &&
                            motion.Stage < source.StageCount)
                        {
                            if (group.ParentMotion == 0)
                                values0[rowIndex].Running |= 2;
                            else
                                motions[group.ParentMotion - 1].Running = true;
                        }
                    }

                }
            }

        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
