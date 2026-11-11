using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Tests;

public sealed class ComponentWriteVersionTests
{
    [Fact]
    public void ReplacingTableValueAdvancesItsWriteVersion()
    {
        using var world = new World();
        var entity = world.Create();
        world.Add(entity, new Position { X = 1 });
        ulong addedVersion = world.ComponentDataWriteVersion<Position>(entity);

        world.Set(entity, new Position { X = 2 });

        Assert.NotEqual(addedVersion, world.ComponentDataWriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReaddingTableComponentAdvancesItsWriteVersion()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        ulong version = world.ComponentDataWriteVersion<Position>(entity);

        world.Remove<Position>(entity);
        world.Add(entity, new Position { X = 2 });

        Assert.NotEqual(version, world.ComponentDataWriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReplacingSparseValueAdvancesItsWriteVersion()
    {
        using var world = new World();
        var entity = world.Create();
        world.SetSparse(entity, new Damage { Amount = 1 });
        ulong addedVersion = world.ComponentDataWriteVersion<Damage>(entity);

        world.SetSparse(entity, new Damage { Amount = 2 });

        Assert.NotEqual(addedVersion, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowingSparseValuesForWriteAdvancesTheirVersions(bool withState)
    {
        using var world = new World();
        var entity = world.Create();
        world.SetSparse(entity, new Damage { Amount = 1 });
        ulong version = world.ComponentDataWriteVersion<Damage>(entity);

        if (withState)
        {
            int amount = 3;
            {
                var sparseScope = world.OpenSparseWrite<Damage>();
                try
                {
                    ReadOnlySpan<Entity> entities = sparseScope.Entities;
                    Span<Damage> values = sparseScope.Values;
                    values[0].Amount = amount;
                }
                finally { sparseScope.Dispose(); }
            }
        }
        else
        {
            {
                var sparseScope = world.OpenSparseWrite<Damage>();
                try
                {
                    ReadOnlySpan<Entity> entities = sparseScope.Entities;
                    Span<Damage> values = sparseScope.Values;
                    values[0].Amount = 3;
                }
                finally { sparseScope.Dispose(); }
            }
        }

        Assert.NotEqual(version, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(3, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void CommandReplayAddsAndReplacesSparseValuesWithNewVersions()
    {
        using var world = new World();
        var entity = world.Create();
        using var commands = new CommandBuffer(world);
        commands.SetSparse(entity, new Damage { Amount = 1 });
        commands.Playback();
        ulong version = world.ComponentDataWriteVersion<Damage>(entity);

        commands.Clear();
        commands.SetSparse(entity, new Damage { Amount = 2 });
        commands.Playback();

        Assert.NotEqual(version, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void CopyingAnEntityAdvancesWrittenTableAndSparseVersions()
    {
        using var world = new World();
        var source = world.Create(new Position { X = 1 });
        world.SetSparse(source, new Damage { Amount = 2 });
        var target = world.Create(new Position { X = 3 });
        world.SetSparse(target, new Damage { Amount = 4 });
        world.Copy(source, target);
        ulong tableVersion = world.ComponentDataWriteVersion<Position>(target);
        ulong sparseVersion = world.ComponentDataWriteVersion<Damage>(target);

        world.Copy(source, target);

        Assert.NotEqual(tableVersion, world.ComponentDataWriteVersion<Position>(target));
        Assert.NotEqual(sparseVersion, world.ComponentDataWriteVersion<Damage>(target));
        Assert.Equal(1, world.Read<Position>(target).X);
        Assert.Equal(2, world.ReadSparse<Damage>(target).Amount);
    }

    [Fact]
    public void BundleReplacementAdvancesTableAndSparseWriteVersions()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        ulong tableVersion = world.ComponentDataWriteVersion<Position>(entity);

        world.ExecuteBundleReplace(entity, [ComponentId<Position>.Value], [], 0, new PositionReplacement());

        Assert.NotEqual(tableVersion, world.ComponentDataWriteVersion<Position>(entity));
        world.SetSparse(entity, new Damage { Amount = 2 });
        ulong sparseVersion = world.ComponentDataWriteVersion<Damage>(entity);
        world.ExecuteBundleReplace(entity, [], [ComponentId<Damage>.Value], 0, new SparseReplacement());

        Assert.NotEqual(sparseVersion, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(3, world.Read<Position>(entity).X);
        Assert.Equal(4, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void TableRowWriteBorrowAdvancesVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1 });
        ulong version = world.ComponentDataWriteVersion<Position>(entity);
        using (QueryScope scope = world.Query<Position>().Open())
            foreach (QueryChunk chunk in scope) chunk.Write<Position>(0).X = 2;
        Assert.NotEqual(version, world.ComponentDataWriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void TableColumnWriteBorrowAdvancesVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1 });
        ulong version = world.ComponentDataWriteVersion<Position>(entity);
        using (QueryScope scope = world.Query<Position>().Open())
            foreach (QueryChunk chunk in scope) chunk.Write<Position>()[0].X = 2;
        Assert.NotEqual(version, world.ComponentDataWriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReadsPreserveTableAndSparseWriteVersions()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        world.SetSparse(entity, new Damage { Amount = 2 });
        ulong tableVersion = world.ComponentDataWriteVersion<Position>(entity);
        ulong sparseVersion = world.ComponentDataWriteVersion<Damage>(entity);
        var query = world.Query(new QueryDescription().WithStored<Position>());
        ulong tick = world.Version;

        {
            using QueryScope cursor = query.OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                    Assert.Equal(1, rowChunk.Read<Position>()[row].X);
            foreach (var chunk in cursor)
                Assert.Equal(1, chunk.Read<Position>()[0].X);

        }
        {
            var sparseScope = world.OpenSparseRead<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
                ReadOnlySpan<Damage> values = sparseScope.Values;
                Assert.Equal(2, values[0].Amount);
            }
            finally { sparseScope.Dispose(); }
        }
        int amount = 0;
        {
            var sparseScope = world.OpenSparseRead<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
                ReadOnlySpan<Damage> values = sparseScope.Values;
                amount = values[0].Amount;
            }
            finally { sparseScope.Dispose(); }
        }
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
        Assert.Equal(2, amount);
        Assert.Equal(tableVersion, world.ComponentDataWriteVersion<Position>(entity));
        Assert.Equal(sparseVersion, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(tick, world.Version);
    }

    [Fact]
    public void CommandReplayAddsAndReplacesTableValuesWithNewVersions()
    {
        using var world = new World();
        var entity = world.Create();
        using var commands = new CommandBuffer(world);
        commands.Add(entity, new Position { X = 1 });
        commands.Playback();
        ulong version = world.ComponentDataWriteVersion<Position>(entity);
        commands.Clear();

        commands.Set(entity, new Position { X = 2 });
        commands.Playback();

        Assert.NotEqual(version, world.ComponentDataWriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void SparseCommandsResolveDeferredEntities()
    {
        using var world = new World();
        using var commands = new CommandBuffer(world);
        var deferred = commands.Create();
        commands.SetSparse(deferred, new Damage { Amount = 1 });
        commands.SetSparse(deferred, new Damage { Amount = 2 });

        commands.Playback();
        var entity = deferred.Resolve();
        ulong version = world.ComponentDataWriteVersion<Damage>(entity);
        commands.Clear();
        commands.SetSparse(entity, new Damage { Amount = 3 });
        commands.Playback();

        Assert.NotEqual(version, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(3, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void FailedCandidateCommandReplayRetainsSourceValuesAndWriteVersions()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        world.SetSparse(entity, new Damage { Amount = 2 });
        ulong tableVersion = world.ComponentDataWriteVersion<Position>(entity);
        ulong sparseVersion = world.ComponentDataWriteVersion<Damage>(entity);
        Entity expired = world.Create();
        world.Destroy(expired);
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        commands.Set(entity, new Position { X = 3 });
        commands.SetSparse(entity, new Damage { Amount = 4 });
        commands.Set(expired, new Position { X = 9 });

        Assert.Throws<ArgumentException>(commands.Playback);

        Assert.Equal(tableVersion, world.ComponentDataWriteVersion<Position>(entity));
        Assert.Equal(sparseVersion, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void MovingComponentStorageRetainsExistingWriteVersions()
    {
        using var world = new World();
        var first = world.Create(new Position { X = 1 });
        var second = world.Create(new Position { X = 2 });
        world.SetSparse(first, new Damage { Amount = 3 });
        world.SetSparse(second, new Damage { Amount = 4 });
        ulong firstVersion = world.ComponentDataWriteVersion<Position>(first);
        ulong tableVersion = world.ComponentDataWriteVersion<Position>(second);
        ulong sparseVersion = world.ComponentDataWriteVersion<Damage>(second);

        world.Add(first, new Velocity { X = 5 });
        world.RemoveSparse<Damage>(first);

        Assert.Equal(firstVersion, world.ComponentDataWriteVersion<Position>(first));
        Assert.Equal(tableVersion, world.ComponentDataWriteVersion<Position>(second));
        Assert.Equal(sparseVersion, world.ComponentDataWriteVersion<Damage>(second));
        Assert.Equal(2, world.Read<Position>(second).X);
        Assert.Equal(4, world.ReadSparse<Damage>(second).Amount);
    }

    [Fact]
    public void FaultingSparseWriteRetainsWrittenValueAndAdvancesVersion()
    {
        using var world = new World();
        var entity = world.Create();
        world.SetSparse(entity, new Damage { Amount = 1 });
        ulong version = world.ComponentDataWriteVersion<Damage>(entity);

        Assert.Throws<ArithmeticException>((Action)(() =>
        {
            var sparseScope = world.OpenSparseWrite<Damage>();
            try
            {
                ReadOnlySpan<Entity> entities = sparseScope.Entities;
                Span<Damage> values = sparseScope.Values;

                values[0].Amount = 3;
                throw new ArithmeticException();

            }
            finally { sparseScope.Dispose(); }
        }));

        Assert.NotEqual(version, world.ComponentDataWriteVersion<Damage>(entity));
        Assert.Equal(3, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void DestroyedEntitiesRejectValueReadsAndReportAbsentVersions()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        world.SetSparse(entity, new Damage { Amount = 2 });
        world.Destroy(entity);

        Assert.Throws<ArgumentException>(
            () => world.Read<Position>(entity));
        Assert.Null(world.ComponentDataWriteVersion(entity, ComponentId<Position>.Value));
        Assert.Throws<ArgumentException>(
            () => world.ReadSparse<Damage>(entity));
        Assert.Null(world.ComponentDataWriteVersion(entity, ComponentId<Damage>.Value));
    }

#if DEBUG
    [Fact]
    public void ReadingMissingTableComponentReportsAbsentVersion()
    {
        using var world = new World();
        var entity = world.Create();

        Assert.Null(world.ComponentDataWriteVersion(entity, ComponentId<Position>.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingMissingSparseComponentReportsAbsentVersion(bool typeAlreadyUsed)
    {
        using var world = new World();
        var entity = world.Create();
        if (typeAlreadyUsed)
            world.SetSparse(world.Create(), new Damage { Amount = 1 });

        Assert.Null(world.ComponentDataWriteVersion(entity, ComponentId<Damage>.Value));
    }
#endif

    private readonly struct PositionReplacement : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value) => view.Write(new Position { X = 3 });
    }
    private readonly struct SparseReplacement : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value) => view.WriteSparse(new Damage { Amount = 4 });
    }

}
