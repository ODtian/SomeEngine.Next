using SomeEngine.Animation;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class AsyncAnimationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CancelingSuspendedWritesRetargetsFromThePresentedColor(bool nested, bool launched)
    {
        using var frames = new InterfaceFrames();
        Entity panel = frames.World.CreateEntity(new Surface { Fill = new Color(1, 0, 0) });
        var error = new InvalidOperationException("The replaced build rejects its changes.");
        Entity root = launched ? frames.Spawn(new LaunchedSurface(panel, new Color(1, 0, 0), null, error)) : nested
            ? frames.Spawn(new FailingGroup(panel, new Color(1, 0, 0), null, error))
            : frames.Spawn(new FailingSurface(panel, new Color(1, 0, 0), null, error));
        frames.Frame(0);
        var completion = new TaskCompletionSource();
        Replace(new Color(0, 0, 1), completion.Task);
        frames.Frame(0);
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<Surface>(panel).Fill);

        Replace(new Color(0, 1, 0), null);
        frames.Frame(0);
        Assert.Equal(3, frames.World.Read<Surface>(panel).CornerRadius);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.375f, 0.5f, 0.125f), frames.World.Read<Surface>(panel).Fill);
        completion.SetResult();
        frames.Frame(0);
        Assert.Equal(new Color(0.375f, 0.5f, 0.125f), frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0, 1, 0), frames.World.Read<Surface>(panel).Fill);
        Assert.False(frames.World.HasSparse<BuildError>(root));

        void Replace(Color before, Task? wait)
        {
            if (launched) frames.World.Replace(root, new LaunchedSurface(panel, before, wait, error));
            else if (nested) frames.World.Replace(root, new FailingGroup(panel, before, wait, error));
            else frames.World.Replace(root, new FailingSurface(panel, before, wait, error));
        }
    }

    [Fact]
    public void LaunchedWritesCommitEachResumedSegmentAndRestoreAfterFailure()
    {
        using var frames = new InterfaceFrames();
        Entity panel = frames.World.CreateEntity(new Surface { Fill = new Color(1, 0, 0) });
        var error = new InvalidOperationException("The launched view rejects its changes.");
        Entity root = frames.Spawn(new LaunchedSurface(panel, new Color(1, 0, 0), null, error));
        frames.Frame(0);
        var completion = new TaskCompletionSource();
        frames.World.Replace(root, new LaunchedSurface(panel, new Color(0, 0, 1), completion.Task, error));
        frames.Frame(0);
        frames.Frame(0.25f);
        Assert.Equal(new Color(0.75f, 0, 0.25f), frames.World.Read<Surface>(panel).Fill);

        completion.SetResult();
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.375f, 0.5f, 0.125f), frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0);
        Assert.Same(error, frames.World.ReadSparse<BuildError>(root).Exception);
        Assert.Equal(3, frames.World.Read<Surface>(panel).CornerRadius);
        Assert.Equal(new Color(0.375f, 0.5f, 0.125f), frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(0.6875f, 0.25f, 0.0625f), frames.World.Read<Surface>(panel).Fill);
        frames.Frame(0.5f);
        Assert.Equal(new Color(1, 0, 0), frames.World.Read<Surface>(panel).Fill);
    }

    private readonly record struct LaunchedSurface(Entity Panel, Color Before, Task? Wait, Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Launch(this, static async (value, cancel) =>
            {
                value.Panel.Background(value.Before).Corner(value.Wait is null ? 3 : 7)
                    .Animate(SurfaceChannels.Fill, Spec.Tween(1));
                if (value.Wait is null) return;
                await value.Wait;
                cancel.ThrowIfCancellationRequested();
                value.Panel.Background(new Color(0, 1, 0)).Corner(9);
                await Scope.NextFrame();
                throw value.Error;
            }, 1);
            return default;
        }
    }
}
