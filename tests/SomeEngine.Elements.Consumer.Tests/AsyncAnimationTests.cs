using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class AsyncAnimationTests
{
    private static readonly Channel<Color> ParameterFill = Channel<Color>.Of<ColorLabel>(static (ref ColorLabel label) => ref label.Fill);

    [Fact]
    public void FailedAsyncParentKeepsPresentedParametersContinuous()
    {
        using var frames = new InterfaceFrames();
        var error = new InvalidOperationException("The parent rejects its animated parameter.");
        Entity root = frames.Spawn(new ParameterParent(new Color(1, 0, 0), null, error));
        frames.Frame(0);
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        var completion = new TaskCompletionSource();
        frames.World.Replace(root, new ParameterParent(new Color(0, 0, 1), completion.Task, error));
        frames.Frame(0);
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<ColorLabel>(label).Fill);
        completion.SetResult();
        frames.Frame(0);
        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<ColorLabel>(label).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.875f, 0, 0.125f), frames.World.Read<ColorLabel>(label).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(1, 0, 0), frames.World.Read<ColorLabel>(label).Fill);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CrossFrameFailureRestoresThePreviouslyCompletedTarget(bool nested, bool computed)
    {
        using var frames = new InterfaceFrames();
        Entity panel = frames.World.CreateEntity(new Surface { Fill = new Color(1, 0, 0) });
        var error = new InvalidOperationException("The resumed view rejects its changes.");
        Entity root = computed ? frames.Spawn(new ComputedGroup(panel, new Color(1, 0, 0), null, error)) : nested
            ? frames.Spawn(new FailingGroup(panel, new Color(1, 0, 0), null, error))
            : frames.Spawn(new FailingSurface(panel, new Color(1, 0, 0), null, error));
        frames.Frame(0);
        var completion = new TaskCompletionSource();
        if (computed) frames.World.Replace(root, new ComputedGroup(panel, new Color(0, 0, 1), completion.Task, error));
        else if (nested) frames.World.Replace(root, new FailingGroup(panel, new Color(0, 0, 1), completion.Task, error));
        else frames.World.Replace(root, new FailingSurface(panel, new Color(0, 0, 1), completion.Task, error));
        frames.Frame(0);
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<Surface>(panel).Fill);

        completion.SetResult();
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.375f, 0.5f, 0.125f), frames.World.Read<Surface>(panel).Fill);
        uint surfaceVersion = frames.World.WriteVersion<Surface>(panel);
        frames.Frame(0);
        Entity content = nested ? Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)) : root;
        Assert.Same(error, frames.World.ReadSparse<BuildError>(content).Exception);
        Assert.Equal(new Color(0.375f, 0.5f, 0.125f), frames.World.Read<Surface>(panel).Fill);
        if (!computed)
        {
            Assert.Equal(3, frames.World.Read<Surface>(panel).CornerRadius);
            Assert.NotEqual(surfaceVersion, frames.World.WriteVersion<Surface>(panel));
        }
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.6875f, 0.25f, 0.0625f), frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(1, 0, 0), frames.World.Read<Surface>(panel).Fill);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuspendedAndResumedWritesContinueFromThePresentedColor(bool sameTarget)
    {
        using var frames = new InterfaceFrames();
        Entity panel = frames.World.CreateEntity(new Surface { Fill = new Color(1, 0, 0) });
        Entity root = frames.Spawn(new SegmentedSurface(panel, new Color(1, 0, 0), default, null));
        frames.Frame(0);
        var completion = new TaskCompletionSource();
        Color after = sameTarget ? new Color(0, 0, 1) : new Color(0, 1, 0);
        frames.World.Replace(root, new SegmentedSurface(panel, new Color(0, 0, 1), after, completion.Task));

        frames.Frame(0);
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<Surface>(panel).Fill);
        completion.SetResult();
        frames.Frame(0);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(sameTarget ? new Color(0.25f, 0, 0.75f) : new Color(0.375f, 0.5f, 0.125f),
            frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(after, frames.World.Read<Surface>(panel).Fill);
    }

    private readonly record struct SegmentedSurface(Entity Panel, Color Before, Color After, Task? Wait) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Panel.Background(Before).Corner(Wait is null ? 3 : 7).Animate(SurfaceChannels.Fill, Spec.Tween(1));
            if (Wait is null) return;
            await Wait;
            Panel.Background(After);
        }
    }

    private readonly record struct FailingSurface(Entity Panel, Color Before, Task? Wait, Exception Error) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Panel.Background(Before).Corner(Wait is null ? 3 : 7).Animate(SurfaceChannels.Fill, Spec.Tween(1));
            if (Wait is null) return;
            await Wait;
            Panel.Background(new Color(0, 1, 0));
            await Scope.NextFrame();
            throw Error;
        }
    }

    private readonly record struct FailingGroup(Entity Panel, Color Before, Task? Wait, Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new FailingSurface(Panel, Before, Wait, Error), 1);
            return default;
        }
    }

    private record struct ColorLabel : IView, IComponent
    {
        public Color Fill;
        public BuildTask Build() => default;
    }

    private readonly record struct ParameterParent(Color Fill, Task? Wait, Exception Error) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.View(new ColorLabel { Fill = Fill }, 1).Animate(ParameterFill, Spec.Tween(1));
            if (Wait is null) return;
            await Wait;
            throw Error;
        }
    }

    private readonly record struct ComputedSurface(Entity Panel, Color Before, Task? Wait, Exception Error) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Panel.Animate(SurfaceChannels.Fill, Spec.Tween(1));
            Entity panel = Panel;
            Color before = Before;
            Scope.Compute(() => panel.Background(before));
            if (Wait is null) return;
            await Wait;
            Panel.Background(new Color(0, 1, 0));
            await Scope.NextFrame();
            throw Error;
        }
    }

    private readonly record struct ComputedGroup(Entity Panel, Color Before, Task? Wait, Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new ComputedSurface(Panel, Before, Wait, Error), 1);
            return default;
        }
    }
}
