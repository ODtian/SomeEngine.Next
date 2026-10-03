using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed class ChildSourceTests
{
    [Fact]
    public void ContentDeclaresAllChildrenAndReleasesTheRemovedSubtree()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentDeclaration(true));
        frames.Frame();
        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray();
        Assert.Equal(new[] { "A", "B" }, children.Select(child => frames.World.Read<Branch>(child).Text));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[1]));
        frames.World.Replace(root, new ContentDeclaration(false));

        frames.Frame();

        Assert.Equal(group, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal(children[0], Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, group)));
        Assert.False(frames.World.IsAlive(children[1]));
        Assert.False(frames.World.IsAlive(leaf));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainerParameterChangesPreserveTheCallerChildren(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContainerDeclaration("first", "A", sparse));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, child));
        if (sparse)
            frames.World.ReplaceSparse(container, new SparseContainer("updated"));
        else
            frames.World.Replace(container, new Container("updated"));

        frames.Frame();

        Assert.Equal("updated", frames.World.Read<DisplayText>(container).Text);
        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container)));
        Assert.Equal(leaf, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, child)));
        Assert.Equal("A", frames.World.Read<Label>(leaf).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerChangesUpdateChildrenWhileEqualContainerDataKeepsItsVersions(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContainerDeclaration("container", "A", sparse));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, child));
        uint parameterVersion = sparse
            ? frames.World.SparseWriteVersion<SparseContainer>(container)
            : frames.World.WriteVersion<Container>(container);
        uint displayVersion = frames.World.WriteVersion<DisplayText>(container);
        frames.World.Replace(root, new ContainerDeclaration("container", "B", sparse));

        frames.Frame();

        Assert.Equal(container, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container)));
        Assert.Equal(leaf, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, child)));
        Assert.Equal("B", frames.World.Read<Label>(leaf).Text);
        Assert.Equal(parameterVersion, sparse
            ? frames.World.SparseWriteVersion<SparseContainer>(container)
            : frames.World.WriteVersion<Container>(container));
        Assert.Equal(displayVersion, frames.World.WriteVersion<DisplayText>(container));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerChangesUpdateContainerDataAndReuseItsDeclaredChildren(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContainerDeclaration("first", "A", sparse));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        frames.World.Replace(root, new ContainerDeclaration("updated", "B", sparse));

        frames.Frame();

        Assert.Equal(container, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container)));
        Assert.Equal("updated", frames.World.Read<DisplayText>(container).Text);
        Assert.Equal("B", frames.World.Read<Branch>(child).Text);
    }

    [Fact]
    public void EmptyContentReleasesAllPreviousChildrenAndTheirSubtrees()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentDeclaration(true));
        frames.Frame();
        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray();
        Entity[] leaves = children.Select(child =>
            Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, child))).ToArray();
        frames.World.Replace(root, new ContentDeclaration(false, IncludeFirst: false));

        frames.Frame();

        Assert.Equal(group, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, group));
        Assert.All(children.Concat(leaves), entity => Assert.False(frames.World.IsAlive(entity)));
    }

    [Fact]
    public void ContentReordersKeyedChildrenAndPreservesTheirLocalData()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new KeyedContentDeclaration([11, 22, 33]));
        frames.Frame();
        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray();
        frames.World.Add(children[1], new DisplayText("selected"));
        frames.World.AddTag<LocalState>(children[1]);
        frames.World.Replace(root, new KeyedContentDeclaration([33, 11, 22]));

        frames.Frame();

        Assert.Equal(new[] { children[2], children[0], children[1] },
            Hierarchy<ElementTree>.GetChildren(frames.World, group).ToArray());
        Assert.Equal("selected", frames.World.Read<DisplayText>(children[1]).Text);
    }

    [Fact]
    public void NestedContentRestoresParentsAndKeysWhileDataUsesTheBuildingEntity()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new NestedContentDeclaration());

        frames.Frame();

        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal(3, children.Length);
        Assert.Equal("caller", frames.World.Read<DisplayText>(root).Text);
        Assert.Equal("outer", frames.World.Read<DisplayText>(children[0]).Text);
        Assert.Equal("D", frames.World.Read<Label>(children[1]).Text);
        Assert.Equal("E", frames.World.Read<Label>(children[2]).Text);
        Assert.Equal(99UL, frames.World.Read<ElementKey>(children[2]).Key);
        Entity[] content = Hierarchy<ElementTree>.GetChildren(frames.World, children[0]).ToArray();
        Assert.Equal(3, content.Length);
        Assert.Equal("A", frames.World.Read<Label>(content[0]).Text);
        Assert.Equal(11UL, frames.World.Read<ElementKey>(content[0]).Key);
        Assert.Equal("inner", frames.World.Read<DisplayText>(content[1]).Text);
        Assert.Equal("C", frames.World.Read<Label>(content[2]).Text);
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, content[1]));
        Assert.Equal("B", frames.World.Read<Label>(leaf).Text);
        Assert.Equal(11UL, frames.World.Read<ElementKey>(leaf).Key);
    }

#if DEBUG
    [Fact]
    public void ContentOnBuildChildrenReportsTheSourceAndRestoresTheScope()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new BuildContentDeclaration());

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);

        Assert.Contains("Content", error.Message, StringComparison.Ordinal);
        Assert.Contains("ChildSource.Build", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
        Hierarchy<ElementTree>.DestroySubtree(frames.World, root);
        Entity replacement = Views.Spawn(frames.World, Entity.Null, new ContentDeclaration(true));

        frames.Frame();

        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, replacement));
        Assert.Equal(2, Hierarchy<ElementTree>.GetChildren(frames.World, group).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoContentScopesForOneContainerReportTheRepeatedDeclaration(bool nested)
    {
        using var frames = new HeadlessFrameDriver();
        Views.Spawn(frames.World, Entity.Null, new RepeatedContentDeclaration(nested));

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);

        Assert.Contains("children", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same rebuild", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
    }

    [Fact]
    public void CallerBuildChildDeclarationsReportTheRequiredContentScope()
    {
        using var frames = new HeadlessFrameDriver();
        Views.Spawn(frames.World, Entity.Null, new ChildDeclaringContainer());

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);

        Assert.Contains("ChildSource.Caller", error.Message, StringComparison.Ordinal);
        Assert.Contains("Content", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentBuildsDeclaringTheSameContainerChildrenReportTheSameRebuild()
    {
        using var frames = new HeadlessFrameDriver();
        Views.Spawn(frames.World, Entity.Null, new SharedContentDeclaration());

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);

        Assert.Contains("children", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same rebuild", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContentCompletionReportsStatefulRepeatedKeysAndRestoresTheScope()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new RepeatedChildren(2));
        frames.Frame();
        Entity group = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, group)[0];
        frames.World.AddTag<LocalState>(child);
        frames.World.Replace(root, new RepeatedChildren(1));

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);

        Assert.Contains("local state", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => Scope.View(new Label("outside")));
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
            Scope.World.Replace(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private readonly record struct DisplayText(string Text) : IComponent;

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
