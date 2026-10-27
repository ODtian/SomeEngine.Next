using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Layout.Tests;

public sealed partial class LayoutCacheTests
{
    [Fact]
    public void RepeatedChildMeasurementsKeepConsecutiveLocalChangesEqualToFullLayout() =>
        SomeEngine.Views.AotSmoke.LayoutChecks.VerifyRepeatedMeasurements();

    [Fact]
    public void MovingAParentUpdatesEveryCachedDescendantInCanvasCoordinates()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new PositionedTree());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity parent = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, parent));

        frames.World.Replace(parent, new AbsolutePosition { Value = new Vector2(70, 90) });
        frames.Frame();
        frames.Frame();

        Assert.Equal(new Rect(70, 90, 200, 100), frames.World.Read<LayoutRect>(parent).Value);
        Assert.Equal(new Rect(70, 90, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Fact]
    public void UserLayoutReadsTrackExternalValuesPresenceAndSparseParameters()
    {
        using var frames = new LayoutFrames();
        Entity source = frames.World.CreateEntity(new ContentSize(new Size(20, 10)));
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new SourceTree(source));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Assert.Equal(new Rect(0, 0, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.Replace(source, new ContentSize(new Size(40, 30)));
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 40, 30), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.AddSparse(source, new ExtraWidth(10));
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 50, 30), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.ReplaceSparse(source, new ExtraWidth(20));
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 60, 30), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.RemoveSparse<ExtraWidth>(source);
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 40, 30), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.ReplaceSparse(leaf, new SourceSize(source, 5));
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 45, 30), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.DestroyEntity(source);
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 5, 0), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Fact]
    public void AStretchedFlexChildStillUpdatesItsIntrinsicBasisWhenContentChanges()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(200, 100) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new GrowingTree());
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, row).ToArray();
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, children[0]));
        Assert.Equal(new Rect(0, 0, 100, 100), frames.World.Read<LayoutRect>(children[0]).Value);

        frames.World.Replace(leaf, new IntrinsicSize(new Size(80, 20)));
        frames.Frame();

        Assert.Equal(new Rect(0, 0, 130, 100), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(130, 0, 70, 100), frames.World.Read<LayoutRect>(children[1]).Value);
    }

#if DEBUG
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EachMeasurementRequiresMeasuredChildrenThroughDevelopmentChecks(bool placeChild)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new MeasurementUsage(false, placeChild));
        frames.Frame();

        frames.World.Replace(root, new MeasurementUsage(true, placeChild));

        Assert.Throws<InvalidOperationException>(frames.Frame);
    }
#endif

    [Fact]
    public void PromotingAPositionedLayoutToCanvasUsesTheNewCanvasOrigin()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new PositionedTree());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity nestedCanvas = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, nestedCanvas));
        Assert.Equal(new Rect(30, 40, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.Add(nestedCanvas, new Canvas { Size = new Size(200, 100) });
        frames.Frame();

        Assert.Equal(new Rect(0, 0, 200, 100), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.Remove<Canvas>(nestedCanvas);
        frames.Frame();

        Assert.Equal(new Rect(30, 40, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(97)]
    [InlineData(2026)]
    public void IncrementalRectanglesMatchFullMeasurementAfterRandomTreeChanges(int seed)
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new RandomTree(seed));
        frames.Frame();
        Entity[] nodes = Descendants(frames.World, root)
            .Where(entity => frames.World.Has<LayoutRect>(entity)).Prepend(canvas).ToArray();
        var random = new Random(seed);
        for (int round = 0; round < 24; round++)
        {
            for (int frame = 0; frame < 3; frame++)
            {
                for (int edit = 0; edit < 4; edit++)
                {
                    Entity node = nodes[random.Next(1, nodes.Length)];
                    ChangeInputs(frames.World, node, random);
                }
                if (round % 4 == 0)
                    frames.World.Replace(canvas, new Canvas
                    {
                        Size = new Size(random.Next(400, 1000), random.Next(300, 700)),
                        Scale = random.Next(1, 3),
                    });
                if (frame == 0)
                    ChangeChildren(frames.World, root, random);
                frames.Frame();
                nodes = Descendants(frames.World, root)
                    .Where(entity => frames.World.Has<LayoutRect>(entity)).Prepend(canvas).ToArray();
            }
            Rect[] incremental = nodes.Select(entity => frames.World.Read<LayoutRect>(entity).Value).ToArray();

            foreach (Entity node in nodes)
                if (frames.World.Has<LayoutCache>(node))
                    frames.World.Remove<LayoutCache>(node);
            frames.Frame();

            Assert.Equal(incremental, nodes.Select(entity => frames.World.Read<LayoutRect>(entity).Value).ToArray());
        }
    }

    [Fact]
    public void ChangesInsideATightChildPreserveSurroundingRectangleVersions()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new TightTree());
        frames.Frame();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Entity boundary = children[0];
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, boundary));
        Entity[] surroundings = [canvas, root, boundary, children[1]];
        uint[] versions = surroundings.Select(frames.World.WriteVersion<LayoutRect>).ToArray();

        frames.World.Replace(leaf, new IntrinsicSize(new Size(40, 30)));
        frames.Frame();

        Assert.Equal(new Rect(0, 0, 40, 30), frames.World.Read<LayoutRect>(leaf).Value);
        Assert.Equal(versions, surroundings.Select(frames.World.WriteVersion<LayoutRect>).ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LayoutValueAndPresentReadsKeepBothOrdersCurrent(bool sparse, bool readFirst)
    {
        using var frames = new LayoutFrames();
        Entity source = frames.World.CreateEntity();
        SetReadDependency(frames.World, source, sparse, new Size(20, 10), replace: false);
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas,
            new ReadDependencyTree(new ReadDependencyProbe(source, sparse, readFirst)));
        frames.Frame();
        Entity leaf = ReadDependencyLeaf(frames.World, root);
        uint version = frames.World.Read<LayoutCache>(leaf).InputVersion;

        frames.Frame();
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
        Assert.Equal(new Rect(0, 0, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);

        SetReadDependency(frames.World, source, sparse, new Size(40, 30), replace: true);
        frames.Frame();
        Assert.NotEqual(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
        Assert.Equal(new Rect(0, 0, 40, 30), frames.World.Read<LayoutRect>(leaf).Value);
        version = frames.World.Read<LayoutCache>(leaf).InputVersion;
        frames.Frame();
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutValueReadsStillDetectRemovalReadditionAndDestruction(bool sparse)
    {
        using var frames = new LayoutFrames();
        Entity source = frames.World.CreateEntity();
        SetReadDependency(frames.World, source, sparse, new Size(20, 10), replace: false);
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas,
            new ReadDependencyTree(new ReadDependencyProbe(source, sparse)));
        frames.Frame();
        Entity leaf = ReadDependencyLeaf(frames.World, root);
        Assert.Equal(new Rect(0, 0, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);

        RemoveReadDependency(frames.World, source, sparse);
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 7, 9), frames.World.Read<LayoutRect>(leaf).Value);
        uint absentVersion = frames.World.Read<LayoutCache>(leaf).InputVersion;
        frames.Frame();
        Assert.Equal(absentVersion, frames.World.Read<LayoutCache>(leaf).InputVersion);

        SetReadDependency(frames.World, source, sparse, new Size(50, 25), replace: false);
        frames.Frame();
        Assert.NotEqual(absentVersion, frames.World.Read<LayoutCache>(leaf).InputVersion);
        Assert.Equal(new Rect(0, 0, 50, 25), frames.World.Read<LayoutRect>(leaf).Value);

        frames.World.DestroyEntity(source);
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 7, 9), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutPresenceOnlyReadsIgnoreValueWritesButObserveAbsentToPresent(bool sparse)
    {
        using var frames = new LayoutFrames();
        Entity source = frames.World.CreateEntity();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas,
            new ReadDependencyTree(new ReadDependencyProbe(source, sparse, PresenceOnly: true)));
        frames.Frame();
        Entity leaf = ReadDependencyLeaf(frames.World, root);
        Assert.Equal(new Rect(0, 0, 7, 9), frames.World.Read<LayoutRect>(leaf).Value);

        SetReadDependency(frames.World, source, sparse, new Size(100, 90), replace: false);
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);
        uint version = frames.World.Read<LayoutCache>(leaf).InputVersion;

        SetReadDependency(frames.World, source, sparse, new Size(200, 180), replace: true);
        frames.Frame();
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
        Assert.Equal(new Rect(0, 0, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);

        RemoveReadDependency(frames.World, source, sparse);
        frames.Frame();
        Assert.Equal(new Rect(0, 0, 7, 9), frames.World.Read<LayoutRect>(leaf).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutPresentReadReplacesAnEarlierAbsentObservationAfterAValueRead(bool sparse)
    {
        using var frames = new SerialReadHistoryFrames();
        Entity source = frames.World.CreateEntity();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new ReadPresenceHistoryTree(frames.World, source, sparse));
        frames.Frame();
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(new Rect(0, 0, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);
        uint version = frames.World.Read<LayoutCache>(leaf).InputVersion;

        frames.Frame();
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
        frames.Frame();
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutAbsentObservationBeforeAValueReadRemainsADependency(bool sparse)
    {
        using var frames = new SerialReadHistoryFrames();
        Entity source = frames.World.CreateEntity();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas,
            new ReadPresenceHistoryTree(frames.World, source, sparse, ConfirmPresent: false));
        frames.Frame();
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        uint version = frames.World.Read<LayoutCache>(leaf).InputVersion;

        frames.Frame();
        Assert.NotEqual(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
        Assert.Equal(new Rect(0, 0, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);
        version = frames.World.Read<LayoutCache>(leaf).InputVersion;
        frames.Frame();
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
    }

    [Fact]
    public void CachedLayoutStillRepairsExternallyReplacedAndRemovedRectangles()
    {
        using var frames = new LayoutFrames();
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new PositionedTree());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity parent = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, parent));
        Entity[] nodes = [canvas, parent, leaf];
        Rect[] expected = nodes.Select(node => frames.World.Read<LayoutRect>(node).Value).ToArray();
        uint version = frames.World.Read<LayoutCache>(leaf).InputVersion;

        foreach (Entity node in nodes)
            frames.World.Replace(node, new LayoutRect { Value = new Rect(900, 800, 1, 2) });
        frames.Frame();
        Assert.Equal(expected, nodes.Select(node => frames.World.Read<LayoutRect>(node).Value).ToArray());
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);

        foreach (Entity node in nodes) frames.World.Remove<LayoutRect>(node);
        frames.Frame();
        Assert.Equal(expected, nodes.Select(node => frames.World.Read<LayoutRect>(node).Value).ToArray());
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
    }

    private static Entity ReadDependencyLeaf(World world, Entity root) =>
        Assert.Single(Hierarchy<ElementTree>.GetChildren(world,
            Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root))));

    private static void SetReadDependency(World world, Entity source, bool sparse, Size value, bool replace)
    {
        if (sparse)
        {
            if (replace) world.ReplaceSparse(source, new SparseReadDependencyValue(value));
            else world.AddSparse(source, new SparseReadDependencyValue(value));
        }
        else
        {
            if (replace) world.Replace(source, new ReadDependencyValue(value));
            else world.Add(source, new ReadDependencyValue(value));
        }
    }

    private static void RemoveReadDependency(World world, Entity source, bool sparse)
    {
        if (sparse) world.RemoveSparse<SparseReadDependencyValue>(source);
        else world.Remove<ReadDependencyValue>(source);
    }

    private readonly record struct ReadDependencyValue(Size Value) : IComponent;
    private readonly record struct SparseReadDependencyValue(Size Value) : ISparseComponent;

    private readonly record struct ReadDependencyTree(ReadDependencyProbe Probe) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content()) Scope.View(Probe, 2);
            return default;
        }
    }

    private readonly record struct ReadDependencyProbe(Entity Source, bool Sparse,
        bool ReadFirst = false, bool PresenceOnly = false) : ILayout, IComponent
    {
        public BuildTask Build() => default;

        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            if (ReadFirst)
            {
                Size size = ReadValue(ref pass);
                return HasValue(ref pass) ? size : new Size(7, 9);
            }
            if (!HasValue(ref pass)) return new Size(7, 9);
            return PresenceOnly ? new Size(20, 10) : ReadValue(ref pass);
        }

        private bool HasValue(ref LayoutPass pass) => Sparse
            ? pass.Has<SparseReadDependencyValue>(Source) : pass.Has<ReadDependencyValue>(Source);

        private Size ReadValue(ref LayoutPass pass) => Sparse
            ? pass.Read<SparseReadDependencyValue>(Source).Value : pass.Read<ReadDependencyValue>(Source).Value;
    }

    private readonly record struct ReadPresenceHistoryTree(World World, Entity Source, bool Sparse, bool ConfirmPresent = true) : ILayout, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new ReadPresenceHistory(World, Source, Sparse, ConfirmPresent), 1);
            return default;
        }

        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            Entity child = pass.Children[0];
            var childConstraints = new Constraints(default, constraints.Max);
            Size size = pass.Measure(child, in childConstraints);
            pass.Place(child, Vector2.Zero);
            return constraints.Constrain(size);
        }
    }

    private readonly record struct ReadPresenceHistory(World World, Entity Source, bool Sparse, bool ConfirmPresent) : ILayout, IComponent
    {
        public BuildTask Build() => default;

        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            bool exists = Sparse ? pass.Has<SparseReadDependencyValue>(Source) : pass.Has<ReadDependencyValue>(Source);
            if (!exists) SetReadDependency(World, Source, Sparse, new Size(20, 10), replace: false);
            Size size = Sparse ? pass.Read<SparseReadDependencyValue>(Source).Value : pass.Read<ReadDependencyValue>(Source).Value;
            if (ConfirmPresent)
                _ = Sparse ? pass.Has<SparseReadDependencyValue>(Source) : pass.Has<ReadDependencyValue>(Source);
            return size;
        }
    }

    private sealed class SerialReadHistoryFrames : IDisposable
    {
        internal World World { get; } = new();
        private readonly SomeEngine.ECS.Systems.SystemGroup<SomeEngine.ECS.Systems.ImmediateSystemContext> _systems;

        internal SerialReadHistoryFrames()
        {
            _systems = new(new SomeEngine.ECS.Systems.ImmediateSystemDriver(World));
            _systems.Add(new ViewRebuildSystem());
            _systems.Add(new LayoutSystem { Parallel = false });
        }

        internal void Frame() => _systems.Update();

        public void Dispose()
        {
            _systems.Dispose();
            World.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundLayoutFixedReadsKeepCallbacksOutsideTheReadOwner(bool parallel)
    {
        var updates = new FixedReadBoundaryUpdate();
        using var frames = new BoundFixedReadFrames(parallel, updates);
        Entity source = frames.World.CreateEntity(new ContentSize(new Size(20, 10)));
        frames.World.AddSparse(source, new ExtraWidth(5));
        updates.Source = source;
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new SourceTree(source));
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Assert.Equal(new Rect(0, 0, 25, 10), frames.World.Read<LayoutRect>(leaf).Value);
        Assert.True(frames.World.Has<FixedReadBoundaryMarker>(leaf));

        frames.World.Remove<FixedReadBoundaryMarker>(leaf);
        updates.Armed = true;
        frames.Frame();

        Assert.Equal(2, updates.WriteCallbacks);
        Assert.Equal(updates.TickAfterHasChangesWrites, updates.TickBeforeReadContentWrites);
        Assert.True(frames.World.Has<FixedReadBoundaryMarker>(leaf));
        Assert.True(frames.World.Has<LayoutCache>(leaf));
        Assert.Equal(new Size(60, 35), frames.World.Read<ContentSize>(source).Value);
        Assert.Equal(15f, frames.World.ReadSparse<ExtraWidth>(source).Value);
        Assert.Equal(new Rect(0, 0, 75, 35), frames.World.Read<LayoutRect>(leaf).Value);
        uint version = frames.World.Read<LayoutCache>(leaf).InputVersion;
        frames.Frame();
        Assert.Equal(version, frames.World.Read<LayoutCache>(leaf).InputVersion);
        Assert.Equal(2, updates.WriteCallbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundLayoutFixedReadsHandleMissingCacheAndRepairRectangles(bool parallel)
    {
        using var frames = new BoundFixedReadFrames(parallel);
        Entity canvas = frames.World.CreateEntity(new Canvas { Size = new Size(800, 600) });
        Entity root = Views.Views.Spawn(frames.World, canvas, new PositionedTree());
        frames.Frame();
        Entity absolute = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity parent = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, absolute));
        Entity leaf = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, parent));
        uint cacheVersion = frames.World.Read<LayoutCache>(leaf).InputVersion;
        uint rectangleVersion = frames.World.WriteVersion<LayoutRect>(leaf);
        frames.Frame();
        Assert.Equal(cacheVersion, frames.World.Read<LayoutCache>(leaf).InputVersion);
        Assert.Equal(rectangleVersion, frames.World.WriteVersion<LayoutRect>(leaf));

        frames.World.Remove<LayoutCache>(leaf);
        frames.Frame();
        Assert.True(frames.World.Has<LayoutCache>(leaf));
        Assert.Equal(new Rect(30, 40, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);
        cacheVersion = frames.World.Read<LayoutCache>(leaf).InputVersion;

        frames.World.Remove<LayoutRect>(parent);
        frames.World.Replace(leaf, new LayoutRect { Value = new Rect(900, 800, 1, 2) });
        frames.Frame();
        Assert.Equal(new Rect(30, 40, 200, 100), frames.World.Read<LayoutRect>(parent).Value);
        Assert.Equal(new Rect(30, 40, 20, 10), frames.World.Read<LayoutRect>(leaf).Value);
        Assert.Equal(cacheVersion, frames.World.Read<LayoutCache>(leaf).InputVersion);
    }

    private readonly record struct FixedReadBoundaryMarker : IComponent;

    private sealed class FixedReadBoundaryUpdate : ILayoutDataUpdate
    {
        internal Entity Source;
        internal bool Armed;
        internal int WriteCallbacks;
        internal uint TickAfterHasChangesWrites;
        internal uint TickBeforeReadContentWrites;
        private bool _finishInputs;
        public bool SupportsParallel => true;

        public bool HasChanges(World world, Entity node)
        {
            if (!Armed || !world.HasSparse<SourceSize>(node)) return false;
            Armed = false;
            world.Replace(Source, new ContentSize(new Size(40, 30)));
            world.ReplaceSparse(Source, new ExtraWidth(10));
            if (world.Has<LayoutCache>(node)) world.Remove<LayoutCache>(node);
            TickAfterHasChangesWrites = world.CurrentTick;
            _finishInputs = true;
            WriteCallbacks++;
            return true;
        }

        public ILayoutContent? ReadContent(World world, Entity node)
        {
            if (!world.HasSparse<SourceSize>(node)) return null;
            if (_finishInputs) TickBeforeReadContentWrites = world.CurrentTick;
            if (!world.Has<FixedReadBoundaryMarker>(node)) world.Add(node, new FixedReadBoundaryMarker());
            if (_finishInputs)
            {
                world.Replace(Source, new ContentSize(new Size(60, 35)));
                world.ReplaceSparse(Source, new ExtraWidth(15));
                _finishInputs = false;
                WriteCallbacks++;
            }
            return null;
        }

        public void Update(World world, Entity node, in Constraints constraints) { }
    }

    private sealed class BoundFixedReadFrames : IDisposable
    {
        internal World World { get; } = SomeEngine.ECS.Systems.WorldJobs.CreateWorld();
        private readonly SomeEngine.ECS.Systems.SystemGroup<SomeEngine.ECS.Systems.ImmediateSystemContext> _systems;

        internal BoundFixedReadFrames(bool parallel, params ILayoutDataUpdate[] dataUpdates)
        {
            _systems = new(new SomeEngine.ECS.Systems.ImmediateSystemDriver(World));
            _systems.Add(new ViewRebuildSystem());
            _systems.Add(new LayoutSystem(dataUpdates) { Parallel = parallel });
        }

        internal void Frame() => _systems.Update();

        public void Dispose()
        {
            _systems.Dispose();
            World.Dispose();
        }
    }

    private readonly record struct TightTree : ILayout, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
                Scope.View(new IntrinsicSize(new Size(20, 10)), 2);
            Scope.View(new Absolute(), 3);
            return default;
        }

        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            var fixedSize = Constraints.Fixed(new Size(100, 80));
            for (int index = 0; index < pass.Children.Count; index++)
            {
                Entity child = pass.Children[index];
                pass.Measure(child, in fixedSize);
                pass.Place(child, new Vector2(index * 150, 0));
            }
            return constraints.Max;
        }
    }

    private readonly record struct PositionedTree : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
            using (Scope.View(new Absolute(), 2).Position(30, 40).Size(200, 100).Content())
                Scope.View(new IntrinsicSize(new Size(20, 10)), 3);
            return default;
        }
    }

    private readonly record struct IntrinsicSize(Size Value) : ILayout, IComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) => Value;
    }

    private readonly record struct ContentSize(Size Value) : IComponent;
    private readonly record struct ExtraWidth(float Value) : ISparseComponent;

    private readonly record struct SourceTree(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Absolute(), 1).Content())
                Scope.View(new SourceSize(Source), 2);
            return default;
        }
    }

    private readonly record struct SourceSize(Entity Source, float Extra = 0) : ILayout, ISparseComponent
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            Size size = pass.Has<ContentSize>(Source) ? pass.Read<ContentSize>(Source).Value : default;
            float extra = pass.Has<ExtraWidth>(Source) ? pass.Read<ExtraWidth>(Source).Value : 0;
            return size with { Width = size.Width + extra + Extra };
        }
    }

    private readonly record struct GrowingTree : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Scope.View(new Flex(Axis.Horizontal), 1).Content())
            {
                using (Scope.View(new Absolute(), 2).Grow().Content())
                    Scope.View(new IntrinsicSize(new Size(20, 20)), 3);
                using (Scope.View(new Absolute(), 4).Grow().Content())
                    Scope.View(new IntrinsicSize(new Size(20, 20)), 5);
            }
            return default;
        }
    }

    private readonly record struct MeasurementUsage(bool SkipMeasure, bool PlaceChild) : ILayout, IComponent
    {
        public BuildTask Build()
        {
            Scope.View(new IntrinsicSize(new Size(20, 10)), 1);
            return default;
        }

        public Size Measure(ref LayoutPass pass, in Constraints constraints)
        {
            Entity child = pass.Children[0];
            if (!SkipMeasure)
                pass.Measure(child, in constraints);
            if (PlaceChild)
                pass.Place(child, default);
            return constraints.Max;
        }
    }

    private static IEnumerable<Entity> Descendants(World world, Entity node)
    {
        yield return node;
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(world, node))
            foreach (Entity descendant in Descendants(world, child))
                yield return descendant;
    }

    private static void ChangeInputs(World world, Entity node, Random random)
    {
        switch (random.Next(5))
        {
            case 0 when world.Has<IntrinsicSize>(node):
                world.Replace(node, new IntrinsicSize(new Size(random.Next(10, 120), random.Next(10, 90))));
                break;
            case 1 when world.Has<Flex>(node):
                world.Replace(node, new Flex((Axis)random.Next(2), random.Next(8),
                    (Align)random.Next((int)Align.Start, (int)Align.Stretch),
                    (Align)random.Next((int)Align.Start, (int)Align.Stretch + 1), random.Next(2) == 0));
                break;
            case 2:
                world.Replace(node, new FlexItem
                {
                    Grow = random.Next(3), Shrink = random.Next(3),
                    Basis = random.Next(2) == 0 ? float.NaN : random.Next(20, 100),
                });
                break;
            case 3:
                world.Replace(node, new AbsolutePosition { Value = new Vector2(random.Next(20), random.Next(20)) });
                break;
            default:
                Box box = world.Read<Box>(node);
                float padding = random.Next(8);
                box.Padding = new Thickness(padding, padding, padding, padding);
                box.Fixed = random.Next(3) == 0 ? new Size(random.Next(50, 200), random.Next(40, 150)) : Size.Auto;
                box.Min = new Size(random.Next(20), random.Next(15));
                world.Replace(node, in box);
                break;
        }
    }

    private static void ChangeChildren(World world, Entity root, Random random)
    {
        Entity[] nodes = Descendants(world, root).ToArray();
        Entity[] parents = nodes.Where(entity => world.Has<Absolute>(entity) || world.Has<Flex>(entity)).ToArray();
        Entity[] leaves = nodes.Where(world.Has<IntrinsicSize>).ToArray();
        Entity parent = parents[random.Next(parents.Length)];
        switch (random.Next(4))
        {
            case 0:
                Entity added = Views.Views.Spawn(world, parent, new IntrinsicSize(new Size(random.Next(20, 100), 30)));
                world.Add(added, new Box { Max = new Size(300, 250) });
                world.Add(added, new AbsolutePosition { Value = new Vector2(5, 7) });
                world.Add(added, new FlexItem { Grow = 1 });
                break;
            case 1 when leaves.Length > 1:
                world.DestroyEntity(leaves[random.Next(leaves.Length)]);
                break;
            case 2 when leaves.Length > 0:
                Hierarchy<ElementTree>.SetParent(world, leaves[random.Next(leaves.Length)], parent);
                break;
            default:
                var children = Hierarchy<ElementTree>.GetChildren(world, parent);
                if (children.Count > 1)
                    Hierarchy<ElementTree>.Reorder(world, children[0], children.Count - 1);
                break;
        }
    }

    private readonly record struct RandomTree(int Seed) : IView, IComponent
    {
        public BuildTask Build()
        {
            BuildBranch(new Random(Seed), 3, 1);
            return default;
        }

        private static void BuildBranch(Random random, int depth, ulong key)
        {
            Entity node;
            if (depth == 0)
                node = Scope.View(new IntrinsicSize(new Size(random.Next(20, 100), random.Next(15, 80))), key);
            else
            {
                node = random.Next(2) == 0
                    ? Scope.View(new Absolute(), key)
                    : Scope.View(new Flex((Axis)random.Next(2), random.Next(5)), key);
                using (node.Content())
                    for (ulong child = 1; child <= 3; child++)
                        BuildBranch(random, depth - 1, child);
            }
            node.MaxSize(300, 250).Padding(random.Next(5)).Position(random.Next(10), random.Next(10));
            node.Grow(random.Next(3));
            if (random.Next(4) == 0)
                node.Size(random.Next(50, 150), random.Next(40, 100));
        }
    }
}
