using System.Globalization;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class AccessibilityTests
{
    [Fact]
    public void EqualAccessibilityValuesPreserveTheirPublicWriteVersions()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity root = Views.Views.Spawn(world, Entity.Null, new UserControl(Role.Button, "Inventory"));
        systems.Update();
        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root));
        uint roleVersion = world.WriteVersion<AccessibleRole>(node);
        uint nameVersion = world.WriteVersion<AccessibleName>(node);

        world.Replace(root, new UserControl(Role.Button, new string("Inventory".ToCharArray())));
        systems.Update();

        Assert.Equal("Inventory", world.Read<AccessibleName>(node).Value);
        Assert.Equal(roleVersion, world.WriteVersion<AccessibleRole>(node));
        Assert.Equal(nameVersion, world.WriteVersion<AccessibleName>(node));
    }

    [Fact]
    public void FormattedTextNamesFollowTheFormattedContent()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity root = Views.Views.Spawn(world, Entity.Null, new FormattedLabel(12.5m));
        systems.Update();
        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root));
        Assert.Equal(Role.Text, world.Read<AccessibleRole>(node).Value);
        Assert.Equal("12.50", world.Read<AccessibleName>(node).Value);

        world.Replace(root, new FormattedLabel(7.5m));
        systems.Update();

        Assert.Equal("7.50", world.Read<AccessibleName>(node).Value);
    }

#if DEBUG
    [Fact]
    public void AccessibilityRequiresAnActiveCalculationScope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Entity.Null.Accessible(Role.Button, "Run"));
        Assert.Contains("active view build", error.Message);
    }

    [Theory]
    [InlineData(false, "defined Role")]
    [InlineData(true, "name string")]
    public void AccessibilityUsageErrorsPreserveThePreviousData(bool nullName, string message)
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity root = Views.Views.Spawn(world, Entity.Null, new UserControl(Role.Button, "Inventory"));
        systems.Update();
        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root));

        world.Replace(root, nullName
            ? new UserControl(Role.Button, null!)
            : new UserControl((Role)byte.MaxValue, "Changed"));
        systems.Update();

        Assert.Contains(message, world.ReadSparse<BuildError>(root).Exception.Message);
        Assert.Equal(Role.Button, world.Read<AccessibleRole>(node).Value);
        Assert.Equal("Inventory", world.Read<AccessibleName>(node).Value);
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextFieldNameFollowsItsPlaceholderWhileTextChangesIndependently(bool rewritten)
    {
        using var frames = new ValueFrames(text: true);
        Entity node = rewritten
            ? Views.Views.Spawn(frames.World, frames.Canvas, new Rewrites.TextField("Ada", "Name"))
            : Views.Views.Spawn(frames.World, frames.Canvas, new TextField("Ada", "Name"));
        frames.Frame();

        Assert.Equal(Role.TextField, frames.World.Read<AccessibleRole>(node).Value);
        Assert.Equal("Name", frames.World.Read<AccessibleName>(node).Value);

        Gamepad.Navigate(frames.World, Direction.Right);
        Keyboard.Text(frames.World, "!");
        frames.Frame();

        Assert.Equal("Ada!", frames.World.Read<EditableText>(node).Text);
        Assert.Equal("Name", frames.World.Read<AccessibleName>(node).Value);

        if (rewritten)
            frames.World.ReplaceSparse(node, new Rewrites.TextField("Bob", "Name"));
        else
            frames.World.ReplaceSparse(node, new TextField("Bob", "Name"));
        frames.Frame();

        Assert.Equal("Bob", frames.World.Read<EditableText>(node).Text);
        Assert.Equal("Name", frames.World.Read<AccessibleName>(node).Value);

        if (rewritten)
            frames.World.ReplaceSparse(node, new Rewrites.TextField("Bob", "Account"));
        else
            frames.World.ReplaceSparse(node, new TextField("Bob", "Account"));
        frames.Frame();

        Assert.Equal(Role.TextField, frames.World.Read<AccessibleRole>(node).Value);
        Assert.Equal("Bob", frames.World.Read<EditableText>(node).Text);
        Assert.Equal("Account", frames.World.Read<AccessibleName>(node).Value);
    }

    [Theory]
    [InlineData("Button", Role.Button, "Settings", "Inventory")]
    [InlineData("UserVirtualList", Role.List, "", "")]
    [InlineData("UserScrollView", Role.Group, "", "")]
    [InlineData("UserFooter", Role.Group, "", "")]
    [InlineData("UserTitle", Role.Heading, "Settings", "Inventory")]
    [InlineData("UserDialog", Role.Dialog, "", "")]
    [InlineData("Knob", Role.Slider, "", "")]
    [InlineData("UserToggle", Role.Toggle, "", "")]
    [InlineData("IconButton", Role.Button, "Settings", "Inventory")]
    [InlineData("UserButton", Role.Button, "Settings", "Inventory")]
    [InlineData("VirtualList", Role.List, "", "")]
    [InlineData("ScrollView", Role.Group, "", "")]
    [InlineData("DialogActions", Role.Group, "", "")]
    [InlineData("DialogTitle", Role.Heading, "Settings", "Inventory")]
    [InlineData("Dialog", Role.Dialog, "", "")]
    [InlineData("Slider", Role.Slider, "", "")]
    [InlineData("Toggle", Role.Toggle, "", "")]
    [InlineData("Image", Role.Image, "", "")]
    public void ControlsDeclareRolesAndNames(string control, Role role, string name, string changedName)
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity root = Views.Views.Spawn(world, Entity.Null, new ControlExample(control, "Settings"));
        systems.Update();

        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root));
        Assert.Equal(role, world.Read<AccessibleRole>(node).Value);
        Assert.Equal(name, world.Read<AccessibleName>(node).Value);

        world.Replace(root, new ControlExample(control, "Inventory"));
        systems.Update();

        Assert.Equal(node, Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root)));
        Assert.Equal(role, world.Read<AccessibleRole>(node).Value);
        Assert.Equal(changedName, world.Read<AccessibleName>(node).Value);
    }

    [Fact]
    public void TextRoleAndNameFollowTheDisplayedString()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity node = Views.Views.Spawn(world, Entity.Null, new Text("Inventory"));
        systems.Update();

        Assert.Equal(Role.Text, world.Read<AccessibleRole>(node).Value);
        Assert.Equal("Inventory", world.Read<AccessibleName>(node).Value);

        node.SetText(world, "Quest log");
        systems.Update();

        Assert.Equal(Role.Text, world.Read<AccessibleRole>(node).Value);
        Assert.Equal("Quest log", world.Read<AccessibleName>(node).Value);
    }

    [Fact]
    public void UserComponentDeclaresAndUpdatesItsRoleAndName()
    {
        using var world = new World();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new ViewRebuildSystem());
        Entity root = Views.Views.Spawn(world, Entity.Null, new UserControl(Role.Button, "Open inventory"));
        systems.Update();

        Entity node = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root));
        Assert.Equal(Role.Button, world.Read<AccessibleRole>(node).Value);
        Assert.Equal("Open inventory", world.Read<AccessibleName>(node).Value);
        Assert.Equal(node, world.Read<ModifiedNode>(root).Value);

        world.Replace(root, new UserControl(Role.Group, "Inventory"));
        systems.Update();

        Assert.Equal(node, Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root)));
        Assert.Equal(Role.Group, world.Read<AccessibleRole>(node).Value);
        Assert.Equal("Inventory", world.Read<AccessibleName>(node).Value);
    }

    private readonly record struct UserControl(Role Role, string Name) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity node = Scope.View(new Group(), 1);
            Scope.Write<ModifiedNode>(Scope.Entity) = new ModifiedNode(node.Accessible(Role, Name));
            return default;
        }
    }

    private readonly record struct ModifiedNode(Entity Value) : IComponent;

    private readonly record struct FormattedLabel(decimal Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            TextFunctions.DeclareText(1, Value, "F2", CultureInfo.InvariantCulture);
            return default;
        }
    }

    private readonly record struct ControlExample(string Control, string Label) : IView, IComponent
    {
        public BuildTask Build()
        {
            switch (Control)
            {
                case "Button": Scope.View(new Button(Label), 1); break;
                case "UserVirtualList": Scope.View(new Rewrites.VirtualList(20), 1); break;
                case "UserScrollView": Scope.View(new Rewrites.ScrollView(), 1); break;
                case "UserFooter": Scope.View(new Rewrites.DialogFooter(), 1); break;
                case "UserTitle": Scope.View(new Rewrites.DialogTitle(Label), 1); break;
                case "UserDialog": Scope.View(new Rewrites.Dialog(), 1); break;
                case "Knob": Scope.View(new Rewrites.Knob(0.5f), 1); break;
                case "UserToggle": Scope.View(new Rewrites.Toggle(true), 1); break;
                case "IconButton": Scope.View(new Rewrites.IconButton(">", Label), 1); break;
                case "UserButton": Scope.View(new Rewrites.Button(Label), 1); break;
                case "VirtualList": Scope.View(new VirtualList(20), 1); break;
                case "ScrollView": Scope.View(new ScrollView(), 1); break;
                case "DialogActions": Scope.View(new DialogActions(), 1); break;
                case "DialogTitle": Scope.View(new DialogTitle(Label), 1); break;
                case "Dialog": Scope.View(new Dialog(), 1); break;
                case "Slider": Scope.View(new Slider(0.5f), 1); break;
                case "Toggle": Scope.View(new Toggle(true), 1); break;
                case "Image": Scope.View(new Image(default), 1); break;
            }
            return default;
        }
    }
}
