using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.Physics;

namespace DefaultGame;

public readonly struct ScenePhysicsEventStream : IComponent;

internal readonly struct SceneEventComponents : IPhysicsEventComponents
{
    public void Add(ref SomeEngine.ECS.Systems.JobCommandWriter commands, DeferredEntity events) =>
        commands.Add(events, new ScenePhysicsEventStream());
}

internal readonly struct AcknowledgeSceneEvents : IPhysicsEventConsumer
{
    public void Consume(scoped in PhysicsStepEvents step, scoped ReadOnlySpan<PhysicsEvent> events,
        ref SomeEngine.ECS.Systems.JobCommandWriter commands) { }
}
