using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static class Animation
{
    /// <summary>Increments the event counter on an instance with a declared event buffer.</summary>
    public static void EmitEvent(World world, Entity entity, ushort id) =>
        world.ExecuteBufferWrite<TimelineEvent, ushort>(entity, ref id,
            static (DynamicBuffer<TimelineEvent> events, ref ushort id) =>
            {
                for (int i = 0; i < events.Count; i++)
                {
                    if (events.Read(i).Id != id)
                        continue;
                    events[i].Count++;
                    return;
                }
                events.Add(new TimelineEvent { Id = id, Count = 1 });
            });

    /// <summary>Enters the stage following the current external boundary.</summary>
    public static void Advance(World world, Entity entity)
    {
        DevelopmentCheck.Require(world.IsEnabled<Timeline>(entity), "External advancement requires an enabled timeline.");
        Timeline timeline = world.Read<Timeline>(entity);
        world.ExecuteBufferRead<TimelineStage, Timeline>(entity, ref timeline,
            static (BufferView<TimelineStage> stages, ref Timeline timeline) =>
            {
                DevelopmentCheck.Require(timeline.Stage < stages.Count, "External advancement requires a current stage.");
                DevelopmentCheck.Require(stages[timeline.Stage].End == StageEnd.External, "Advance requires an External boundary.");
                timeline.Stage++;
                timeline.Time = 0;
            });
        world.Replace(entity, timeline);
    }
}
