using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class UntrackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntrackedReadsKeepTheDisplayUntilAnOutsideReadChanges(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity sample = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(sample, new SparseValue(3));
        else
            frames.World.Add(sample, new Value(3));
        Entity trigger = frames.World.CreateEntity(new Value(5));
        Entity root = Views.Spawn(frames.World, Entity.Null, new SampleDisplay(sample, trigger, sparse));

        frames.Frame();
        Assert.Equal(new DisplayValues(3, 5), frames.World.Read<DisplayValues>(root));

        if (sparse)
            frames.World.ReplaceSparse(sample, new SparseValue(7));
        else
            frames.World.Replace(sample, new Value(7));
        frames.Frame();
        Assert.Equal(new DisplayValues(3, 5), frames.World.Read<DisplayValues>(root));

        frames.World.Replace(trigger, new Value(11));
        frames.Frame();
        Assert.Equal(new DisplayValues(7, 11), frames.World.Read<DisplayValues>(root));

        if (sparse)
            frames.World.ReplaceSparse(sample, new SparseValue(13));
        else
            frames.World.Replace(sample, new Value(13));
        frames.Frame();
        Assert.Equal(new DisplayValues(7, 11), frames.World.Read<DisplayValues>(root));
    }

    [Fact]
    public void NestedUntrackBlocksKeepTheOuterBlockInItsValueOnlyState()
    {
        using var frames = new HeadlessFrameDriver();
        Entity sample = frames.World.CreateEntity(new Value(3));
        Entity trigger = frames.World.CreateEntity(new Value(5));
        Entity root = Views.Spawn(frames.World, Entity.Null, new NestedSampleDisplay(sample, trigger));

        frames.Frame();
        Assert.Equal(new DisplayValues(9, 5), frames.World.Read<DisplayValues>(root));

        frames.World.Replace(sample, new Value(7));
        frames.Frame();
        Assert.Equal(new DisplayValues(9, 5), frames.World.Read<DisplayValues>(root));

        frames.World.Replace(trigger, new Value(11));
        frames.Frame();
        Assert.Equal(new DisplayValues(21, 11), frames.World.Read<DisplayValues>(root));
    }

    [Fact]
    public void UntrackDisposalAfterAnExceptionRestoresOutsideReads()
    {
        using var frames = new HeadlessFrameDriver();
        Entity sample = frames.World.CreateEntity(new Value(3));
        Entity trigger = frames.World.CreateEntity(new Value(5));
        Entity root = Views.Spawn(frames.World, Entity.Null, new ThrowingSampleDisplay(sample, trigger));

        frames.Frame();
        Assert.Equal(new DisplayValues(3, 5), frames.World.Read<DisplayValues>(root));

        frames.World.Replace(sample, new Value(7));
        frames.Frame();
        Assert.Equal(new DisplayValues(3, 5), frames.World.Read<DisplayValues>(root));

        frames.World.Replace(trigger, new Value(11));
        frames.Frame();
        Assert.Equal(new DisplayValues(7, 11), frames.World.Read<DisplayValues>(root));
    }

#if DEBUG
    [Fact]
    public void UntrackOutsideBuildReportsTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => Scope.Untrack());
    }
#endif

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildBuildsUseTheirModeAndRestoreTheSurroundingReadState(bool once)
    {
        using var frames = new HeadlessFrameDriver();
        Entity sample = frames.World.CreateEntity(new Value(3));
        Entity childSource = frames.World.CreateEntity(new Value(5));
        Entity trigger = frames.World.CreateEntity(new Value(7));
        Entity root = once
            ? Views.Spawn(frames.World, Entity.Null, new OnceSampleGroup(sample, childSource, trigger))
            : Views.Spawn(frames.World, Entity.Null, new SampleGroup(sample, childSource, trigger));

        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(new DisplayValues(6, 7), frames.World.Read<DisplayValues>(root));
        Assert.Equal(new DisplayValues(5, 0), frames.World.Read<DisplayValues>(child));

        frames.World.Replace(sample, new Value(11));
        frames.World.Replace(childSource, new Value(13));
        frames.Frame();
        Assert.Equal(new DisplayValues(6, 7), frames.World.Read<DisplayValues>(root));
        Assert.Equal(new DisplayValues(13, 0), frames.World.Read<DisplayValues>(child));

        frames.World.Replace(trigger, new Value(17));
        frames.Frame();
        Assert.Equal(once ? new DisplayValues(6, 7) : new DisplayValues(22, 17),
            frames.World.Read<DisplayValues>(root));
        Assert.Equal(new DisplayValues(13, 0), frames.World.Read<DisplayValues>(child));
    }

    private readonly record struct SampleGroup(Entity Sample, Entity ChildSource, Entity Trigger) : IView, IComponent
    {
        public BuildTask Build()
        {
            BuildSampleGroup(Sample, ChildSource, Trigger);
            return default;
        }
    }

    private readonly record struct OnceSampleGroup(Entity Sample, Entity ChildSource, Entity Trigger) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            BuildSampleGroup(Sample, ChildSource, Trigger);
            return default;
        }
    }

    private static void BuildSampleGroup(Entity sampleSource, Entity childSource, Entity triggerSource)
    {
        int sample;
        using (Scope.Untrack())
        {
            sample = Scope.Read<Value>(sampleSource).Number;
            Scope.View(new ChildDisplay(childSource));
            sample += Scope.Read<Value>(sampleSource).Number;
        }
        int trigger = Scope.Read<Value>(triggerSource).Number;
        WriteDisplay(new DisplayValues(sample, trigger));
    }

    private readonly record struct ChildDisplay(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(new DisplayValues(Scope.Read<Value>(Source).Number, 0));
            return default;
        }
    }

    private readonly record struct ThrowingSampleDisplay(Entity Sample, Entity Trigger) : IView, IComponent
    {
        public BuildTask Build()
        {
            int sample = 0;
            try
            {
                using (Scope.Untrack())
                {
                    sample = Scope.Read<Value>(Sample).Number;
                    throw new SampleError();
                }
            }
            catch (SampleError)
            {
            }
            int trigger = Scope.Read<Value>(Trigger).Number;
            WriteDisplay(new DisplayValues(sample, trigger));
            return default;
        }
    }

    private sealed class SampleError : Exception;

    private readonly record struct NestedSampleDisplay(Entity Sample, Entity Trigger) : IView, IComponent
    {
        public BuildTask Build()
        {
            int sample;
            using (Scope.Untrack())
            {
                sample = Scope.Read<Value>(Sample).Number;
                using (Scope.Untrack())
                    sample += Scope.Read<Value>(Sample).Number;
                sample += Scope.Read<Value>(Sample).Number;
            }
            int trigger = Scope.Read<Value>(Trigger).Number;
            WriteDisplay(new DisplayValues(sample, trigger));
            return default;
        }
    }

    private readonly record struct SampleDisplay(Entity Sample, Entity Trigger, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            int sample;
            using (Scope.Untrack())
                sample = ReadValue(Sample, Sparse);
            int trigger = Scope.Read<Value>(Trigger).Number;
            WriteDisplay(new DisplayValues(sample, trigger));
            return default;
        }
    }

    private static int ReadValue(Entity source, bool sparse) => sparse
        ? Scope.Read<SparseValue>(source).Number
        : Scope.Read<Value>(source).Number;

    private static void WriteDisplay(DisplayValues values)
    {
        if (Scope.World.Has<DisplayValues>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, values);
        else
            Scope.World.Add(Scope.Entity, values);
    }

    private readonly record struct Value(int Number) : IComponent;
    private readonly record struct SparseValue(int Number) : ISparseComponent;
    private readonly record struct DisplayValues(int Sample, int Trigger) : IComponent;
}
