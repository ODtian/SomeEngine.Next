using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

public static class AnimationSystems
{
    /// <summary>Adds the timeline clock, built-in property evaluators and composers, and timeline completion in order.</summary>
    public static void AddDefaults(SystemGroup<ImmediateSystemContext> schedule, Entity time = default)
    {
        schedule.Add(new TimelineSystem(time));
        schedule.Add(new TrackSystem<float, FloatOps>(TrackFields.LayerWeights));
        schedule.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
        schedule.Add(new TrackSystem<float, FloatOps>());
        schedule.Add(new TrackSystem<Vector2, Vector2Ops>());
        schedule.Add(new TrackSystem<Vector3, Vector3Ops>());
        schedule.Add(new TrackSystem<Vector4, Vector4Ops>());
        schedule.Add(new TrackSystem<Quaternion, QuaternionOps>());
        schedule.Add(new TrackSystem<Color, ColorOps>());
        schedule.Add(new TrackSystem<Thickness, ThicknessOps>());
        schedule.Add(new TrackSystem<Rect, RectOps>());
        schedule.Add(new TrackSystem<Matrix3x2, Matrix3x2Ops>());
        schedule.Add(new ComposeSystem<float, FloatOps>());
        schedule.Add(new ComposeSystem<Vector2, Vector2Ops>());
        schedule.Add(new ComposeSystem<Vector3, Vector3Ops>());
        schedule.Add(new ComposeSystem<Vector4, Vector4Ops>());
        schedule.Add(new ComposeSystem<Quaternion, QuaternionOps>());
        schedule.Add(new ComposeSystem<Color, ColorOps>());
        schedule.Add(new ComposeSystem<Thickness, ThicknessOps>());
        schedule.Add(new ComposeSystem<Rect, RectOps>());
        schedule.Add(new ComposeSystem<Matrix3x2, Matrix3x2Ops>());
        schedule.Add(new TimelineEndSystem());
    }
}
