using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static class Animation
{
    /// <summary>Increments the event counter on an instance with a declared event buffer.</summary>
    public static void EmitEvent(World world, Entity entity, ushort id) => EmitEvent(world, entity, id, 1);

    /// <summary>Adds every marker crossed by an evaluation interval, including repeated clip cycles.</summary>
    public static void EmitEvent(World world, Entity entity, ushort id, uint count)
    {
        var marker = (Id: id, Count: count);
        world.ExecuteBufferWrite<TimelineEvent, (ushort Id, uint Count)>(entity, ref marker,
            static (DynamicBuffer<TimelineEvent> events, ref (ushort Id, uint Count) marker) =>
            {
                for (int i = 0; i < events.Count; i++)
                {
                    if (events.Read(i).Id != marker.Id)
                        continue;
                    events[i].Count += marker.Count;
                    return;
                }
                events.Add(new TimelineEvent { Id = marker.Id, Count = marker.Count });
            });
    }

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
