using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

[Collection("Rebuild diagnostics")]
public sealed partial class EnvironmentTests
{
    [Fact]
    public void ProvideStoresTableAndSparseThemesAndReturnsTheNode()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemePair(3, 5, 0));

        frames.Frame();

        Assert.Equal(new Theme(3), frames.World.ReadComponentData<Theme>(root));
        Assert.Equal(new SparseTheme(5), frames.World.ReadComponentData<SparseTheme>(root));
        Assert.Equal(root, frames.World.ReadComponentData<ThemeNode>(root).Value);
        ulong tableVersion = frames.World.ComponentDataWriteVersion<Theme>(root);
        ulong sparseVersion = frames.World.ComponentDataWriteVersion<SparseTheme>(root);
        frames.World.Set(root, new ThemePair(3, 5, 1));
        frames.Frame();

        Assert.Equal(tableVersion, frames.World.ComponentDataWriteVersion<Theme>(root));
        Assert.Equal(sparseVersion, frames.World.ComponentDataWriteVersion<SparseTheme>(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvidedThemeChangesUpdateReadersAndKeepOtherDescendants(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemeGroup(3, sparse));
        frames.Frame();
        var children = ElementChildren.Read(frames.World, root);
        Entity reader = children[0];
        Entity other = children[1];
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(reader).Value);
        frames.Frame();
        ulong otherVersion = frames.World.ComponentDataWriteVersion<DisplayText>(other);

        frames.World.Set(root, new ThemeGroup(7, sparse));
        frames.Frame();

        Assert.Equal("7", frames.World.ReadComponentData<DisplayText>(reader).Value);
        Assert.Equal("other", frames.World.ReadComponentData<DisplayText>(other).Value);
        Assert.Equal(otherVersion, frames.World.ComponentDataWriteVersion<DisplayText>(other));
        ulong readerVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);
        frames.Frame();
        Assert.Equal(readerVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MiddleNodeProvidesItsWholeThemeIncludingTheDefaultValue(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemeGroup(3, sparse, ChildColor: 0));
        frames.Frame();
        var children = ElementChildren.Read(frames.World, root);
        Entity outerReader = children[0];
        Entity middle = children[2];
        Entity innerReader = ElementChildren.Read(frames.World, middle)[0];

        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(outerReader).Value);
        Assert.Equal("0", frames.World.ReadComponentData<DisplayText>(innerReader).Value);
        Assert.True(sparse ? frames.World.HasSparse<SparseTheme>(middle) : frames.World.Has<Theme>(middle));
        ulong innerVersion = frames.World.ComponentDataWriteVersion<DisplayText>(innerReader);
        frames.World.Set(root, new ThemeGroup(7, sparse, ChildColor: 0));
        frames.Frame();

        Assert.Equal("7", frames.World.ReadComponentData<DisplayText>(outerReader).Value);
        Assert.Equal("0", frames.World.ReadComponentData<DisplayText>(innerReader).Value);
        Assert.Equal(innerVersion, frames.World.ComponentDataWriteVersion<DisplayText>(innerReader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterProvideRefreshesAFormerlyMissingThemeWithinTheFrame(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThemeGroup(0, sparse, ThemePresent: false));
        frames.Frame();
        Entity reader = ElementChildren.Read(frames.World, root)[0];
        Assert.Equal("0", frames.World.ReadComponentData<DisplayText>(reader).Value);
        frames.Frame();

        frames.World.Set(root, new ThemeGroup(7, sparse));
        frames.Frame();

        Assert.Equal("7", frames.World.ReadComponentData<DisplayText>(reader).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ThemeCalculationsFollowTheirViewThroughChangesAndMissingSources(bool sparse, bool memo)
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.Create();
        Entity second = frames.World.Create();
        SetTheme(frames.World, first, sparse, 3);
        SetTheme(frames.World, second, sparse, 5);
        Entity reader = Views.Spawn(frames.World, first, new ThemeCalculationDisplay(sparse, memo));
        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(reader).Value);
        frames.Frame();

        SetTheme(frames.World, first, sparse, 7);
        frames.Frame();
        Assert.Equal("7", frames.World.ReadComponentData<DisplayText>(reader).Value);

        Hierarchy<ElementTree>.SetParent(frames.World, reader, second);
        frames.Frame();
        Assert.Equal("5", frames.World.ReadComponentData<DisplayText>(reader).Value);

        RemoveTheme(frames.World, second, sparse);
        frames.Frame();
        Assert.Equal("0", frames.World.ReadComponentData<DisplayText>(reader).Value);

        SetTheme(frames.World, second, sparse, 11);
        frames.Frame();
        Assert.Equal("11", frames.World.ReadComponentData<DisplayText>(reader).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);
        SetTheme(frames.World, first, sparse, 13);
        frames.Frame();
        Assert.Equal("11", frames.World.ReadComponentData<DisplayText>(reader).Value);
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThemeSourceMovesAreReportedInRebuildDiagnostics(bool sparse)
    {
        RebuildDiagnostics.Enabled = true;
        try
        {
            using var frames = new HeadlessFrameDriver();
            Entity first = frames.World.Create();
            Entity second = frames.World.Create();
            SetTheme(frames.World, first, sparse, 3);
            SetTheme(frames.World, second, sparse, 3);
            Entity reader = Views.Spawn(frames.World, first, new ThemeDisplay(sparse));
            frames.Frame();

            Hierarchy<ElementTree>.SetParent(frames.World, reader, second);
            frames.Frame();

            Assert.Equal("3;3", frames.World.ReadComponentData<DisplayText>(reader).Value);
            int component = sparse ? ComponentId<SparseTheme>.Value : ComponentId<Theme>.Value;
            Assert.Contains(new RebuildRecord(reader, first, component),
                RebuildDiagnostics.LastFrame(frames.World).ToArray());
        }
        finally
        {
            RebuildDiagnostics.Enabled = false;
        }
    }

#if DEBUG
    [Fact]
    public void ReadUpOutsideBuildReportsTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Scope.ReadUp<Theme>());
    }

    [Fact]
    public void ProvideOutsideBuildReportsTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Entity.Null.Provide(new Theme(3)));
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AncestorThemeChangesUpdateItsReadersAndKeepOtherDisplays(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.Create();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity reader = Views.Spawn(frames.World, ancestor, new ThemeDisplay(sparse));
        Entity other = Views.Spawn(frames.World, ancestor, new FixedDisplay("other"));

        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(reader).Value);
        ulong themeVersion = sparse
            ? frames.World.ComponentDataWriteVersion<SparseTheme>(ancestor)
            : frames.World.ComponentDataWriteVersion<Theme>(ancestor);
        ulong otherDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(other);
        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(reader).Value);
        Assert.Equal(themeVersion, sparse
            ? frames.World.ComponentDataWriteVersion<SparseTheme>(ancestor)
            : frames.World.ComponentDataWriteVersion<Theme>(ancestor));

        SetTheme(frames.World, ancestor, sparse, 7);
        frames.Frame();

        Assert.Equal("3;7", frames.World.ReadComponentData<DisplayText>(reader).Value);
        Assert.Equal("other", frames.World.ReadComponentData<DisplayText>(other).Value);
        Assert.Equal(otherDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearestThemeSuppliesItsSubtreeIncludingTheProvidingNode(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.Create();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity middle = frames.World.Create();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, ancestor);
        SetTheme(frames.World, middle, sparse, 5);
        Entity outerReader = Views.Spawn(frames.World, ancestor, new ThemeDisplay(sparse));
        Entity innerReader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        Entity selfReader = Views.Spawn(frames.World, ancestor, new ThemeDisplay(sparse));
        SetTheme(frames.World, selfReader, sparse, 11);

        frames.Frame();

        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(outerReader).Value);
        Assert.Equal("5", frames.World.ReadComponentData<DisplayText>(innerReader).Value);
        Assert.Equal("11", frames.World.ReadComponentData<DisplayText>(selfReader).Value);
        ulong innerDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(innerReader);
        ulong selfDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(selfReader);
        SetTheme(frames.World, ancestor, sparse, 7);
        frames.Frame();

        Assert.Equal("3;7", frames.World.ReadComponentData<DisplayText>(outerReader).Value);
        Assert.Equal("5", frames.World.ReadComponentData<DisplayText>(innerReader).Value);
        Assert.Equal("11", frames.World.ReadComponentData<DisplayText>(selfReader).Value);
        Assert.Equal(innerDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(innerReader));
        Assert.Equal(selfDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(selfReader));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingThemeUsesTheDefaultUntilAnAncestorProvidesIt(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.Create();
        Entity middle = frames.World.Create();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, ancestor);
        Entity reader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        frames.Frame();
        Assert.Equal("0", frames.World.ReadComponentData<DisplayText>(reader).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);

        Entity other = frames.World.Create();
        SetTheme(frames.World, other, sparse, 3);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));

        SetTheme(frames.World, ancestor, sparse, 7);
        frames.Frame();

        Assert.Equal("0;7", frames.World.ReadComponentData<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RelevantParentChangesSupplyTheNewAncestorTheme(bool sparse, bool moveAncestor)
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.Create();
        Entity second = frames.World.Create();
        SetTheme(frames.World, first, sparse, 3);
        SetTheme(frames.World, second, sparse, 5);
        Entity middle = frames.World.Create();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, first);
        Entity reader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        frames.Frame();
        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(reader).Value);

        Entity moved = moveAncestor ? middle : reader;
        Hierarchy<ElementTree>.SetParent(frames.World, moved, second);
        frames.Frame();
        Assert.Equal("3;5", frames.World.ReadComponentData<DisplayText>(reader).Value);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);
        SetTheme(frames.World, first, sparse, 7);
        frames.Frame();
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));

        SetTheme(frames.World, second, sparse, 11);
        frames.Frame();
        Assert.Equal("3;5;11", frames.World.ReadComponentData<DisplayText>(reader).Value);

        Hierarchy<ElementTree>.Detach(frames.World, moved);
        frames.Frame();
        Assert.Equal("3;5;11;0", frames.World.ReadComponentData<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderChangesRebindTheDisplayEvenWhenTheThemeValuesMatch(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.Create();
        Entity second = frames.World.Create();
        SetTheme(frames.World, first, sparse, 3);
        SetTheme(frames.World, second, sparse, 3);
        Entity reader = Views.Spawn(frames.World, first, new ThemeDisplay(sparse));
        frames.Frame();
        ulong firstDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);

        Hierarchy<ElementTree>.SetParent(frames.World, reader, second);
        frames.Frame();
        Assert.NotEqual(firstDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));
        Assert.Equal("3;3", frames.World.ReadComponentData<DisplayText>(reader).Value);
        ulong secondDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);
        SetTheme(frames.World, first, sparse, 5);
        frames.Frame();
        Assert.Equal(secondDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));

        SetTheme(frames.World, second, sparse, 7);
        frames.Frame();
        Assert.Equal("3;3;7", frames.World.ReadComponentData<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingAndRemovingNearestThemesRefreshesTheDisplayedSource(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.Create();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity middle = frames.World.Create();
        Hierarchy<ElementTree>.SetParent(frames.World, middle, ancestor);
        Entity reader = Views.Spawn(frames.World, middle, new ThemeDisplay(sparse));
        frames.Frame();

        SetTheme(frames.World, middle, sparse, 5);
        frames.Frame();
        Assert.Equal("3;5", frames.World.ReadComponentData<DisplayText>(reader).Value);

        RemoveTheme(frames.World, middle, sparse);
        frames.Frame();
        Assert.Equal("3;5;3", frames.World.ReadComponentData<DisplayText>(reader).Value);

        RemoveTheme(frames.World, ancestor, sparse);
        frames.Frame();
        Assert.Equal("3;5;3;0", frames.World.ReadComponentData<DisplayText>(reader).Value);

        SetTheme(frames.World, reader, sparse, 11);
        frames.Frame();
        Assert.Equal("3;5;3;0;11", frames.World.ReadComponentData<DisplayText>(reader).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnceThemeReadsRetainTheirInitialDisplay(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity ancestor = frames.World.Create();
        SetTheme(frames.World, ancestor, sparse, 3);
        Entity reader = Views.Spawn(frames.World, ancestor, new OnceThemeDisplay(sparse));
        frames.Frame();
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(reader);

        SetTheme(frames.World, ancestor, sparse, 7);
        Hierarchy<ElementTree>.Detach(frames.World, reader);
        frames.Frame();

        Assert.Equal("3", frames.World.ReadComponentData<DisplayText>(reader).Value);
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(reader));
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
                world.SetSparse(entity, new SparseTheme(color));
            else
                world.SetSparse(entity, new SparseTheme(color));
        }
        else
        {
            if (world.Has<Theme>(entity))
                world.Set(entity, new Theme(color));
            else
                world.Add(entity, new Theme(color));
        }
    }

    private static void AppendDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
        {
            text = $"{Scope.World.ReadComponentData<DisplayText>(Scope.Entity).Value};{text}";
            Scope.World.Set(Scope.Entity, new DisplayText(text));
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

    private readonly record struct ThemeCalculationDisplay(bool Sparse, bool Memo) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            bool sparse = Sparse;
            Entity node = Scope.Entity;
            if (Memo)
            {
                Memo<int> color = Scope.Memo(() => ReadTheme(sparse));
                Scope.Compute(() => Scope.Write<DisplayText>(node) = new DisplayText(color.Value.ToString()));
            }
            else
            {
                Scope.Compute(() => Scope.Write<DisplayText>(node) = new DisplayText(ReadTheme(sparse).ToString()));
            }
            return default;
        }
    }

    private readonly record struct ThemePair(int TableColor, int SparseColor, int Revision) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity node = Scope.Entity.Provide(new Theme(TableColor)).Provide(new SparseTheme(SparseColor));
            if (Scope.World.Has<ThemeNode>(Scope.Entity))
                Scope.World.Set(Scope.Entity, new ThemeNode(node));
            else
                Scope.World.Add(Scope.Entity, new ThemeNode(node));
            return default;
        }
    }

    private readonly record struct ThemeNode(Entity Value) : IComponent;
    private readonly record struct Theme(int Color) : ITrackedComponent;
    private readonly record struct SparseTheme(int Color) : ISparseComponent, ITrackedComponent;
    private readonly record struct DisplayText(string Value) : ITrackedComponent;
}
