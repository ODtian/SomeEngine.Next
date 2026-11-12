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

        var children = ElementChildren.Read(frames.World, root);
        Require(children.Length == 3, "The root has three declared children.");
        Require(frames.World.ReadComponentData<TableText>(children[0]).Value == "first", "The first child retains its table parameters.");
        Require(frames.World.ReadComponentData<ReferenceGroup>(root).Name == "vvvvv", "The root retains its sparse string parameter.");
        Require(frames.World.ReadComponentData<ReferenceGroup>(root).NumberCallback() == 41, "The root retains its callback parameter.");
        Require(frames.World.ReadComponentData<TableText>(children[2]).Value == "last", "The last child retains its declaration position.");
        var grandchildren = ElementChildren.Read(frames.World, children[1]);
        Require(grandchildren.Length == 1, "The branch creates its child during the first frame.");
        Require(frames.World.ReadComponentData<TableText>(grandchildren[0]).Value == "vvvvv:41", "The branch builds from its sparse parameters.");
        Require(frames.World.ReadComponentData<ViewType>(children[0]).Id == frames.World.ReadComponentData<ViewType>(children[2]).Id,
            "Equal view types share a type number.");
        Require(frames.World.ReadComponentData<ViewType>(children[0]).Id != frames.World.ReadComponentData<ViewType>(children[1]).Id,
            "Different view types have different type numbers.");

        VerifyParameterChanges(frames, root, children.ToArray(), grandchildren[0]);
        VerifyOnceParameters(frames);
        VerifyKeyedChildren(frames);
        VerifySpawnedChildParameters(frames);
        VerifyCallerChildren(frames);
        VerifyReadDependencies(frames);
        VerifyAsyncBuilds(frames);
        AsyncIntegrationChecks.VerifyWrites();
        AsyncIntegrationChecks.VerifyComputations();
        AsyncIntegrationChecks.VerifyRecoveredReads(cancel: false);
        AsyncIntegrationChecks.VerifyRecoveredReads(cancel: true);
        AsyncIntegrationChecks.VerifyRecoveredParameters(cancel: false);
        AsyncIntegrationChecks.VerifyRecoveredParameters(cancel: true);
        AsyncIntegrationChecks.VerifyFailureRecovery();
        AsyncIntegrationChecks.VerifyFailureRecovery(nested: true);
        AsyncIntegrationChecks.VerifyObjectFrames();
        AsyncIntegrationChecks.VerifyRestoredBuild();
        JobBuildChecks.VerifyResult(completed: false);
        JobBuildChecks.VerifyResult(completed: true);
        VerifyObjectState(frames);
        ComputationChecks.Verify();
        VerifyBuildErrors();
        ComponentWriteChecks.Verify();
        StructureDependencyChecks.Verify();
        VerifyUntrackedReads(frames);
    }

    private static void VerifySpawnedChildParameters(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new HistoryGroup(7));
        Entity child = Views.Spawn(frames.World, root, new HistoryText(3));
        frames.Frame();
        Require(frames.World.ReadComponentData<TextHistory>(child).Value == "7",
            "The parent's first build presents the child's new parameters once.");
        ulong version = frames.World.ComponentDataWriteVersion<TextHistory>(child);
        frames.Frame();
        Require(frames.World.ComponentDataWriteVersion<TextHistory>(child) == version,
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
                Scope.World.Set(Scope.Entity, new TextHistory($"{Scope.World.ReadComponentData<TextHistory>(Scope.Entity).Value},{Value}"));
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
        ulong branchVersion = frames.World.ComponentDataWriteVersion<SparseBranch>(children[1]);
        ulong leafVersion = frames.World.ComponentDataWriteVersion<TableText>(leaf);
        frames.World.SetSparse(root, new ReferenceGroup("vvvvv", ReadNumber));

        frames.Frame();

        Require(ElementChildren.Read(frames.World, root).AsSpan().SequenceEqual(children),
            "Equal child parameters retain the declared entities.");
        Require(frames.World.ComponentDataWriteVersion<SparseBranch>(children[1]) == branchVersion,
            "Equal sparse parameters retain their write version.");
        Require(frames.World.ComponentDataWriteVersion<TableText>(leaf) == leafVersion,
            "Equal nested parameters retain their write version.");
        frames.World.SetSparse(root, new ReferenceGroup("updated", ReadChangedNumber));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Require(ElementChildren.Read(frames.World, root).AsSpan().SequenceEqual(children),
            "Changed child parameters retain the declared entities.");
        Require(frames.World.ReadComponentData<SparseBranch>(children[1]).NumberCallback() == 53,
            "Changed sparse parameters retain the latest callback.");
        Require(frames.World.ReadComponentData<TableText>(leaf).Value == "updated:53",
            "Changed sparse parameters rebuild the nested table text within the frame.");
        leafVersion = frames.World.ComponentDataWriteVersion<TableText>(leaf);
        frames.Frame();
        Require(frames.World.ComponentDataWriteVersion<TableText>(leaf) == leafVersion,
            "A later frame retains the completed parameter update.");
    }

    private static void VerifyOnceParameters(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceText("first"));
        frames.Frame();
        Entity child = ElementChildren.Read(frames.World, root)[0];
        frames.World.Set(root, new OnceText("updated"));

        frames.Frame();

        Require(frames.World.ReadComponentData<OnceText>(root).Value == "updated", "Once stores the latest parameters.");
        Require(frames.World.ReadComponentData<TableText>(child).Value == "first", "Once retains its first content.");
    }

    private static void VerifyKeyedChildren(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new KeyedGroup([11, 22, 33]));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        Entity[] leaves = rows.Select(row => ElementChildren.Read(frames.World, row)[0]).ToArray();
        frames.World.Set(rows[1], new Selection(7));
        frames.World.Set(root, new KeyedGroup([33, 11, 22]));

        frames.Frame();

        Require(ElementChildren.Read(frames.World, root).AsSpan().SequenceEqual([rows[2], rows[0], rows[1]]),
            "Key scopes preserve each row entity in the new declaration order.");
        Require(frames.World.ReadComponentData<Selection>(rows[1]).Value == 7, "Keyed rows preserve their local state.");
        Require(frames.World.ReadComponentData<ElementKey>(rows[1]).Key == 22, "The row retains its declared key.");
        frames.World.Set(root, new KeyedGroup([33, 22]));

        frames.Frame();

        Require(ElementChildren.Read(frames.World, root).AsSpan().SequenceEqual([rows[2], rows[1]]),
            "Deleting an item preserves the remaining keyed rows.");
        Require(!(frames.World.IsAlive(rows[0]) && frames.World.Has<ViewType>(rows[0])) && !(frames.World.IsAlive(leaves[0]) && frames.World.Has<ViewType>(leaves[0])),
            "Deleting an item releases its row and subtree.");
        frames.World.Set(root, new KeyedGroup([33, 22], TextOnly: true));

        frames.Frame();

        Entity[] texts = ElementChildren.Read(frames.World, root);
        Require(texts.Length == 2 && frames.World.ReadComponentData<TableText>(texts[0]).Value == "33" &&
            frames.World.ReadComponentData<TableText>(texts[1]).Value == "22", "Type changes create the declared replacement views.");
        Require(!(frames.World.IsAlive(rows[1]) && frames.World.Has<ViewType>(rows[1])) && !(frames.World.IsAlive(rows[2]) && frames.World.Has<ViewType>(rows[2])) &&
            !(frames.World.IsAlive(leaves[1]) && frames.World.Has<ViewType>(leaves[1])) && !(frames.World.IsAlive(leaves[2]) && frames.World.Has<ViewType>(leaves[2])),
            "Type changes release the previous rows and their subtrees.");
    }

    private static void VerifyReadDependencies(HeadlessFrameDriver frames)
    {
        Entity source = frames.World.Create(new TableText("table"));
        frames.World.SetSparse(source, new SparseText("sparse"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ComponentText(source));
        ulong sourceVersion = frames.World.ComponentDataWriteVersion<TableText>(source);

        frames.Frame();

        Entity child = ElementChildren.Read(frames.World, root)[0];
        Require(frames.World.ReadComponentData<TableText>(child).Value == "table:sparse", "Reads supply table and sparse text.");
        Require(frames.World.ComponentDataWriteVersion<TableText>(source) == sourceVersion, "A read preserves the source write version.");
        frames.World.Set(source, new TableText("changed"));
        frames.World.SetSparse(source, new SparseText("updated"));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Require(frames.World.ReadComponentData<TableText>(child).Value == "changed:updated", "Changed sources update the displayed text.");
        ulong textVersion = frames.World.ComponentDataWriteVersion<TableText>(child);
        frames.Frame();
        Require(frames.World.ComponentDataWriteVersion<TableText>(child) == textVersion, "A later frame retains the displayed text version.");
    }

    private static void VerifyAsyncBuilds(HeadlessFrameDriver frames)
    {
        Entity steps = Views.Spawn(frames.World, Entity.Null, new FrameSequence());
        frames.Frame();
        Require(frames.World.ReadComponentData<Selection>(steps).Value == 0, "The sequence starts at its first visible value.");
        for (int step = 1; step <= 30; step++)
        {
            frames.Frame();
            Require(frames.World.ReadComponentData<Selection>(steps).Value == step, "Each frame displays its next sequence value.");
        }

        var completion = new TaskCompletionSource<string>();
        Entity loading = Views.Spawn(frames.World, Entity.Null, new LoadedText(completion.Task));
        frames.Frame();
        Require(frames.World.ReadComponentData<TableText>(loading).Value == "loading", "The loading text is immediately visible.");
        var worker = new Thread(() => completion.SetResult("ready"));
        worker.Start();
        worker.Join();
        Require(frames.World.ReadComponentData<TableText>(loading).Value == "loading", "The loading text remains until rebuilding.");
        frames.Frame();
        Require(frames.World.ReadComponentData<TableText>(loading).Value == "ready", "Worker completion supplies the rebuilt text.");
        Entity child = ElementChildren.Read(frames.World, loading)[0];
        Require(frames.World.ReadComponentData<TableText>(child).Value == "ready", "The resumed build creates its text child.");

        completion = new TaskCompletionSource<string>();
        Entity contentRoot = Views.Spawn(frames.World, Entity.Null, new ContentLoad(completion.Task));
        frames.Frame();
        Entity group = ElementChildren.Read(frames.World, contentRoot)[0];
        worker = new Thread(() => completion.SetResult("content ready"));
        worker.Start();
        worker.Join();
        frames.Frame();
        frames.Frame();
        var content = ElementChildren.Read(frames.World, group);
        Require(content.Length == 2 && frames.World.ReadComponentData<TableText>(content[1]).Value == "content ready",
            "A suspended content helper retains its container through the calling build.");
    }

    private readonly record struct ContentLoad(Task<string> Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Entity group = Scope.View(new Group());
            BuildTask content;
            using (group.Content())
                content = LoadChildren(Result);
            await content;
        }

        private static async BuildTask LoadChildren(Task<string> result)
        {
            Scope.View(new TableText("loading"));
            string text = await result.ConfigureAwait(false);
            Scope.View(new TableText(text));
        }
    }

    private readonly record struct FrameSequence : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new Selection(0));
            for (int step = 1; step <= 30; step++)
            {
                await Scope.NextFrame();
                Scope.World.Set(Scope.Entity, new Selection(step));
            }
        }
    }

    private readonly record struct LoadedText(Task<string> Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.World.Add(Scope.Entity, new TableText("loading"));
            string result = await Result.ConfigureAwait(false);
            Scope.World.Set(Scope.Entity, new TableText(result));
            Scope.View(new TableText(result));
        }
    }

    private static void VerifyObjectState(HeadlessFrameDriver frames)
    {
        var count = new State<int>(3);
        var text = new TextData("first");
        Entity root = Views.Spawn(frames.World, Entity.Null, new StateAndObjectText(count, text));
        frames.Frame();
        Entity child = ElementChildren.Read(frames.World, root)[0];
        Require(frames.World.ReadComponentData<TableText>(child).Value == "first:3", "State and object reads supply the initial text.");

        Task.Run(() =>
        {
            count.Update(static (ref int value) => value++);
            count.Update(3, static (ref int value, int amount) => value += amount);
            text.Value = "shared";
            text.AdvanceShared();
        }).GetAwaiter().GetResult();
        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Require(frames.World.ReadComponentData<TableText>(child).Value == "shared:7", "Cross-thread writes update the next frame's text.");

        uint version = count.Version;
        count.Value = 7;
        count.Update(static (ref int value) => value += 0);
        frames.Frame();
        Require(count.Version == version, "Equal changes retain the State version.");
        Require(frames.World.ReadComponentData<TableText>(child).Value == "shared:7", "Equal changes retain the displayed text.");
        text.Value = "changed";
        text.Advance();
        frames.Frame();
        Require(frames.World.ReadComponentData<TableText>(child).Value == "changed:7", "A custom version cell updates the displayed text.");

        var writing = new TextData("writing") { Version = 1 };
        Entity writingRoot = Views.Spawn(frames.World, Entity.Null, new StateAndObjectText(count, writing));
        frames.Frame();
        Entity writingChild = ElementChildren.Read(frames.World, writingRoot)[0];
        Require(frames.World.ReadComponentData<TableText>(writingChild).Value == "writing:7", "An odd version supplies this frame's text.");
        writing.Value = "continuing";
        frames.Frame();
        Require(frames.World.ReadComponentData<TableText>(writingChild).Value == "continuing:7", "An ongoing write updates the next frame's text.");
        writing.Value = "completed";
        writing.Version = 2;
        frames.Frame();
        Require(frames.World.ReadComponentData<TableText>(writingChild).Value == "completed:7", "The completed version supplies its text.");
    }

    private static void VerifyUntrackedReads(HeadlessFrameDriver frames)
    {
        Entity sample = frames.World.Create(new TableText("table"));
        frames.World.SetSparse(sample, new SparseText("sparse"));
        Entity trigger = frames.World.Create(new TableText("first"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new SampledText(sample, trigger));

        frames.Frame();

        Entity child = ElementChildren.Read(frames.World, root)[0];
        Require(frames.World.ReadComponentData<TableText>(child).Value == "table:sparse:first",
            "Untracked reads supply table and sparse values.");
        frames.World.Set(sample, new TableText("changed"));
        frames.World.SetSparse(sample, new SparseText("updated"));

        frames.Frame();

        Require(frames.World.ReadComponentData<TableText>(child).Value == "table:sparse:first",
            "Untracked source writes retain the displayed values.");
        frames.World.Set(trigger, new TableText("second"));

        frames.Frame();

        Require(frames.World.ReadComponentData<TableText>(child).Value == "changed:updated:second",
            "An outside dependency refreshes the displayed values.");
    }

    private static void VerifyBuildErrors()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create();
        frames.World.SetSparse(source, new SparseText("before"));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ErrorContent(source));
        frames.Frame();
        Entity branch = ElementChildren.Read(frames.World, root)[0];
        Entity[] children = ElementChildren.Read(frames.World, branch);
        frames.World.SetSparse(source, new SparseText("failed"));

        frames.Frame();
        frames.Frame();

        BuildError error = frames.World.ReadComponentData<BuildError>(branch);
        Require(error.Frame == 2 && error.Exception is InvalidOperationException, "The child retains its exception and frame.");
        Require(ElementChildren.Read(frames.World, branch).AsSpan().SequenceEqual(children),
            "A failed build retains its complete children.");
        Require(frames.World.ReadComponentData<TableText>(children[0]).Value == "before", "The previous child parameters survive.");
        Require(frames.World.ReadComponentData<TableText>(children[1]).Value == "last", "The second child parameters survive.");
        Entity fallback = ElementChildren.Read(frames.World, root)[1];
        Require(frames.World.ReadComponentData<TableText>(fallback).Value == "content failed", "The ancestor displays the child's error.");

        frames.World.SetSparse(source, new SparseText("after"));
        frames.Frame();
        frames.Frame();

        Require(!frames.World.HasSparse<BuildError>(branch), "The recovered child clears its error.");
        Require(frames.World.ReadComponentData<TableText>(children[0]).Value == "after", "A changed dependency restores content.");
        Require(!(frames.World.IsAlive(fallback) && frames.World.Has<ViewType>(fallback)), "The ancestor releases its fallback after recovery.");
        Entity content = ElementChildren.Read(frames.World, root)[1];
        Require(frames.World.ReadComponentData<TableText>(content).Value == "content", "The ancestor restores its content.");
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
        Entity container = ElementChildren.Read(frames.World, root)[0];
        Entity[] children = ElementChildren.Read(frames.World, container);
        Require(children.Length == 2, "Content declares both branches under its container.");
        Entity firstLeaf = ElementChildren.Read(frames.World, children[0])[0];
        Entity secondLeaf = ElementChildren.Read(frames.World, children[1])[0];
        frames.World.Set(container, new ContentContainer(7));

        frames.Frame();

        Require(frames.World.ReadComponentData<Selection>(container).Value == 7,
            "Caller containers rebuild their own data from changed parameters.");
        Require(ElementChildren.Read(frames.World, container).AsSpan().SequenceEqual(children),
            "Caller container rebuilds preserve the declared children.");
        ulong displayVersion = frames.World.ComponentDataWriteVersion<Selection>(container);
        frames.World.Set(root, new ContentGroup(7, 33, false));

        frames.Frame();

        var content = ElementChildren.Read(frames.World, container);
        Require(content.Length == 1 && content[0] == children[0], "Content keeps the remaining branch entity.");
        Require(frames.World.ComponentDataWriteVersion<Selection>(container) == displayVersion,
            "Equal container parameters preserve the container data version.");
        Require(frames.World.ReadComponentData<TableText>(firstLeaf).Value == "33",
            "Caller changes update nested content while the container parameters stay equal.");
        Require(!(frames.World.IsAlive(children[1]) && frames.World.Has<ViewType>(children[1])) && !(frames.World.IsAlive(secondLeaf) && frames.World.Has<ViewType>(secondLeaf)),
            "Content completion releases the removed branch and its subtree.");

        Entity source = frames.World.Create(new TableText("first"));
        frames.World.SetSparse(source, new SparseText("source"));
        Entity readingRoot = Views.Spawn(frames.World, Entity.Null, new ContentText(source));
        frames.Frame();
        Entity group = ElementChildren.Read(frames.World, readingRoot)[0];
        Entity text = ElementChildren.Read(frames.World, group)[0];
        Require(frames.World.ReadComponentData<TableText>(text).Value == "first:source",
            "Content reads the caller's table and sparse sources.");
        frames.World.Set(source, new TableText("changed"));
        frames.World.SetSparse(source, new SparseText("updated"));

        frames.Frame();

        Require(frames.World.ReadComponentData<TableText>(text).Value == "changed:updated",
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
                Scope.World.Set(Scope.Entity, new Selection(Value));
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

    private readonly record struct TableText(string Value) : IView, ITrackedComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct SparseText(string Value) : ISparseComponent, ITrackedComponent;

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
                Scope.World.Set(Scope.Entity, default(LocalState));
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
