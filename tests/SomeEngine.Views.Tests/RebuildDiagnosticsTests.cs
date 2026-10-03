using SomeEngine.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Trace;

namespace SomeEngine.Views.Tests;

[Collection("Rebuild diagnostics")]
public sealed class RebuildDiagnosticsTests : IDisposable
{
    public RebuildDiagnosticsTests() => RebuildDiagnostics.Enabled = true;

    public void Dispose() => RebuildDiagnostics.Enabled = false;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentWritesReportTheViewAndChangedSource(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity();
        if (sparse)
            frames.World.AddSparse(source, new SparseValue(3));
        else
            frames.World.Add(source, new Value(3));
        Entity view = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(source, sparse));
        frames.Frame();

        if (sparse)
            frames.World.ReplaceSparse(source, new SparseValue(7));
        else
            frames.World.Replace(source, new Value(7));
        frames.Frame();

        Assert.Equal(7, frames.World.Read<DisplayValue>(view).Number);
        ushort component = sparse ? ComponentId<SparseValue>.Value : ComponentId<Value>.Value;
        Assert.Contains(new RebuildRecord(view, source, component), RebuildDiagnostics.LastFrame(frames.World).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParameterWritesReportTheViewAsTheirSource(bool sparse)
    {
        using var frames = new HeadlessFrameDriver();
        Entity view = sparse
            ? Views.Spawn(frames.World, Entity.Null, new SparseParameterDisplay(3))
            : Views.Spawn(frames.World, Entity.Null, new ParameterDisplay(3));
        frames.Frame();

        if (sparse)
            frames.World.ReplaceSparse(view, new SparseParameterDisplay(7));
        else
            frames.World.Replace(view, new ParameterDisplay(7));
        frames.Frame();

        Assert.Equal(7, frames.World.Read<DisplayValue>(view).Number);
        ushort component = sparse ? ComponentId<SparseParameterDisplay>.Value : ComponentId<ParameterDisplay>.Value;
        Assert.Contains(new RebuildRecord(view, view, component), RebuildDiagnostics.LastFrame(frames.World).ToArray());
    }

    [Fact]
    public void DisablingDiagnosticsClearsRecordsWhileTheDisplayKeepsUpdating()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Value(3));
        Entity view = Views.Spawn(frames.World, Entity.Null, new ValueDisplay(source, Sparse: false));
        frames.Frame();
        frames.World.Replace(source, new Value(7));
        frames.Frame();
        Assert.Contains(new RebuildRecord(view, source, ComponentId<Value>.Value),
            RebuildDiagnostics.LastFrame(frames.World).ToArray());

        RebuildDiagnostics.Enabled = false;
        Assert.True(RebuildDiagnostics.LastFrame(frames.World).IsEmpty);
        frames.World.Replace(source, new Value(9));
        frames.Frame();
        Assert.Equal(9, frames.World.Read<DisplayValue>(view).Number);
        Assert.True(RebuildDiagnostics.LastFrame(frames.World).IsEmpty);

        RebuildDiagnostics.Enabled = true;
        Assert.True(RebuildDiagnostics.LastFrame(frames.World).IsEmpty);
    }

    [Fact]
    public void AdvancingAWorldReplacesOnlyItsOwnFrameRecords()
    {
        using var firstFrames = new HeadlessFrameDriver();
        using var secondFrames = new HeadlessFrameDriver();
        Entity firstSource = firstFrames.World.CreateEntity(new Value(3));
        Entity firstView = Views.Spawn(firstFrames.World, Entity.Null, new ValueDisplay(firstSource, Sparse: false));
        secondFrames.World.CreateEntity();
        Entity secondSource = secondFrames.World.CreateEntity(new Value(5));
        Entity secondView = Views.Spawn(secondFrames.World, Entity.Null, new ValueDisplay(secondSource, Sparse: false));
        Assert.True(RebuildDiagnostics.LastFrame(firstFrames.World).IsEmpty);
        firstFrames.Frame();
        secondFrames.Frame();

        firstFrames.World.Replace(firstSource, new Value(7));
        firstFrames.Frame();
        secondFrames.World.Replace(secondSource, new Value(11));
        secondFrames.Frame();
        var firstReason = new RebuildRecord(firstView, firstSource, ComponentId<Value>.Value);
        var secondReason = new RebuildRecord(secondView, secondSource, ComponentId<Value>.Value);
        Assert.Contains(firstReason, RebuildDiagnostics.LastFrame(firstFrames.World).ToArray());
        Assert.Contains(secondReason, RebuildDiagnostics.LastFrame(secondFrames.World).ToArray());

        firstFrames.Frame();
        Assert.True(RebuildDiagnostics.LastFrame(firstFrames.World).IsEmpty);
        Assert.Contains(secondReason, RebuildDiagnostics.LastFrame(secondFrames.World).ToArray());
    }

    [Fact]
    public void AllChangedSourcesAreReportedAlongsideParameterChanges()
    {
        using var frames = new HeadlessFrameDriver();
        Entity first = frames.World.CreateEntity(new Value(3));
        Entity second = frames.World.CreateEntity(new Value(5));
        Entity view = Views.Spawn(frames.World, Entity.Null, new ParameterValueDisplay(first, second, 0));
        frames.Frame();
        frames.World.Replace(first, new Value(7));
        frames.World.Replace(second, new Value(11));
        frames.World.Replace(view, new ParameterValueDisplay(first, second, 13));

        frames.Frame();

        Assert.Equal(31, frames.World.Read<DisplayValue>(view).Number);
        RebuildRecord[] reasons = RebuildDiagnostics.LastFrame(frames.World).ToArray();
        Assert.Contains(new RebuildRecord(view, view, ComponentId<ParameterValueDisplay>.Value), reasons);
        Assert.Contains(new RebuildRecord(view, first, ComponentId<Value>.Value), reasons);
        Assert.Contains(new RebuildRecord(view, second, ComponentId<Value>.Value), reasons);
    }

    [Fact]
    public void ParentAndSynchronousChildRebuildsReportTheirChangedSources()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Value(3));
        Entity other = frames.World.CreateEntity(new Value(5));
        Entity parent = Views.Spawn(frames.World, Entity.Null, new ValueGroup(source, other));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, parent));
        frames.World.Replace(source, new Value(7));

        frames.Frame();

        Assert.Equal(82, frames.World.Read<DisplayValue>(child).Number);
        Assert.Equal(82, frames.World.Read<DisplayValue>(parent).Number);
        RebuildRecord[] reasons = RebuildDiagnostics.LastFrame(frames.World).ToArray();
        Assert.Contains(new RebuildRecord(parent, source, ComponentId<Value>.Value), reasons);
        Assert.Contains(new RebuildRecord(child, source, ComponentId<Value>.Value), reasons);
        Assert.Contains(new RebuildRecord(child, child, ComponentId<ParameterValueDisplay>.Value), reasons);
    }

    [Fact]
    public void FirstBuildsIdentifyEachViewWithAnEmptySource()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Value(3));
        Entity other = frames.World.CreateEntity(new Value(5));
        Entity parent = Views.Spawn(frames.World, Entity.Null, new ValueGroup(source, other));

        frames.Frame();

        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, parent));
        RebuildRecord[] reasons = RebuildDiagnostics.LastFrame(frames.World).ToArray();
        Assert.Contains(new RebuildRecord(parent, Entity.Null, 0), reasons);
        Assert.Contains(new RebuildRecord(child, Entity.Null, 0), reasons);
    }

#if DEBUG
    [Fact]
    public void ReadingDiagnosticsRequiresAWorld()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RebuildDiagnostics.LastFrame(null!));
        Assert.Contains("require a World", error.Message);
    }
#endif

    [Fact]
    public void ViewTypesAppearAsNamedBuildSections()
    {
        RebuildDiagnostics.Enabled = false;
        string tracePath = Path.Combine(Path.GetTempPath(), $"someengine-views-{Guid.NewGuid():N}.setrace");
        try
        {
            using var frames = new HeadlessFrameDriver();
            Entity source = frames.World.CreateEntity(new Value(3));
            Views.Spawn(frames.World, Entity.Null, new ValueDisplay(source, Sparse: false));
            Views.Spawn(frames.World, Entity.Null, new ParameterDisplay(5));
            using (EngineTrace.Start(tracePath))
                frames.Frame();

            TraceData trace = TraceFile.Read(tracePath);
            string[] sections = trace.Events
                .Where(entry => entry.Event.Kind == TraceEventKind.Begin)
                .Select(entry => trace.Sites.Single(site => site.Id == entry.Event.Site).Name)
                .ToArray();
            Assert.Contains($"{typeof(ValueDisplay).FullName}.Build", sections);
            Assert.Contains($"{typeof(ParameterDisplay).FullName}.Build", sections);
        }
        finally
        {
            File.Delete(tracePath);
        }
    }

    private readonly record struct ParameterDisplay(int Number) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Number);
            return default;
        }
    }

    private readonly record struct ValueGroup(Entity Source, Entity Other) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Scope.Read<Value>(Source).Number;
            Entity child = Scope.View(new ParameterValueDisplay(Source, Other, value * 10));
            WriteDisplay(Scope.World.Read<DisplayValue>(child).Number);
            return default;
        }
    }

    private readonly record struct ParameterValueDisplay(Entity Source, Entity Other, int Offset) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Scope.Read<Value>(Source).Number;
            int other = Scope.Read<Value>(Other).Number;
            WriteDisplay(value + other + Offset);
            return default;
        }
    }

    private readonly record struct SparseParameterDisplay(int Number) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Number);
            return default;
        }
    }

    private readonly record struct ValueDisplay(Entity Source, bool Sparse) : IView, IComponent
    {
        public BuildTask Build()
        {
            int value = Sparse ? Scope.Read<SparseValue>(Source).Number : Scope.Read<Value>(Source).Number;
            WriteDisplay(value);
            return default;
        }
    }

    private static void WriteDisplay(int number)
    {
        if (Scope.World.Has<DisplayValue>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, new DisplayValue(number));
        else
            Scope.World.Add(Scope.Entity, new DisplayValue(number));
    }

    private readonly record struct Value(int Number) : IComponent;
    private readonly record struct SparseValue(int Number) : ISparseComponent;
    private readonly record struct DisplayValue(int Number) : IComponent;
}

[CollectionDefinition("Rebuild diagnostics", DisableParallelization = true)]
public sealed class RebuildDiagnosticsCollection;
