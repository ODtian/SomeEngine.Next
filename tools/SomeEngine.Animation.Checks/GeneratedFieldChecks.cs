using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks;

internal static partial class CheckAnimation
{
    private static void CheckGeneratedFieldPlayback()
    {
        using var world = new World();
        var style = new ConsumerStyle();
        Entity target = world.CreateEntity(new ConsumerFields { Style = style });
        var spec = Spec.Tween<float, FloatOps, Linear>(1);
        PropertyAnimation.Declare(world, target, ConsumerFieldsChannels.Value, spec);
        PropertyAnimation.Declare(world, target, ConsumerFieldsChannels.Width, spec);
        PropertyAnimation.Declare(world, target, ConsumerFieldsChannels.Radius, spec);
        PropertyAnimation.Declare(world, target, ConsumerFieldsChannels.Height, spec);
        PropertyAnimation.To(world, target, ConsumerFieldsChannels.Value, 1f, spec);
        PropertyAnimation.To(world, target, ConsumerFieldsChannels.Width, 2f, spec);
        PropertyAnimation.To(world, target, ConsumerFieldsChannels.Radius, 3f, spec);
        PropertyAnimation.To(world, target, ConsumerFieldsChannels.Height, 4f, spec);
        uint before = world.WriteVersion<ConsumerFields>(target);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>());
        systems.Add(new TimelineEndSystem());
        var context = new ImmediateSystemContext(world, 0, 0, 1);
        systems.Update(ref context);
        ConsumerFields result = world.Read<ConsumerFields>(target);
        Require((result.Value, result.Inner.Width, style.Corner.Radius, result.Height) == (1f, 2f, 3f, 4f),
            "Generated ordinary, value-nested, reference-nested and property channels share typed playback.");
        Require(result.Style == style && world.WriteVersion<ConsumerFields>(target) != before,
            "Generated reference paths keep the current object and advance the root write version.");
    }
}

[GenerateFields]
[ComponentField("Inner.Width", Name = "Width")]
[ComponentField("Style.Corner.Radius", Name = "Radius")]
internal struct ConsumerFields : IComponent
{
    public float Value;
    public ConsumerInner Inner { get; set; }
    public ConsumerStyle Style;
    public float Height { get; set; }
}

internal struct ConsumerInner { public float Width { get; set; } }
internal struct ConsumerCorner { public float Radius { get; set; } }
internal sealed class ConsumerStyle { public ConsumerCorner Corner { get; set; } }
