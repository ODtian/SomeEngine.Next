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
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (composition)
                Keyboard.Composition(world, null!, 0);
            else
                Keyboard.Text(world, null!);
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (composition)
                Keyboard.Composition(null!, "a", 0);
            else
                Keyboard.Text(null!, "a");
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void CompositionRequiresACaretInsideItsText(int caret)
    {
        using var world = new World();
        Assert.Throws<InvalidOperationException>(() => Keyboard.Composition(world, "a", caret));
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

        Assert.Throws<InvalidOperationException>(() => systems.Update());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingHelpersRequireAnActiveBuild(bool read)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (read)
                Entity.Null.Edited(out string _);
            else
                TextEditingFunctions.TextEditing(Entity.Null);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingSystemRequiresAWorld(bool cancellation)
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (cancellation)
                TextEditingSystem.CancelComposition(null!, Entity.Null);
            else
                TextEditingSystem.HandleInput(null!, Entity.Null, default);
        });
    }
#endif
}
