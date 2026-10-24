using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Checks;

internal static partial class CheckAnimation
{
    private static void CheckBoundWorldFields()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        Entity single = world.CreateEntity(new ConsumerFields { Style = new ConsumerStyle() });
        Entity layered = world.CreateEntity(new ConsumerFields { Style = new ConsumerStyle() });
        var channel = ConsumerFieldsChannels.Value;
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, single, channel, spec);
        PropertyAnimation.To(world, single, channel, 10f, spec);
        var layers = new AnimationLayers { Count = 2 };
        layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 0.75f };
        layers.Items[1] = new() { Mode = LayerMode.Additive, Weight = 0.5f };
        world.Add(layered, in layers);
        PropertyAnimation.SetBase(world, layered, channel, 0f);
        PropertyAnimation.Declare(world, layered, channel, spec, layer: 0);
        PropertyAnimation.Declare(world, layered, channel, spec, layer: 1);
        PropertyAnimation.To(world, layered, channel, 10f, spec, layer: 0);
        PropertyAnimation.To(world, layered, channel, 20f, spec, layer: 1);
        uint before = world.WriteVersion<ConsumerFields>(single);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, in spec);
        var context = new ImmediateSystemContext(world, 0, 0, 1);
        systems.Update(ref context);
        Require(channel.Read(world, single) == 10f && world.WriteVersion<ConsumerFields>(single) != before,
            "A checked WorldJobs world admits single-source typed field writes and publishes their root versions.");
        Require(channel.Read(world, layered) == 17.5f,
            "A checked WorldJobs world admits declared source, layer, base and final field access.");
    }
}
