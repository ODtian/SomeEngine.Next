using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.Views.Tests;

/// <summary>Combined asynchronous view behavior used by managed and native consumers.</summary>
internal static partial class AsyncIntegrationChecks
{
    internal static void VerifyWrites()
    {
        using var frames = new HeadlessFrameDriver();
        Entity target = frames.World.CreateEntity(new Number(1));
        frames.World.AddSparse(target, new SparseNumber(2));
        var completion = new TaskCompletionSource();
        Entity root = Views.Spawn(frames.World, Entity.Null, new WrittenContent(target, completion.Task));
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 10, "The first segment commits its table value.");
        Require(frames.World.ReadSparse<SparseNumber>(target).Value == 20, "The first segment commits its sparse value.");
        frames.World.Replace(target, new Number(40));
        frames.World.ReplaceSparse(target, new SparseNumber(50));
        completion.SetResult();

        frames.Frame();

        Require(frames.World.Read<Number>(target).Value == 41, "The resumed segment writes from the current table value.");
        Require(frames.World.ReadSparse<SparseNumber>(target).Value == 51, "The resumed segment writes from the current sparse value.");
        Entity group = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity label = Hierarchy<ElementTree>.GetChildren(frames.World, group)[0];
        Require(frames.World.Read<NumberLabel>(label).Value == 41, "Content uses the calling calculation's current copy.");
    }

    internal static void VerifyComputations()
    {
        using var frames = new HeadlessFrameDriver();
        Entity target = frames.World.CreateEntity(new Number());
        frames.World.AddSparse(target, new SparseNumber());
        var number = new State<int>(3);
        var completion = new TaskCompletionSource();
        Views.Spawn(frames.World, Entity.Null, new ComputedContent(number, target, completion.Task));
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 6, "The first segment creates its Memo binding.");
        number.Value = 7;
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 14, "The binding refreshes while the view awaits its result.");
        completion.SetResult();
        frames.Frame();
        Require(frames.World.ReadSparse<SparseNumber>(target).Value == 8, "The resumed segment creates another calculation.");
        number.Value = 9;
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 18, "The Memo binding survives asynchronous completion.");
        Require(frames.World.ReadSparse<SparseNumber>(target).Value == 10, "The later calculation refreshes through shared dependency checks.");
    }

    internal static void VerifyFailureRecovery(bool nested = false)
    {
        using var frames = new HeadlessFrameDriver();
        Entity target = frames.World.CreateEntity(new Number());
        frames.World.AddSparse(target, new SparseNumber());
        var first = new State<int>(3);
        var second = new State<int>(7);
        var error = new InvalidOperationException("resumed content failed");
        Entity root = nested
            ? Views.Spawn(frames.World, Entity.Null, new RecoveringGroup(first, target, 3, null, error))
            : Views.Spawn(frames.World, Entity.Null, new RecoveringContent(first, target, 3, null, error));
        frames.Frame();
        Entity content = nested ? Hierarchy<ElementTree>.GetChildren(frames.World, root)[0] : root;
        var completion = new TaskCompletionSource();
        if (nested)
            frames.World.Replace(root, new RecoveringGroup(second, target, 7, completion.Task, error));
        else
            frames.World.Replace(root, new RecoveringContent(second, target, 7, completion.Task, error));

        frames.Frame();

        Require(frames.World.Read<Number>(target).Value == 14, "The suspended build exposes its new binding.");
        Require(frames.World.ReadSparse<SparseNumber>(target).Value == 7, "The first segment exposes its sparse write.");
        Require(frames.World.Has<AddedNumber>(target), "The suspended build exposes its added component.");
        completion.SetResult();
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 100, "A later segment commits another table value.");
        Require(frames.World.ReadSparse<SparseNumber>(target).Value == 200, "A later segment commits another sparse value.");

        frames.Frame();

        Require(ReferenceEquals(frames.World.ReadSparse<BuildError>(content).Exception, error),
            "The final segment records the original error.");
        Require(frames.World.ReadSparse<BuildError>(content).Frame == 4, "The error records its resumed frame.");
        Require(frames.World.Read<Number>(target).Value == 6, "Failure restores the previous table output.");
        Require(frames.World.ReadSparse<SparseNumber>(target).Value == 3, "Failure restores the previous sparse output.");
        Require(!frames.World.Has<AddedNumber>(target), "Failure restores the previous component presence.");
        first.Value = 9;
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 18, "The previous Memo binding refreshes after recovery.");
        second.Value = 11;
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 18, "The restored binding retains its own source.");
    }

    internal static void VerifyObjectFrames()
    {
        using var frames = new HeadlessFrameDriver();
        var text = new SharedText { Value = 7, Version = 1 };
        Entity root = Views.Spawn(frames.World, Entity.Null, new ObjectContent(text));
        frames.Frame();
        Require(frames.World.Read<Number>(root).Value == 0, "The first segment presents loading data.");
        text.Version = 2;
        frames.Frame();
        Require(frames.World.Read<Number>(root).Value == 0, "A published object write restarts the waiting view.");
        frames.Frame();
        Require(frames.World.Read<Number>(root).Value == 7, "The resumed read settles within its own frame.");
        text.Value = 11;
        text.Version = 4;
        frames.Frame();
        frames.Frame();
        Require(frames.World.Read<Number>(root).Value == 11, "A later object version refreshes the asynchronous view.");
    }

    internal static void VerifyRestoredBuild()
    {
        var registry = new SerializationRegistry();
        ViewSerialization.Register(registry);
        var type = new SerializationTypeKey(new Guid("02904d5f-dca0-4ab9-8d85-261c917db2a0"), "StoredAsyncView", 1);
        registry.Register<StoredAsyncView, StoredAsyncViewCodec>(type);
        ViewSerialization.RegisterType<StoredAsyncView>(type);
        registry.Register<Number, NumberCodec>(new SerializationTypeKey(
            new Guid("74042e4b-afb6-4b5f-9bdf-6be03f660fc1"), "AsyncNumber", 1));
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new StoredAsyncView(17));
        frames.Frame();
        frames.Frame();
        using var stream = new MemoryStream();
        WorldCheckpointCodec.Write(stream, frames.World, registry);
        stream.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(stream, registry));
        restored.Frame();
        Require(restored.World.Read<Number>(root).Value == 0, "The restored Once view starts its asynchronous build.");
        restored.Frame();
        Require(restored.World.Read<Number>(root).Value == 17, "The restored build resumes with its saved parameters.");
        restored.Frame();
        Require(restored.World.Read<Number>(root).Value == 17, "The completed restored view retains its result.");
    }

    private readonly record struct StoredAsyncView(int Value) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public async BuildTask Build()
        {
            Scope.Write<Number>(Scope.Entity) = new Number();
            await Scope.NextFrame();
            Scope.Write<Number>(Scope.Entity) = new Number(Value);
        }
    }

    private struct StoredAsyncViewCodec : IComponentCodec<StoredAsyncView>
    {
        public void Write(ref DataWriter writer, in StoredAsyncView value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out StoredAsyncView value) => value = new StoredAsyncView(reader.ReadInt32());
    }

    private struct NumberCodec : IComponentCodec<Number>
    {
        public void Write(ref DataWriter writer, in Number value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out Number value) => value = new Number(reader.ReadInt32());
    }

    private sealed class SharedText : VersionCell
    {
        internal int Value;
    }

    private readonly record struct ObjectContent(SharedText Text) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.Depend(Text);
            Scope.Write<Number>(Scope.Entity) = new Number();
            await Scope.NextFrame();
            Scope.Depend(Text);
            Scope.Write<Number>(Scope.Entity) = new Number(Text.Value);
        }
    }

    private readonly record struct ComputedContent(State<int> Number, Entity Target, Task Result) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public async BuildTask Build()
        {
            State<int> number = Number;
            Entity target = Target;
            Memo<int> doubled = Scope.Memo(() => number.Value * 2);
            Scope.Compute(() => Scope.Write<Number>(target) = new Number(doubled.Value));
            await Result;
            Scope.Compute(() => Scope.Write<SparseNumber>(target) = new SparseNumber(number.Value + 1));
        }
    }

    private readonly record struct RecoveringContent(State<int> Number, Entity Target, int Value, Task? Result,
        Exception Error) : IView, IComponent
    {
        public async BuildTask Build()
        {
            State<int> number = Number;
            Entity target = Target;
            Memo<int> doubled = Scope.Memo(() => number.Value * 2);
            Scope.Compute(() => Scope.Write<Number>(target) = new Number(doubled.Value));
            Scope.Write<SparseNumber>(target) = new SparseNumber(Value);
            if (Result is null)
                return;
            Scope.Ensure<AddedNumber>(target);
            await Result;
            Scope.Write<Number>(target) = new Number(100);
            Scope.Write<SparseNumber>(target) = new SparseNumber(200);
            await Scope.NextFrame();
            throw Error;
        }
    }

    private readonly record struct RecoveringGroup(State<int> Number, Entity Target, int Value, Task? Result,
        Exception Error) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new RecoveringContent(Number, Target, Value, Result, Error), 1);
            return default;
        }
    }

    private readonly record struct WrittenContent(Entity Target, Task Result) : IView, IComponent
    {
        public async BuildTask Build()
        {
            Scope.Write<Number>(Target) = new Number(10);
            Scope.Write<SparseNumber>(Target) = new SparseNumber(20);
            await Result;
            Entity group = Scope.View(new Group(), 1);
            using (group.Content())
            {
                Scope.Write<Number>(Target) = new Number(Scope.Write<Number>(Target).Value + 1);
                Scope.Write<SparseNumber>(Target) = new SparseNumber(Scope.Write<SparseNumber>(Target).Value + 1);
                Scope.View(new NumberLabel(Scope.Write<Number>(Target).Value), 1);
            }
        }
    }

    private readonly record struct Number(int Value) : IComponent;
    private readonly record struct SparseNumber(int Value) : ISparseComponent;
    private readonly record struct AddedNumber(int Value) : IComponent;
    private readonly record struct NumberLabel(int Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
