using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class TextEditingTests
{
#if DEBUG
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CharacterInputRequiresTextAndAWorld(bool composition)
    {
        using var world = new World();
        var textError = Assert.Throws<InvalidOperationException>(() =>
        {
            if (composition)
                Keyboard.Composition(world, null!, 0);
            else
                Keyboard.Text(world, null!);
        });
        Assert.Contains("text", textError.Message);
        var worldError = Assert.Throws<InvalidOperationException>(() =>
        {
            if (composition)
                Keyboard.Composition(null!, "a", 0);
            else
                Keyboard.Text(null!, "a");
        });
        Assert.Contains("World", worldError.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void CompositionRequiresACaretInsideItsText(int caret)
    {
        using var world = new World();
        var error = Assert.Throws<InvalidOperationException>(() => Keyboard.Composition(world, "a", caret));
        Assert.Contains("caret", error.Message);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 4)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void EditableTextRequiresCompleteTextElementPositions(int caret, int selectionStart)
    {
        using var world = new World();
        Entity node = world.CreateEntity(new EditableText { Text = "😀a", Caret = caret, SelectionStart = selectionStart });
        world.Add(node, new Focusable());
        world.AddSparse(node, new Focused());
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new FocusNavigationSystem());
        Keyboard.Key(world, Key.Home, true);

        var error = Assert.Throws<InvalidOperationException>(() => systems.Update());
        Assert.Contains("EditableText", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingHelpersRequireAnActiveBuild(bool read)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (read)
                Entity.Null.Edited(out _);
            else
                TextEditingFunctions.TextEditing(Entity.Null);
        });
        Assert.Contains("active view build", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingSystemRequiresAWorld(bool cancellation)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (cancellation)
                TextEditingSystem.CancelComposition(null!, Entity.Null);
            else
                TextEditingSystem.HandleInput(null!, Entity.Null, default);
        });
        Assert.Contains("World", error.Message);
    }
#endif
}
