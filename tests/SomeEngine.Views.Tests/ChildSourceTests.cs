using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class ChildSourceTests
{
    [Fact]
    public void ContentDeclaresAllChildrenAndReleasesTheRemovedSubtree()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentDeclaration(true));
        frames.Frame();
        Entity group = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity[] children = ElementChildren.Read(frames.World, group);
        Assert.Equal(new[] { "A", "B" }, children.Select(child => frames.World.ReadComponentData<Branch>(child).Text));
        Entity leaf = Assert.Single(ElementChildren.Read(frames.World, children[1]));
        frames.World.Set(root, new ContentDeclaration(false));

        frames.Frame();

        Assert.Equal(group, Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.Equal(children[0], Assert.Single(ElementChildren.Read(frames.World, group)));
        Assert.False(frames.World.IsAlive(children[1]) && frames.World.Has<ViewType>(children[1]));
        Assert.False(frames.World.IsAlive(leaf) && frames.World.Has<ViewType>(leaf));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainerParameterChangesPreserveTheCallerChildren(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContainerDeclaration("first", "A", sparse));
        frames.Frame();
        Entity container = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = Assert.Single(ElementChildren.Read(frames.World, container));
        Entity leaf = Assert.Single(ElementChildren.Read(frames.World, child));
        if (sparse)
            frames.World.SetSparse(container, new SparseContainer("updated"));
        else
            frames.World.Set(container, new Container("updated"));

        frames.Frame();

        Assert.Equal("updated", frames.World.ReadComponentData<DisplayText>(container).Text);
        Assert.Equal(child, Assert.Single(ElementChildren.Read(frames.World, container)));
        Assert.Equal(leaf, Assert.Single(ElementChildren.Read(frames.World, child)));
        Assert.Equal("A", frames.World.ReadComponentData<Label>(leaf).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerChangesUpdateChildrenWhileEqualContainerDataKeepsItsVersions(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContainerDeclaration("container", "A", sparse));
        frames.Frame();
        Entity container = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = Assert.Single(ElementChildren.Read(frames.World, container));
        Entity leaf = Assert.Single(ElementChildren.Read(frames.World, child));
        ulong parameterVersion = sparse
            ? frames.World.ComponentDataWriteVersion<SparseContainer>(container)
            : frames.World.ComponentDataWriteVersion<Container>(container);
        ulong displayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(container);
        frames.World.Set(root, new ContainerDeclaration("container", "B", sparse));

        frames.Frame();

        Assert.Equal(container, Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.Equal(child, Assert.Single(ElementChildren.Read(frames.World, container)));
        Assert.Equal(leaf, Assert.Single(ElementChildren.Read(frames.World, child)));
        Assert.Equal("B", frames.World.ReadComponentData<Label>(leaf).Text);
        Assert.Equal(parameterVersion, sparse
            ? frames.World.ComponentDataWriteVersion<SparseContainer>(container)
            : frames.World.ComponentDataWriteVersion<Container>(container));
        Assert.Equal(displayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(container));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerChangesUpdateContainerDataAndReuseItsDeclaredChildren(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContainerDeclaration("first", "A", sparse));
        frames.Frame();
        Entity container = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = Assert.Single(ElementChildren.Read(frames.World, container));
        frames.World.Set(root, new ContainerDeclaration("updated", "B", sparse));

        frames.Frame();

        Assert.Equal(container, Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.Equal(child, Assert.Single(ElementChildren.Read(frames.World, container)));
        Assert.Equal("updated", frames.World.ReadComponentData<DisplayText>(container).Text);
        Assert.Equal("B", frames.World.ReadComponentData<Branch>(child).Text);
    }

    [Fact]
    public void EmptyContentReleasesAllPreviousChildrenAndTheirSubtrees()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentDeclaration(true));
        frames.Frame();
        Entity group = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity[] children = ElementChildren.Read(frames.World, group);
        Entity[] leaves = children.Select(child =>
            Assert.Single(ElementChildren.Read(frames.World, child))).ToArray();
        frames.World.Set(root, new ContentDeclaration(false, IncludeFirst: false));

        frames.Frame();

        Assert.Equal(group, Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.Empty(ElementChildren.Read(frames.World, group));
        Assert.All(children.Concat(leaves), entity => Assert.False(frames.World.IsAlive(entity) && frames.World.Has<ViewType>(entity)));
    }

    [Fact]
    public void ContentReordersKeyedChildrenAndPreservesTheirLocalData()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new KeyedContentDeclaration([11, 22, 33]));
        frames.Frame();
        Entity group = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity[] children = ElementChildren.Read(frames.World, group);
        frames.World.Add(children[1], new DisplayText("selected"));
        frames.World.Set(children[1], default(LocalState));
        frames.World.Set(root, new KeyedContentDeclaration([33, 11, 22]));

        frames.Frame();

        Assert.Equal(new[] { children[2], children[0], children[1] },
            ElementChildren.Read(frames.World, group));
        Assert.Equal("selected", frames.World.ReadComponentData<DisplayText>(children[1]).Text);
    }

    [Fact]
    public void NestedContentRestoresParentsAndKeysWhileDataUsesTheBuildingEntity()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NestedContentDeclaration());

        frames.Frame();

        Entity[] children = ElementChildren.Read(frames.World, root);
        Assert.Equal(3, children.Length);
        Assert.Equal("caller", frames.World.ReadComponentData<DisplayText>(root).Text);
        Assert.Equal("outer", frames.World.ReadComponentData<DisplayText>(children[0]).Text);
        Assert.Equal("D", frames.World.ReadComponentData<Label>(children[1]).Text);
        Assert.Equal("E", frames.World.ReadComponentData<Label>(children[2]).Text);
        Assert.Equal(99UL, frames.World.ReadComponentData<ElementKey>(children[2]).Key);
        Entity[] content = ElementChildren.Read(frames.World, children[0]);
        Assert.Equal(3, content.Length);
        Assert.Equal("A", frames.World.ReadComponentData<Label>(content[0]).Text);
        Assert.Equal(11UL, frames.World.ReadComponentData<ElementKey>(content[0]).Key);
        Assert.Equal("inner", frames.World.ReadComponentData<DisplayText>(content[1]).Text);
        Assert.Equal("C", frames.World.ReadComponentData<Label>(content[2]).Text);
        Entity leaf = Assert.Single(ElementChildren.Read(frames.World, content[1]));
        Assert.Equal("B", frames.World.ReadComponentData<Label>(leaf).Text);
        Assert.Equal(11UL, frames.World.ReadComponentData<ElementKey>(leaf).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentReadsUpdateTheCallerChildrenWhenTheirSourceChanges(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        if (sparse)
            frames.World.SetSparse(source, new SparseText("A"));
        else
            frames.World.Add(source, new DisplayText("A"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ReadContentDeclaration(source, sparse));
        frames.Frame();
        Entity group = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = Assert.Single(ElementChildren.Read(frames.World, group));
        ulong groupVersion = frames.World.ComponentDataWriteVersion<Group>(group);
        if (sparse)
            frames.World.SetSparse(source, new SparseText("B"));
        else
            frames.World.Set(source, new DisplayText("B"));

        frames.Frame();

        Assert.Equal(group, Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.Equal(child, Assert.Single(ElementChildren.Read(frames.World, group)));
        Assert.Equal("B", frames.World.ReadComponentData<Label>(child).Text);
        Assert.Equal(groupVersion, frames.World.ComponentDataWriteVersion<Group>(group));
    }

    [Fact]
    public void ContainerDependencyChangesUpdateItsDataAndPreserveCallerChildren()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create(new DisplayText("first"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ReadContainerDeclaration(source));
        frames.Frame();
        Entity container = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = Assert.Single(ElementChildren.Read(frames.World, container));
        frames.World.Set(source, new DisplayText("updated"));

        frames.Frame();

        Assert.Equal("updated", frames.World.ReadComponentData<DisplayText>(container).Text);
        Assert.Equal(child, Assert.Single(ElementChildren.Read(frames.World, container)));
        Assert.Equal("A", frames.World.ReadComponentData<Label>(child).Text);
    }

    [Fact]
    public void OnceContentReadsPreserveTheFirstChildrenAfterSourceChanges()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create(new DisplayText("A"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceReadContentDeclaration(source));
        frames.Frame();
        Entity group = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = Assert.Single(ElementChildren.Read(frames.World, group));
        ulong childVersion = frames.World.ComponentDataWriteVersion<Label>(child);
        frames.World.Set(source, new DisplayText("B"));

        frames.Frame();

        Assert.Equal(child, Assert.Single(ElementChildren.Read(frames.World, group)));
        Assert.Equal("A", frames.World.ReadComponentData<Label>(child).Text);
        Assert.Equal(childVersion, frames.World.ComponentDataWriteVersion<Label>(child));
    }

    [Fact]
    public void ContentKeepsDependenciesReadBeforeAndAfterItsBlock()
    {
        using var frames = new HeadlessFrameDriver();
        Entity before = frames.World.Create(new DisplayText("A"));
        Entity inside = frames.World.Create(new DisplayText("B"));
        Entity after = frames.World.Create(new DisplayText("C"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new SurroundingReads(before, inside, after));
        frames.Frame();
        Entity group = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = Assert.Single(ElementChildren.Read(frames.World, group));
        frames.World.Set(before, new DisplayText("D"));

        frames.Frame();
        Assert.Equal("DC", frames.World.ReadComponentData<DisplayText>(root).Text);
        frames.World.Set(inside, new DisplayText("E"));
        frames.Frame();
        Assert.Equal("E", frames.World.ReadComponentData<Label>(child).Text);
        frames.World.Set(after, new DisplayText("F"));
        frames.Frame();
        Assert.Equal("DF", frames.World.ReadComponentData<DisplayText>(root).Text);
    }

#if DEBUG
    [Fact]
    public void ContentOnBuildChildrenReportsTheSourceAndRestoresTheScope()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new BuildContentDeclaration());

        frames.Frame();
        var error = ReadBuildException(frames.World);

        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
        Hierarchy<ElementTree>.DestroySubtree(frames.World, root);
        Entity replacement = Views.Spawn(frames.World, Entity.Null, new ContentDeclaration(true));

        frames.Frame();

        Entity group = Assert.Single(ElementChildren.Read(frames.World, replacement));
        Assert.Equal(2, ElementChildren.Read(frames.World, group).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoContentScopesForOneContainerReportTheRepeatedDeclaration(bool nested)
    {
        using var frames = new HeadlessFrameDriver();
        Views.Spawn(frames.World, Entity.Null, new RepeatedContentDeclaration(nested));

        frames.Frame();
        var error = ReadBuildException(frames.World);

        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
    }

    [Fact]
    public void CallerBuildChildDeclarationsReportTheRequiredContentScope()
    {
        using var frames = new HeadlessFrameDriver();
        Views.Spawn(frames.World, Entity.Null, new ChildDeclaringContainer());

        frames.Frame();
        var error = ReadBuildException(frames.World);
    }

    [Fact]
    public void DifferentBuildsDeclaringTheSameContainerChildrenReportTheSameRebuild()
    {
        using var frames = new HeadlessFrameDriver();
        Views.Spawn(frames.World, Entity.Null, new SharedContentDeclaration());

        frames.Frame();
        var error = ReadBuildException(frames.World);
    }

    [Fact]
    public void ContentCompletionReportsStatefulRepeatedKeysAndRestoresTheScope()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RepeatedChildren(2));
        frames.Frame();
        Entity group = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity child = ElementChildren.Read(frames.World, group)[0];
        frames.World.Set(child, default(LocalState));
        frames.World.Set(root, new RepeatedChildren(1));

        frames.Frame();
        var error = ReadBuildException(frames.World);

        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
    }

    private static InvalidOperationException ReadBuildException(World world)
    {
        InvalidOperationException? exception = null;
        world.ExecuteSparseRead<BuildError>((entities, errors) =>
            exception = Assert.IsType<InvalidOperationException>(Assert.Single(errors.ToArray()).Exception));
        return exception!;
    }

    private readonly record struct RepeatedChildren(int Count) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
            {
                for (int index = 0; index < Count; index++)
                    Scope.View(new Label(index.ToString()));
            }
            return default;
        }
    }

    private readonly record struct SharedContentDeclaration : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity group = Scope.View(new Group());
            Scope.View(new ContentPart(group, "A"));
            Scope.View(new ContentPart(group, "B"));
            return default;
        }
    }

    private readonly record struct ContentPart(Entity Group, string Text) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Group.Content())
                Scope.View(new Label(Text));
            return default;
        }
    }

    private readonly record struct ChildDeclaringContainer : IView, IComponent
    {
        public static ChildSource Children => ChildSource.Caller;

        public BuildTask Build()
        {
            Scope.View(new Label("own child"));
            return default;
        }
    }

    private readonly record struct RepeatedContentDeclaration(bool Nested) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity group = Scope.View(new Group());
            using (group.Content())
            {
                Scope.View(new Label("A"));
                if (Nested)
                {
                    using (group.Content())
                        Scope.View(new Label("B"));
                }
            }
            if (!Nested)
            {
                using (group.Content())
                    Scope.View(new Label("B"));
            }
            return default;
        }
    }

    private readonly record struct BuildContentDeclaration : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Branch("A")).Content())
                Scope.View(new Label("B"));
            return default;
        }
    }
#endif

    private readonly record struct ContentDeclaration(bool IncludeSecond, bool IncludeFirst = true) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
            {
                if (IncludeFirst)
                    Scope.View(new Branch("A"), 11);
                if (IncludeSecond)
                    Scope.View(new Branch("B"), 22);
            }
            return default;
        }
    }

    private readonly record struct KeyedContentDeclaration(ulong[] Ids) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
            {
                foreach (ulong id in Ids)
                {
                    using (Scope.Key(id))
                        Scope.View(new Branch(id.ToString()));
                }
            }
            return default;
        }
    }

    private readonly record struct NestedContentDeclaration : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.Key(9))
            {
                using (var outer = Scope.View(new Group(), 1).Content())
                {
                    WriteDisplay("caller");
                    Scope.World.Add(outer.Entity, new DisplayText("outer"));
                    Scope.View(new Label("A"), 11);
                    using (var inner = Scope.View(new Group(), 22).Content())
                    {
                        Scope.World.Add(inner.Entity, new DisplayText("inner"));
                        Scope.View(new Label("B"), 11);
                    }
                    Scope.View(new Label("C"), 33);
                }
                Scope.View(new Label("D"), 2);
            }
            Scope.View(new Label("E"), 99);
            return default;
        }
    }

    private readonly record struct ReadContentDeclaration(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
            {
                string text = Sparse ? Scope.Read<SparseText>(Source).Text : Scope.Read<DisplayText>(Source).Text;
                Scope.View(new Label(text));
            }
            return default;
        }
    }

    private readonly record struct OnceReadContentDeclaration(Entity Source) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
                Scope.View(new Label(Scope.Read<DisplayText>(Source).Text));
            return default;
        }
    }

    private readonly record struct ReadContainerDeclaration(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new ReadContainer(Source)).Content())
                Scope.View(new Label("A"));
            return default;
        }
    }

    private readonly record struct ReadContainer(Entity Source) : IView, IComponent
    {
        public static ChildSource Children => ChildSource.Caller;

        public BuildTask Build()
        {
            WriteDisplay(Scope.Read<DisplayText>(Source).Text);
            return default;
        }
    }

    private readonly record struct SurroundingReads(Entity Before, Entity Inside, Entity After) : IView, IComponent
    {
        public BuildTask Build()
        {
            string before = Scope.Read<DisplayText>(Before).Text;
            using (Scope.View(new Group()).Content())
                Scope.View(new Label(Scope.Read<DisplayText>(Inside).Text));
            WriteDisplay(before + Scope.Read<DisplayText>(After).Text);
            return default;
        }
    }

    private readonly record struct SparseText(string Text) : ISparseComponent, ITrackedComponent;

    private readonly record struct ContainerDeclaration(string Name, string Text, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity container = Sparse
                ? Scope.View(new SparseContainer(Name))
                : Scope.View(new Container(Name));
            using (container.Content())
                Scope.View(new Branch(Text));
            return default;
        }
    }

    private readonly record struct Container(string Name) : IView, IComponent
    {
        public static ChildSource Children => ChildSource.Caller;

        public BuildTask Build()
        {
            WriteDisplay(Name);
            return default;
        }
    }

    private readonly record struct SparseContainer(string Name) : IView, ISparseComponent
    {
        public static ChildSource Children => ChildSource.Caller;

        public BuildTask Build()
        {
            WriteDisplay(Name);
            return default;
        }
    }

    private static void WriteDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
            Scope.World.Set(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private readonly record struct DisplayText(string Text) : ITrackedComponent;

    private readonly record struct Branch(string Text) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new Label(Text));
            return default;
        }
    }

    private readonly record struct Label(string Text) : IView, IComponent
    {
        public BuildTask Build() => default;
    }
}
