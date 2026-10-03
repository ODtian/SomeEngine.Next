using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

/// <summary>Public view behavior shared by the test runner and the ECS NativeAOT executable.</summary>
internal static partial class FirstBuildChecks
{
    internal static void Verify()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ReferenceGroup(new string('v', 5), ReadNumber));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Require(children.Count == 3, "The root has three declared children.");
        Require(frames.World.Read<TableText>(children[0]).Value == "first", "The first child retains its table parameters.");
        Require(frames.World.ReadSparse<ReferenceGroup>(root).Name == "vvvvv", "The root retains its sparse string parameter.");
        Require(frames.World.ReadSparse<ReferenceGroup>(root).NumberCallback() == 41, "The root retains its callback parameter.");
        Require(frames.World.Read<TableText>(children[2]).Value == "last", "The last child retains its declaration position.");
        var grandchildren = Hierarchy<ElementTree>.GetChildren(frames.World, children[1]);
        Require(grandchildren.Count == 1, "The branch creates its child during the first frame.");
        Require(frames.World.Read<TableText>(grandchildren[0]).Value == "vvvvv:41", "The branch builds from its sparse parameters.");
        Require(frames.World.Read<ViewType>(children[0]).Id == frames.World.Read<ViewType>(children[2]).Id,
            "Equal view types share a type number.");
        Require(frames.World.Read<ViewType>(children[0]).Id != frames.World.Read<ViewType>(children[1]).Id,
            "Different view types have different type numbers.");

        VerifyParameterChanges(frames, root, children.ToArray(), grandchildren[0]);
        VerifyOnceParameters(frames);
        VerifyKeyedChildren(frames);
        VerifySpawnedChildParameters(frames);
        VerifyCallerChildren(frames);
        VerifyReadDependencies(frames);
        VerifyObjectState(frames);
        VerifyBuildErrors();
        StructureDependencyChecks.Verify();
        VerifyUntrackedReads(frames);
    }

    private static void VerifySpawnedChildParameters(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new HistoryGroup(7));
        Entity child = Views.Spawn(frames.World, root, new HistoryText(3));
        frames.Frame();
        Require(frames.World.Read<TextHistory>(child).Value == "7",
            "The parent's first build presents the child's new parameters once.");
        uint version = frames.World.WriteVersion<TextHistory>(child);
        frames.Frame();
        Require(frames.World.WriteVersion<TextHistory>(child) == version,
            "The next frame preserves the completed child display.");
    }

    private readonly record struct HistoryGroup(int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new HistoryText(Value), 0);
            return default;
        }
    }

    private readonly record struct HistoryText(int Value) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Scope.World.Has<TextHistory>(Scope.Entity))
                Scope.World.Replace(Scope.Entity, new TextHistory($"{Scope.World.Read<TextHistory>(Scope.Entity).Value},{Value}"));
            else
                Scope.World.Add(Scope.Entity, new TextHistory(Value.ToString()));
            return default;
        }
    }

    private readonly record struct TextHistory(string Value) : IComponent;

    private static int ReadNumber() => 41;
    private static int ReadChangedNumber() => 53;

    private static void VerifyParameterChanges(HeadlessFrameDriver frames, Entity root, Entity[] children, Entity leaf)
    {
        uint branchVersion = frames.World.SparseWriteVersion<SparseBranch>(children[1]);
        uint leafVersion = frames.World.WriteVersion<TableText>(leaf);
        frames.World.ReplaceSparse(root, new ReferenceGroup("vvvvv", ReadNumber));

        frames.Frame();

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray().AsSpan().SequenceEqual(children),
            "Equal child parameters retain the declared entities.");
        Require(frames.World.SparseWriteVersion<SparseBranch>(children[1]) == branchVersion,
            "Equal sparse parameters retain their write version.");
        Require(frames.World.WriteVersion<TableText>(leaf) == leafVersion,
            "Equal nested parameters retain their write version.");
        frames.World.ReplaceSparse(root, new ReferenceGroup("updated", ReadChangedNumber));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray().AsSpan().SequenceEqual(children),
            "Changed child parameters retain the declared entities.");
        Require(frames.World.ReadSparse<SparseBranch>(children[1]).NumberCallback() == 53,
            "Changed sparse parameters retain the latest callback.");
        Require(frames.World.Read<TableText>(leaf).Value == "updated:53",
            "Changed sparse parameters rebuild the nested table text within the frame.");
        leafVersion = frames.World.WriteVersion<TableText>(leaf);
        frames.Frame();
        Require(frames.World.WriteVersion<TableText>(leaf) == leafVersion,
            "A later frame retains the completed parameter update.");
    }

    private static void VerifyOnceParameters(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceText("first"));
        frames.Frame();
        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        frames.World.Replace(root, new OnceText("updated"));

        frames.Frame();

        Require(frames.World.Read<OnceText>(root).Value == "updated", "Once stores the latest parameters.");
        Require(frames.World.Read<TableText>(child).Value == "first", "Once retains its first content.");
    }

    private static void VerifyKeyedChildren(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new KeyedGroup([11, 22, 33]));
        frames.Frame();
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Entity[] leaves = rows.Select(row => Hierarchy<ElementTree>.GetChildren(frames.World, row)[0]).ToArray();
        frames.World.Replace(rows[1], new Selection(7));
        frames.World.Replace(root, new KeyedGroup([33, 11, 22]));

        frames.Frame();

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Span.SequenceEqual([rows[2], rows[0], rows[1]]),
            "Key scopes preserve each row entity in the new declaration order.");
        Require(frames.World.Read<Selection>(rows[1]).Value == 7, "Keyed rows preserve their local state.");
        Require(frames.World.Read<ElementKey>(rows[1]).Key == 22, "The row retains its declared key.");
        frames.World.Replace(root, new KeyedGroup([33, 22]));

        frames.Frame();

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Span.SequenceEqual([rows[2], rows[1]]),
            "Deleting an item preserves the remaining keyed rows.");
        Require(!frames.World.IsAlive(rows[0]) && !frames.World.IsAlive(leaves[0]),
            "Deleting an item releases its row and subtree.");
        frames.World.Replace(root, new KeyedGroup([33, 22], TextOnly: true));

        frames.Frame();

        Entity[] texts = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Require(texts.Length == 2 && frames.World.Read<TableText>(texts[0]).Value == "33" &&
            frames.World.Read<TableText>(texts[1]).Value == "22", "Type changes create the declared replacement views.");
        Require(!frames.World.IsAlive(rows[1]) && !frames.World.IsAlive(rows[2]) &&
            !frames.World.IsAlive(leaves[1]) && !frames.World.IsAlive(leaves[2]),
            "Type changes release the previous rows and their subtrees.");
    }

    private static void VerifyReadDependencies(HeadlessFrameDriver frames)
    {
        Entity source = frames.World.CreateEntity(new TableText("table"));
        frames.World.AddSparse(source, new SparseText("sparse"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ComponentText(source));
        uint sourceVersion = frames.World.WriteVersion<TableText>(source);

        frames.Frame();

        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Require(frames.World.Read<TableText>(child).Value == "table:sparse", "Reads supply table and sparse text.");
        Require(frames.World.WriteVersion<TableText>(source) == sourceVersion, "A read preserves the source write version.");
        frames.World.Replace(source, new TableText("changed"));
        frames.World.ReplaceSparse(source, new SparseText("updated"));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Require(frames.World.Read<TableText>(child).Value == "changed:updated", "Changed sources update the displayed text.");
        uint textVersion = frames.World.WriteVersion<TableText>(child);
        frames.Frame();
        Require(frames.World.WriteVersion<TableText>(child) == textVersion, "A later frame retains the displayed text version.");
    }

    private static void VerifyObjectState(HeadlessFrameDriver frames)
    {
        var count = new State<int>(3);
        var text = new TextData("first");
        Entity root = Views.Spawn(frames.World, Entity.Null, new StateAndObjectText(count, text));
        frames.Frame();
        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Require(frames.World.Read<TableText>(child).Value == "first:3", "State and object reads supply the initial text.");

        Task.Run(() =>
        {
            count.Update(static (ref int value) => value++);
            count.Update(3, static (ref int value, int amount) => value += amount);
            text.Value = "shared";
            text.AdvanceShared();
        }).GetAwaiter().GetResult();
        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Require(frames.World.Read<TableText>(child).Value == "shared:7", "Cross-thread writes update the next frame's text.");

        uint version = count.Version;
        count.Value = 7;
        count.Update(static (ref int value) => value += 0);
        frames.Frame();
        Require(count.Version == version, "Equal changes retain the State version.");
        Require(frames.World.Read<TableText>(child).Value == "shared:7", "Equal changes retain the displayed text.");
        text.Value = "changed";
        text.Advance();
        frames.Frame();
        Require(frames.World.Read<TableText>(child).Value == "changed:7", "A custom version cell updates the displayed text.");
    }

    private static void VerifyUntrackedReads(HeadlessFrameDriver frames)
    {
        Entity sample = frames.World.CreateEntity(new TableText("table"));
        frames.World.AddSparse(sample, new SparseText("sparse"));
        Entity trigger = frames.World.CreateEntity(new TableText("first"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new SampledText(sample, trigger));

        frames.Frame();

        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Require(frames.World.Read<TableText>(child).Value == "table:sparse:first",
            "Untracked reads supply table and sparse values.");
        frames.World.Replace(sample, new TableText("changed"));
        frames.World.ReplaceSparse(sample, new SparseText("updated"));

        frames.Frame();

        Require(frames.World.Read<TableText>(child).Value == "table:sparse:first",
            "Untracked source writes retain the displayed values.");
        frames.World.Replace(trigger, new TableText("second"));

        frames.Frame();

        Require(frames.World.Read<TableText>(child).Value == "changed:updated:second",
            "An outside dependency refreshes the displayed values.");
    }

    private static void VerifyBuildErrors()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        frames.World.AddSparse(source, new SparseText("before"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ErrorContent(source));
        frames.Frame();
        Entity branch = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, branch).ToArray();
        frames.World.ReplaceSparse(source, new SparseText("failed"));

        frames.Frame();
        frames.Frame();

        BuildError error = frames.World.ReadSparse<BuildError>(branch);
        Require(error.Frame == 2 && error.Exception.Message == "content failed", "The child retains its exception and frame.");
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, branch).Span.SequenceEqual(children),
            "A failed build retains its complete children.");
        Require(frames.World.Read<TableText>(children[0]).Value == "before", "The previous child parameters survive.");
        Require(frames.World.Read<TableText>(children[1]).Value == "last", "The second child parameters survive.");
        Entity fallback = Hierarchy<ElementTree>.GetChildren(frames.World, root)[1];
        Require(frames.World.Read<TableText>(fallback).Value == "content failed", "The ancestor displays the child's error.");

        frames.World.ReplaceSparse(source, new SparseText("after"));
        frames.Frame();
        frames.Frame();

        Require(!frames.World.HasSparse<BuildError>(branch), "The recovered child clears its error.");
        Require(frames.World.Read<TableText>(children[0]).Value == "after", "A changed dependency restores content.");
        Require(!frames.World.IsAlive(fallback), "The ancestor releases its fallback after recovery.");
        Entity content = Hierarchy<ElementTree>.GetChildren(frames.World, root)[1];
        Require(frames.World.Read<TableText>(content).Value == "content", "The ancestor restores its content.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void VerifyCallerChildren(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new ContentGroup(3, 11, true));
        frames.Frame();
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, container).ToArray();
        Require(children.Length == 2, "Content declares both branches under its container.");
        Entity firstLeaf = Hierarchy<ElementTree>.GetChildren(frames.World, children[0])[0];
        Entity secondLeaf = Hierarchy<ElementTree>.GetChildren(frames.World, children[1])[0];
        frames.World.Replace(container, new ContentContainer(7));

        frames.Frame();

        Require(frames.World.Read<Selection>(container).Value == 7,
            "Caller containers rebuild their own data from changed parameters.");
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, container).Span.SequenceEqual(children),
            "Caller container rebuilds preserve the declared children.");
        uint displayVersion = frames.World.WriteVersion<Selection>(container);
        frames.World.Replace(root, new ContentGroup(7, 33, false));

        frames.Frame();

        var content = Hierarchy<ElementTree>.GetChildren(frames.World, container);
        Require(content.Count == 1 && content[0] == children[0], "Content keeps the remaining branch entity.");
        Require(frames.World.WriteVersion<Selection>(container) == displayVersion,
            "Equal container parameters preserve the container data version.");
        Require(frames.World.Read<TableText>(firstLeaf).Value == "33",
            "Caller changes update nested content while the container parameters stay equal.");
        Require(!frames.World.IsAlive(children[1]) && !frames.World.IsAlive(secondLeaf),
            "Content completion releases the removed branch and its subtree.");

        Entity source = frames.World.CreateEntity(new TableText("first"));
        frames.World.AddSparse(source, new SparseText("source"));
        Entity readingRoot = Views.Spawn(frames.World, Entity.Null, new ContentText(source));
        frames.Frame();
        Entity group = Hierarchy<ElementTree>.GetChildren(frames.World, readingRoot)[0];
        Entity text = Hierarchy<ElementTree>.GetChildren(frames.World, group)[0];
        Require(frames.World.Read<TableText>(text).Value == "first:source",
            "Content reads the caller's table and sparse sources.");
        frames.World.Replace(source, new TableText("changed"));
        frames.World.ReplaceSparse(source, new SparseText("updated"));

        frames.Frame();

        Require(frames.World.Read<TableText>(text).Value == "changed:updated",
            "Changed content sources rebuild the caller's children.");
    }

    private readonly record struct ContentText(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Group()).Content())
            {
                string table = Scope.Read<TableText>(Source).Value;
                string sparse = Scope.Read<SparseText>(Source).Value;
                Scope.View(new TableText($"{table}:{sparse}"));
            }
            return default;
        }
    }

    private readonly record struct ContentGroup(int Value, ulong First, bool IncludeSecond) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new ContentContainer(Value)).Content())
            {
                Scope.View(new KeyedBranch(First), 11);
                if (IncludeSecond)
                    Scope.View(new KeyedBranch(22), 22);
            }
            return default;
        }
    }

    private readonly record struct ContentContainer(int Value) : IView, IComponent
    {
        public static ChildSource Children => ChildSource.Caller;

        public BuildTask Build()
        {
            if (Scope.World.Has<Selection>(Scope.Entity))
                Scope.World.Replace(Scope.Entity, new Selection(Value));
            else
                Scope.World.Add(Scope.Entity, new Selection(Value));
            return default;
        }
    }

    private readonly record struct ReferenceGroup(string Name, Func<int> NumberCallback) : IView, ISparseComponent
    {
        public async BuildTask Build()
        {
            await Task.CompletedTask;
            await default(BuildTask);
            Scope.View(new TableText("first"));
            Scope.View(new SparseBranch(Name, NumberCallback));
            Scope.View(new TableText("last"));
        }
    }

    private readonly record struct SparseBranch(string Name, Func<int> NumberCallback) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            Scope.View(new TableText($"{Name}:{NumberCallback()}"));
            return default;
        }
    }

    private readonly record struct TableText(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct SparseText(string Value) : ISparseComponent;

    private sealed class TextData(string value) : VersionCell
    {
        internal string Value = value;
    }

    private readonly record struct StateAndObjectText(State<int> Count, TextData Text) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Depend(Text);
            Scope.View(new TableText($"{Text.Value}:{Count.Value}"));
            return default;
        }
    }

    private readonly record struct ErrorContent(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            Entity branch = Scope.View(new FailingContent(Source), 1);
            if (Scope.Has<BuildError>(branch))
            {
                string message = Scope.Read<BuildError>(branch).Exception.Message;
                Scope.View(new TableText(message), 2);
            }
            else
            {
                Scope.View(new TableText("content"), 3);
            }
            return default;
        }
    }

    private readonly record struct FailingContent(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            string value = Scope.Read<SparseText>(Source).Value;
            if (value == "failed")
            {
                Scope.View(new TableText("changed"), 2);
                Scope.View(new TableText("added"), 3);
                throw new InvalidOperationException("content failed");
            }
            Scope.View(new TableText(value), 1);
            Scope.View(new TableText("last"), 2);
            return default;
        }
    }

    private readonly record struct ComponentText(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            string table = Scope.Read<TableText>(Source).Value;
            string sparse = Scope.Read<SparseText>(Source).Value;
            Scope.View(new TableText($"{table}:{sparse}"));
            return default;
        }
    }

    private readonly record struct SampledText(Entity Sample, Entity Trigger) : IView, IComponent
    {
        public BuildTask Build()
        {
            string table;
            string sparse;
            using (Scope.Untrack())
            {
                table = Scope.Read<TableText>(Sample).Value;
                sparse = Scope.Read<SparseText>(Sample).Value;
            }
            string trigger = Scope.Read<TableText>(Trigger).Value;
            Scope.View(new TableText($"{table}:{sparse}:{trigger}"));
            return default;
        }
    }

    private readonly record struct KeyedGroup(ulong[] Ids, bool TextOnly = false) : IView, IComponent
    {
        public BuildTask Build()
        {
            foreach (ulong id in Ids)
            {
                using (Scope.Key(id))
                {
                    if (TextOnly)
                        Scope.View(new TableText(id.ToString()), 0);
                    else
                        Scope.View(new KeyedBranch(id), 0);
                }
            }
            return default;
        }
    }

    private readonly record struct KeyedBranch(ulong Id) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            if (!Scope.World.Has<Selection>(Scope.Entity))
            {
                Scope.World.Add(Scope.Entity, new Selection(0));
                Scope.World.AddTag<LocalState>(Scope.Entity);
            }
            Scope.View(new TableText(Id.ToString()));
            return default;
        }
    }

    private readonly record struct Selection(int Value) : IComponent;

    private readonly record struct OnceText(string Value) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Scope.View(new TableText(Value));
            return default;
        }
    }
}
