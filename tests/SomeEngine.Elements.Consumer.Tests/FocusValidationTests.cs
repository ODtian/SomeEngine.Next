using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class FocusTests
{
#if DEBUG
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InputRequiresSupportedKeysAndDirections(bool keyboard)
    {
        using var world = new World();
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (keyboard)
                Keyboard.Key(world, (Key)255, true);
            else
                Gamepad.Navigate(world, (Direction)255);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InputRequiresAWorld(int input)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (input)
            {
                case 0: Keyboard.Key(null!, Key.Left, true); break;
                case 1: Gamepad.Navigate(null!, Direction.Right); break;
                case 2: Gamepad.Confirm(null!); break;
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FocusHelpersRequireAnActiveViewBuild(bool read)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (read)
                Entity.Null.Focused();
            else
                FocusFunctions.Focusable(Entity.Null);
        });
    }

    [Fact]
    public void InputRequiresOneCaptureEntity()
    {
        using var world = new World();
        world.CreateEntity(new InputCapture());
        world.CreateEntity(new InputCapture());
        Assert.Throws<InvalidOperationException>(() => Gamepad.Confirm(world));
    }

    [Fact]
    public void NavigationRequiresOneFocusedEntity()
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusChecksForValidation());
        frames.Frame();
        var nodes = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        frames.World.AddSparse(nodes[0], new Focused());
        frames.World.AddSparse(nodes[1], new Focused());
        Assert.Throws<InvalidOperationException>(frames.Frame);
    }

    private readonly record struct FocusChecksForValidation() : IView, IComponent
    {
        public BuildTask Build()
        {
            FocusFunctions.Focusable(Scope.View(new Group()));
            FocusFunctions.Focusable(Scope.View(new Group()));
            return default;
        }
    }
#endif
}
