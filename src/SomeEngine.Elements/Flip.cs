using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public static class Flip
{
    /// <summary>Enables same-entity layout transitions during the current build.</summary>
    public static void Enable(Entity node, Spec spec) => Enable(Scope.World, node, spec);

    /// <summary>Declares the presentation data and inline tracks used after layout.</summary>
    public static void Enable(World world, Entity node, Spec spec)
    {
        if (!world.Has<PresentationOffset>(node))
            world.Add(node, PresentationOffset.Identity);
        PropertyAnimation.Declare<Matrix3x2>(world, node);
        PropertyAnimation.Declare<float>(world, node);
        Timeline timeline = world.Read<Timeline>(node);
        if (timeline.Clock != TimelineClock.Interface)
        {
            timeline.Clock = TimelineClock.Interface;
            world.Replace(node, timeline);
        }
        var settings = new FlipSettings { Spec = spec };
        if (world.Has<FlipSettings>(node)) world.Replace(node, settings);
        else world.Add(node, settings);
        if (!world.Has<FlipFrame>(node)) world.Add(node, new FlipFrame());
    }
}

/// <summary>The previous frame's layout and caller transform for this entity.</summary>
internal struct FlipFrame : IComponent
{
    internal Rect Rectangle;
    internal Matrix3x2 Transform;
    internal float Alpha;
    internal bool HasFrame;
}

/// <summary>The caller's motion settings for this entity's layout transitions.</summary>
internal struct FlipSettings : IComponent
{
    internal Spec Spec;
}
