using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Advances state and blend sample phases after all value evaluators have run.</summary>
public struct GraphEndSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _graphs;

    public void OnCreate(ref ImmediateSystemContext context) =>
        _graphs = context.World.Query(context.World.QueryDefinition().Read<GraphInstance>().ReadWrite<Timeline>()
            .Enabled<Timeline>().OptionalBuffer<GraphMotion>(QueryAccess.ReadWrite));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        world.ExecuteQuery(_graphs, context.LastSystemVersion, ref world, static (QueryCursor rows, ref World world) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GraphInstance instance = row.Read<GraphInstance>();
                if (instance.Data is null || instance.TimeSource == default || !world.IsAlive(instance.TimeSource))
                    continue;
                Timeline time = world.Read<Timeline>(instance.TimeSource);
                time.Running |= row.Read<Timeline>().Running;
                world.Replace(instance.TimeSource, time);
                if (instance.Data[0].GroupCount > 0)
                {
                    BufferView<GraphMotion> motions = row.ReadBuffer<GraphMotion>();
                    GraphMotion[] values = ArrayPool<GraphMotion>.Shared.Rent(motions.Count);
                    try
                    {
                        motions.AsSpan().CopyTo(values);
                        var source = (Values: values, Count: motions.Count);
                        world.ExecuteBufferWrite<GraphMotion, (GraphMotion[] Values, int Count)>(instance.TimeSource, ref source,
                            static (DynamicBuffer<GraphMotion> destination, ref (GraphMotion[] Values, int Count) source) =>
                            {
                                for (int i = 0; i < source.Count; i++)
                                    destination[i].Running |= source.Values[i].Running;
                            });
                    }
                    finally { ArrayPool<GraphMotion>.Shared.Return(values); }
                }
            }
        });
        world.ExecuteQuery(_graphs, context.LastSystemVersion, static rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GraphInstance instance = row.Read<GraphInstance>();
                if (instance.Data is null || instance.TimeSource != default || instance.Data[0].GroupCount == 0)
                    continue;
                ref GraphData data = ref instance.Data[0];
                DynamicBuffer<GraphMotion> motions = row.Buffer<GraphMotion>();
                for (int i = motions.Count - 1; i >= 0; i--)
                {
                    ref GraphMotion motion = ref motions[i];
                    if (motion.Weight <= 0)
                        continue;
                    GraphMotionData source = data.Motions![i];
                    var timeline = new Timeline
                    {
                        Time = motion.Time, Stage = motion.Stage, Paused = row.Read<Timeline>().Paused,
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
                            row.ReadWrite<Timeline>().Running |= 2;
                        else
                            motions[group.ParentMotion - 1].Running = true;
                    }
                }
            }
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_graphs);
}
