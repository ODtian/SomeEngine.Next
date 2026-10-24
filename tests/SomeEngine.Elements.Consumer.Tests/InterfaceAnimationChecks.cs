using System.Numerics;
using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Public animation behavior shared by the consumer tests and NativeAOT executable.</summary>
public static partial class InterfaceAnimationChecks
{
    private static readonly Channel<DistanceValue> Distance = Channel<DistanceValue>.Of<DistanceNode>(
        static (ref DistanceNode node) => ref node.Value);

    public static void Run()
    {
        using var world = new World();
        Entity time = world.CreateEntity(new AnimationTime(0));
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        systems.Add(new TimelineSystem(time));
        systems.Add(new TrackSystem<float, FloatOps>());
        systems.Add(new TrackSystem<Color, ColorOps>());
        systems.Add(new TrackSystem<Matrix3x2, Matrix3x2Ops>());
        systems.Add(new TrackSystem<DistanceValue, DistanceOps>());
        systems.Add(new TimelineEndSystem());
        systems.Add(new LayoutSystem());
        systems.Add(new FlipSystem());
        var target = new State<float>();
        Entity root = Views.Views.Spawn(world, canvas, new MovingSurface(target, 0));
        Frame(0);
        var children = Hierarchy<ElementTree>.GetChildren(world, root);
        Entity distance = children[0];
        Entity container = children[1];
        Entity panel = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        ElementTransform user = world.Read<ElementTransform>(panel);

        target.Value = 10;
        Frame(0);
        Frame(0.5f);
        Require(world.Read<DistanceNode>(distance).Value.Meters == 5, "The user's value operations sample the declared channel.");
        Require(world.Read<Surface>(panel).Fill == new Color(0.5f, 0, 0.5f), "The interface clock advances color while the game is paused.");
        Require(world.Read<PresentationOffset>(panel).Matrix == Matrix3x2.CreateTranslation(-50, 0), "FLIP samples the inline matrix track.");
        Require(world.Read<ElementTransform>(panel).Matrix == user.Matrix, "FLIP preserves the caller's transform.");

        world.Replace(root, new MovingSurface(target, 1));
        Frame(0.25f);
        Require(world.Read<DistanceNode>(distance).Value.Meters == 7.5f, "An equal parameter target keeps its elapsed progress.");
        Require(world.Read<Surface>(panel).Fill == new Color(0.75f, 0, 0.25f), "An equal modifier target keeps its elapsed progress.");
        Frame(0.25f);
        Require(world.Read<PresentationOffset>(panel).Matrix == Matrix3x2.Identity, "FLIP completes at identity.");

        target.Value = 0;
        Frame(0);
        Frame(0.5f);
        Require(world.Read<DistanceNode>(distance).Value.Meters == 5, "A user's channel retargets from its presented value.");
        target.Value = 10;
        Frame(0);
        Require(world.Read<DistanceNode>(distance).Value.Meters == 5, "Retargeting keeps the first presented value continuous.");
        Require(world.Read<PresentationOffset>(panel).Matrix == Matrix3x2.CreateTranslation(-50, 0), "A second layout change continues from the current presentation.");
        Frame(0.5f);
        Require(world.Read<DistanceNode>(distance).Value.Meters == 7.5f, "A user's channel continues toward the new target.");
        Frame(0.5f);
        Require(world.Read<DistanceNode>(distance).Value.Meters == 10, "A user's channel reaches its target.");

        void Frame(float delta)
        {
            world.Replace(time, new AnimationTime(delta, gamePaused: true));
            systems.Update();
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private readonly record struct DistanceValue(float Meters);

    private readonly struct DistanceOps : IValueOps<DistanceValue>
    {
        public static DistanceValue Lerp(in DistanceValue a, in DistanceValue b, float t) => new(a.Meters + (b.Meters - a.Meters) * t);
        public static DistanceValue Add(in DistanceValue a, in DistanceValue b) => new(a.Meters + b.Meters);
        public static DistanceValue Subtract(in DistanceValue a, in DistanceValue b) => new(a.Meters - b.Meters);
        public static DistanceValue Scale(in DistanceValue a, float scale) => new(a.Meters * scale);
        public static float Distance(in DistanceValue a, in DistanceValue b) => MathF.Abs(a.Meters - b.Meters);
    }

    private record struct DistanceNode : IView, IComponent
    {
        public DistanceValue Value;
        public BuildTask Build() => default;
    }

    private readonly record struct MovingSurface(State<float> Target, int Revision) : IView, IComponent
    {
        public BuildTask Build()
        {
            float value = Target.Value;
            Scope.View(new DistanceNode { Value = new DistanceValue(value) }, 1).Animate(Distance, Spec.Tween(1));
            using (Scope.View(new Absolute(), 2).Content())
            {
                Entity panel = Scope.View(new Absolute(), 3).Size(80, 20).Position(value * 10, 0)
                    .Background(new Color(value / 10, 0, 1 - value / 10)).Animate(SurfaceChannels.Fill, Spec.Tween(1));
                Scope.Write<ElementTransform>(panel) = new ElementTransform { Matrix = Matrix3x2.CreateTranslation(3, 4), Alpha = 0.8f };
                Flip.Enable(panel, Spec.Tween(1));
            }
            return default;
        }
    }
}
