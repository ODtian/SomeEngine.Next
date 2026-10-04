using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Views;

namespace SomeEngine.Views.AotSmoke;

internal static partial class AsyncAnimationChecks
{
    private static readonly Channel<Color> Fill = Channel<Color>.Of<Paint>(static (ref Paint paint) => ref paint.Fill);

    internal static void Run()
    {
        using var world = new World();
        Entity time = world.CreateEntity(new AnimationTime(0));
        Entity target = world.CreateEntity(new Paint { Fill = new Color(1, 0, 0) });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        AnimationSystems.AddDefaults(systems, time);
        var error = new InvalidOperationException("The resumed animation is rejected.");
        Entity root = global::SomeEngine.Views.Views.Spawn(world, Entity.Null, new PaintGroup(target, new Color(1, 0, 0), null, error));
        Frame(0);
        var ready = new TaskCompletionSource();
        world.Replace(root, new PaintGroup(target, new Color(0, 0, 1), ready.Task, error));
        Frame(0);
        Frame(0.25f);
        Require(world.Read<Paint>(target).Fill == new Color(0.75f, 0, 0.25f), "The suspended child commits its target.");
        ready.SetResult();
        Frame(0.5f);
        Require(world.Read<Paint>(target).Fill == new Color(0.375f, 0.5f, 0.125f), "The resumed child changes its target from the presented color.");
        Frame(0);
        Require(world.Read<Paint>(target).Fill == new Color(0.375f, 0.5f, 0.125f), "Failure keeps the presented color continuous.");
        Frame(0.5f);
        Require(world.Read<Paint>(target).Fill == new Color(0.6875f, 0.25f, 0.0625f), "Recovery restores the earlier complete target.");
        Frame(0.5f);
        Require(world.Read<Paint>(target).Fill == new Color(1, 0, 0), "Recovery reaches the earlier complete target.");

        Entity launched = global::SomeEngine.Views.Views.Spawn(world, Entity.Null,
            new LaunchedPaint(target, new Color(1, 0, 0), null, error));
        Frame(0);
        var launchedReady = new TaskCompletionSource();
        world.Replace(launched, new LaunchedPaint(target, new Color(0, 0, 1), launchedReady.Task, error));
        Frame(0);
        Frame(0.25f);
        Require(world.Read<Paint>(target).Fill == new Color(0.75f, 0, 0.25f), "Launch commits its suspended target.");
        launchedReady.SetResult();
        Frame(0.5f);
        Require(world.Read<Paint>(target).Fill == new Color(0.375f, 0.5f, 0.125f), "Launch commits its resumed target.");
        Frame(0);
        Require(ReferenceEquals(world.ReadSparse<BuildError>(launched).Exception, error), "Launch reports the resumed failure.");
        Frame(1);
        Require(world.Read<Paint>(target).Fill == new Color(1, 0, 0), "Launch restores the completed target.");

        var canceledReady = new TaskCompletionSource();
        world.Replace(launched, new LaunchedPaint(target, new Color(0, 0, 1), canceledReady.Task, error));
        Frame(0);
        Frame(0.25f);
        world.Replace(launched, new LaunchedPaint(target, new Color(0, 1, 0), null, error));
        Frame(0);
        Require(world.Read<Paint>(target).Fill == new Color(0.75f, 0, 0.25f), "Launch cancellation keeps the presented color.");
        canceledReady.SetResult();
        Frame(0.5f);
        Require(world.Read<Paint>(target).Fill == new Color(0.375f, 0.5f, 0.125f), "The replacement keeps its target after old-task cleanup.");
        Frame(0.5f);
        Require(world.Read<Paint>(target).Fill == new Color(0, 1, 0), "The replacement reaches its target.");

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

    private struct Paint : IComponent
    {
        public Color Fill;
    }

    private readonly record struct PaintView(Entity Target, Color Before, Task? Ready, Exception Error) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Target.Animate(Fill, Spec.Tween(1));
            Entity target = Target;
            Color before = Before;
            Scope.Compute(() => Scope.Write<Paint>(target).Fill = before);
            if (Ready is null) return;
            await Ready;
            Scope.Write<Paint>(Target).Fill = new Color(0, 1, 0);
            await Scope.NextFrame();
            throw Error;
        }
    }

    private readonly record struct PaintGroup(Entity Target, Color Before, Task? Ready, Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new PaintView(Target, Before, Ready, Error), 1);
            return default;
        }
    }

    private readonly record struct LaunchedPaint(Entity Target, Color Before, Task? Ready, Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Launch(this, static async (value, cancel) =>
            {
                value.Target.Animate(Fill, Spec.Tween(1));
                Scope.Write<Paint>(value.Target).Fill = value.Before;
                if (value.Ready is null) return;
                await value.Ready;
                cancel.ThrowIfCancellationRequested();
                Scope.Write<Paint>(value.Target).Fill = new Color(0, 1, 0);
                await Scope.NextFrame();
                throw value.Error;
            }, 1);
            return default;
        }
    }
}
