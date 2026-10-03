using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class EnvironmentTests
{
    [Fact]
    public void ProvideStoresTableAndSparseThemesAndReturnsTheNode()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemePair(3, 5, 0));

        frames.Frame();

        Assert.Equal(new Theme(3), frames.World.Read<Theme>(root));
        Assert.Equal(new SparseTheme(5), frames.World.ReadSparse<SparseTheme>(root));
        Assert.Equal(root, frames.World.Read<ThemeNode>(root).Value);
        uint tableVersion = frames.World.WriteVersion<Theme>(root);
        uint sparseVersion = frames.World.SparseWriteVersion<SparseTheme>(root);
        frames.World.Replace(root, new ThemePair(3, 5, 1));
        frames.Frame();

        Assert.Equal(tableVersion, frames.World.WriteVersion<Theme>(root));
        Assert.Equal(sparseVersion, frames.World.SparseWriteVersion<SparseTheme>(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvidedThemeChangesUpdateReadersAndKeepOtherDescendants(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemeGroup(3, sparse));
        frames.Frame();
        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Entity reader = children[0];
        Entity other = children[1];
        Assert.Equal("3", frames.World.Read<DisplayText>(reader).Value);
        frames.Frame();
        uint otherVersion = frames.World.WriteVersion<DisplayText>(other);

        frames.World.Replace(root, new ThemeGroup(7, sparse));
        frames.Frame();

        Assert.Equal("7", frames.World.Read<DisplayText>(reader).Value);
        Assert.Equal("other", frames.World.Read<DisplayText>(other).Value);
        Assert.Equal(otherVersion, frames.World.WriteVersion<DisplayText>(other));
        uint readerVersion = frames.World.WriteVersion<DisplayText>(reader);
        frames.Frame();
        Assert.Equal(readerVersion, frames.World.WriteVersion<DisplayText>(reader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiddleNodeProvidesItsWholeThemeIncludingTheDefaultValue(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemeGroup(3, sparse, ChildColor: 0));
        frames.Frame();
        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Entity outerReader = children[0];
        Entity middle = children[2];
        Entity innerReader = Hierarchy<ElementTree>.GetChildren(frames.World, middle)[0];

        Assert.Equal("3", frames.World.Read<DisplayText>(outerReader).Value);
        Assert.Equal("0", frames.World.Read<DisplayText>(innerReader).Value);
        Assert.True(sparse ? frames.World.HasSparse<SparseTheme>(middle) : frames.World.Has<Theme>(middle));
        uint innerVersion = frames.World.WriteVersion<DisplayText>(innerReader);
        frames.World.Replace(root, new ThemeGroup(7, sparse, ChildColor: 0));
        frames.Frame();

        Assert.Equal("7", frames.World.Read<DisplayText>(outerReader).Value);
        Assert.Equal("0", frames.World.Read<DisplayText>(innerReader).Value);
        Assert.Equal(innerVersion, frames.World.WriteVersion<DisplayText>(innerReader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterProvideRefreshesAFormerlyMissingThemeWithinTheFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemeGroup(0, sparse, ThemePresent: false));
        frames.Frame();
        Entity reader = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Assert.Equal("0", frames.World.Read<DisplayText>(reader).Value);
        frames.Frame();

        frames.World.Replace(root, new ThemeGroup(7, sparse));
        frames.Frame();

        Assert.Equal("7", frames.World.Read<DisplayText>(reader).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(reader);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(reader));
    }

#if DEBUG
    [Fact]
    public void ReadUpOutsideBuildReportsTheRequiredScope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Scope.ReadUp<Theme>());
        Assert.Contains("active view build", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProvideOutsideBuildReportsTheRequiredScope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Entity.Null.Provide(new Theme(3)));
        Assert.Contains("active view build", error.Message, StringComparison.OrdinalIgnoreCase);
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AncestorThemeChangesUpdateItsReadersAndKeepOtherDisplays(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.CreateEntity();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity reader = Views.Spawn(frames.World, ancestor, new ThemeDisplay(sparse));
        Entity other = Views.Spawn(frames.World, ancestor, new FixedDisplay("other"));

        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(reader).Value);
        uint themeVersion = sparse
            ? frames.World.SparseWriteVersion<SparseTheme>(ancestor)
            : frames.World.WriteVersion<Theme>(ancestor);
        uint otherDisplayVersion = frames.World.WriteVersion<DisplayText>(other);
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(reader).Value);
        Assert.Equal(themeVersion, sparse
            ? frames.World.SparseWriteVersion<SparseTheme>(ancestor)
            : frames.World.WriteVersion<Theme>(ancestor));

        SetTheme(frames.World, ancestor, sparse, 7);
        frames.Frame();

        Assert.Equal("3;7", frames.World.Read<DisplayText>(reader).Value);
        Assert.Equal("other", frames.World.Read<DisplayText>(other).Value);
        Assert.Equal(otherDisplayVersion, frames.World.WriteVersion<DisplayText>(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearestThemeSuppliesItsSubtreeIncludingTheProvidingNode(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.CreateEntity();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity middle = frames.World.CreateEntity();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, ancestor);
        SetTheme(frames.World, middle, sparse, 5);
        Entity outerReader = Views.Spawn(frames.World, ancestor, new ThemeDisplay(sparse));
        Entity innerReader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        Entity selfReader = Views.Spawn(frames.World, ancestor, new ThemeDisplay(sparse));
        SetTheme(frames.World, selfReader, sparse, 11);

        frames.Frame();

        Assert.Equal("3", frames.World.Read<DisplayText>(outerReader).Value);
        Assert.Equal("5", frames.World.Read<DisplayText>(innerReader).Value);
        Assert.Equal("11", frames.World.Read<DisplayText>(selfReader).Value);
        uint innerDisplayVersion = frames.World.WriteVersion<DisplayText>(innerReader);
        uint selfDisplayVersion = frames.World.WriteVersion<DisplayText>(selfReader);
        SetTheme(frames.World, ancestor, sparse, 7);
        frames.Frame();

        Assert.Equal("3;7", frames.World.Read<DisplayText>(outerReader).Value);
        Assert.Equal("5", frames.World.Read<DisplayText>(innerReader).Value);
        Assert.Equal("11", frames.World.Read<DisplayText>(selfReader).Value);
        Assert.Equal(innerDisplayVersion, frames.World.WriteVersion<DisplayText>(innerReader));
        Assert.Equal(selfDisplayVersion, frames.World.WriteVersion<DisplayText>(selfReader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingThemeUsesTheDefaultUntilAnAncestorProvidesIt(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.CreateEntity();
        Entity middle = frames.World.CreateEntity();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, ancestor);
        Entity reader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        frames.Frame();
        Assert.Equal("0", frames.World.Read<DisplayText>(reader).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(reader);

        Entity other = frames.World.CreateEntity();
        SetTheme(frames.World, other, sparse, 3);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(reader));

        SetTheme(frames.World, ancestor, sparse, 7);
        frames.Frame();

        Assert.Equal("0;7", frames.World.Read<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RelevantParentChangesSupplyTheNewAncestorTheme(bool sparse, bool moveAncestor)
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.CreateEntity();
        Entity second = frames.World.CreateEntity();
        SetTheme(frames.World, first, sparse, 3);
        SetTheme(frames.World, second, sparse, 5);
        Entity middle = frames.World.CreateEntity();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, first);
        Entity reader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        frames.Frame();
        Assert.Equal("3", frames.World.Read<DisplayText>(reader).Value);

        Entity moved = moveAncestor ? middle : reader;
        Hierarchy<ElementTree>.SetParent(frames.World, moved, second);
        frames.Frame();
        Assert.Equal("3;5", frames.World.Read<DisplayText>(reader).Value);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(reader);
        SetTheme(frames.World, first, sparse, 7);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(reader));

        SetTheme(frames.World, second, sparse, 11);
        frames.Frame();
        Assert.Equal("3;5;11", frames.World.Read<DisplayText>(reader).Value);

        Hierarchy<ElementTree>.Detach(frames.World, moved);
        frames.Frame();
        Assert.Equal("3;5;11;0", frames.World.Read<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderChangesRebindTheDisplayEvenWhenTheThemeValuesMatch(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.CreateEntity();
        Entity second = frames.World.CreateEntity();
        SetTheme(frames.World, first, sparse, 3);
        SetTheme(frames.World, second, sparse, 3);
        Entity reader = Views.Spawn(frames.World, first, new ThemeDisplay(sparse));
        frames.Frame();
        uint firstDisplayVersion = frames.World.WriteVersion<DisplayText>(reader);

        Hierarchy<ElementTree>.SetParent(frames.World, reader, second);
        frames.Frame();
        Assert.NotEqual(firstDisplayVersion, frames.World.WriteVersion<DisplayText>(reader));
        Assert.Equal("3;3", frames.World.Read<DisplayText>(reader).Value);
        uint secondDisplayVersion = frames.World.WriteVersion<DisplayText>(reader);
        SetTheme(frames.World, first, sparse, 5);
        frames.Frame();
        Assert.Equal(secondDisplayVersion, frames.World.WriteVersion<DisplayText>(reader));

        SetTheme(frames.World, second, sparse, 7);
        frames.Frame();
        Assert.Equal("3;3;7", frames.World.Read<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingAndRemovingNearestThemesRefreshesTheDisplayedSource(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.CreateEntity();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity middle = frames.World.CreateEntity();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, ancestor);
        Entity reader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        frames.Frame();

        SetTheme(frames.World, middle, sparse, 5);
        frames.Frame();
        Assert.Equal("3;5", frames.World.Read<DisplayText>(reader).Value);

        RemoveTheme(frames.World, middle, sparse);
        frames.Frame();
        Assert.Equal("3;5;3", frames.World.Read<DisplayText>(reader).Value);

        RemoveTheme(frames.World, ancestor, sparse);
        frames.Frame();
        Assert.Equal("3;5;3;0", frames.World.Read<DisplayText>(reader).Value);

        SetTheme(frames.World, reader, sparse, 11);
        frames.Frame();
        Assert.Equal("3;5;3;0;11", frames.World.Read<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnceThemeReadsRetainTheirInitialDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.CreateEntity();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity reader = Views.Spawn(frames.World, ancestor, new OnceThemeDisplay(sparse));
        frames.Frame();
        uint displayVersion = frames.World.WriteVersion<DisplayText>(reader);

        SetTheme(frames.World, ancestor, sparse, 7);
        Hierarchy<ElementTree>.Detach(frames.World, reader);
        frames.Frame();

        Assert.Equal("3", frames.World.Read<DisplayText>(reader).Value);
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(reader));
    }

    private readonly record struct OnceThemeDisplay(bool Sparse) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            AppendDisplay(ReadTheme(Sparse).ToString());
            return default;
        }
    }

    private static void RemoveTheme(World world, Entity entity, bool sparse)
    {
        if (sparse)
            world.RemoveSparse<SparseTheme>(entity);
        else
            world.Remove<Theme>(entity);
    }

    private readonly record struct ThemeDisplay(bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            AppendDisplay(ReadTheme(Sparse).ToString());
            return default;
        }
    }

    private static int ReadTheme(bool sparse)
    {
        if (sparse)
        {
            ref readonly SparseTheme theme = ref Scope.ReadUp<SparseTheme>();
            return theme.Color;
        }
        ref readonly Theme tableTheme = ref Scope.ReadUp<Theme>();
        return tableTheme.Color;
    }

    private readonly record struct FixedDisplay(string Text) : IView, IComponent
    {
        public BuildTask Build()
        {
            AppendDisplay(Text);
            return default;
        }
    }

    private static void SetTheme(World world, Entity entity, bool sparse, int color)
    {
        if (sparse)
        {
            if (world.HasSparse<SparseTheme>(entity))
                world.ReplaceSparse(entity, new SparseTheme(color));
            else
                world.AddSparse(entity, new SparseTheme(color));
        }
        else
        {
            if (world.Has<Theme>(entity))
                world.Replace(entity, new Theme(color));
            else
                world.Add(entity, new Theme(color));
        }
    }

    private static void AppendDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
        {
            text = $"{Scope.World.Read<DisplayText>(Scope.Entity).Value};{text}";
            Scope.World.Replace(Scope.Entity, new DisplayText(text));
        }
        else
        {
            Scope.World.Add(Scope.Entity, new DisplayText(text));
        }
    }

    private readonly record struct ThemeGroup(int Color, bool Sparse, bool ThemePresent = true, int? ChildColor = null) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (ThemePresent)
            {
                if (Sparse)
                    Scope.Entity.Provide(new SparseTheme(Color));
                else
                    Scope.Entity.Provide(new Theme(Color));
            }
            Scope.View(new ThemeValueDisplay(Sparse), key: 1);
            Scope.View(new FixedDisplay("other"), key: 2);
            if (ChildColor is int color)
                Scope.View(new ThemeGroup(color, Sparse), key: 3);
            return default;
        }
    }

    private readonly record struct ThemeValueDisplay(bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Write<DisplayText>(Scope.Entity) = new DisplayText(ReadTheme(Sparse).ToString());
            return default;
        }
    }

    private readonly record struct ThemePair(int TableColor, int SparseColor, int Refresh) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity node = Scope.Entity.Provide(new Theme(TableColor)).Provide(new SparseTheme(SparseColor));
            if (Scope.World.Has<ThemeNode>(Scope.Entity))
                Scope.World.Replace(Scope.Entity, new ThemeNode(node));
            else
                Scope.World.Add(Scope.Entity, new ThemeNode(node));
            return default;
        }
    }

    private readonly record struct ThemeNode(Entity Value) : IComponent;
    private readonly record struct Theme(int Color) : IComponent;
    private readonly record struct SparseTheme(int Color) : ISparseComponent;
    private readonly record struct DisplayText(string Value) : IComponent;
}
