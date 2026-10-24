using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Tests;

public sealed class ComponentWriteVersionTests
{
    [Fact]
    public void ReplacingTableValueAdvancesItsWriteVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.Add(entity, new Position { X = 1 });
        uint addedVersion = world.WriteVersion<Position>(entity);

        world.Replace(entity, new Position { X = 2 });

        Assert.NotEqual(addedVersion, world.WriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReaddingTableComponentAdvancesItsWriteVersion()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        uint version = world.WriteVersion<Position>(entity);

        world.Remove<Position>(entity);
        world.Add(entity, new Position { X = 2 });

        Assert.NotEqual(version, world.WriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReplacingSparseValueAdvancesItsWriteVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddSparse(entity, new Damage { Amount = 1 });
        uint addedVersion = world.SparseWriteVersion<Damage>(entity);

        world.ReplaceSparse(entity, new Damage { Amount = 2 });

        Assert.NotEqual(addedVersion, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowingSparseValuesForWriteAdvancesTheirVersions(bool withState)
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddSparse(entity, new Damage { Amount = 1 });
        uint version = world.SparseWriteVersion<Damage>(entity);

        if (withState)
        {
            int amount = 3;
            world.ExecuteSparseWrite<Damage, int>(ref amount,
                static (ReadOnlySpan<Entity> entities, Span<Damage> values, ref int state)
                    => values[0].Amount = state);
        }
        else
        {
            world.ExecuteSparseWrite<Damage>(
                static (entities, values) => values[0].Amount = 3);
        }

        Assert.NotEqual(version, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(3, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void CommandReplayAddsAndReplacesSparseValuesWithNewVersions()
    {
        var world = new World();
        var entity = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        commands.AddSparse(entity, new Damage { Amount = 1 });
        commands.Playback();
        uint version = world.SparseWriteVersion<Damage>(entity);

        commands.Clear();
        commands.ReplaceSparse(entity, new Damage { Amount = 2 });
        commands.Playback();

        Assert.NotEqual(version, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void CopyingAnEntityAdvancesWrittenTableAndSparseVersions()
    {
        var world = new World();
        var source = world.CreateEntity(new Position { X = 1 });
        world.AddSparse(source, new Damage { Amount = 2 });
        var target = world.CreateEntity(new Position { X = 3 });
        world.AddSparse(target, new Damage { Amount = 4 });
        world.CopyEntity(source, target);
        uint tableVersion = world.WriteVersion<Position>(target);
        uint sparseVersion = world.SparseWriteVersion<Damage>(target);

        world.CopyEntity(source, target);

        Assert.NotEqual(tableVersion, world.WriteVersion<Position>(target));
        Assert.NotEqual(sparseVersion, world.SparseWriteVersion<Damage>(target));
        Assert.Equal(1, world.Read<Position>(target).X);
        Assert.Equal(2, world.ReadSparse<Damage>(target).Amount);
    }

    [Fact]
    public void BundleReplacementAdvancesTableAndSparseWriteVersions()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        uint tableVersion = world.WriteVersion<Position>(entity);

        world.ExecuteBundleReplace(entity, [ComponentMetadata<Position>.Id],
            static view => view.Write(new Position { X = 3 }));

        Assert.NotEqual(tableVersion, world.WriteVersion<Position>(entity));
        world.AddSparse(entity, new Damage { Amount = 2 });
        uint sparseVersion = world.SparseWriteVersion<Damage>(entity);
        world.ExecuteBundleReplace(entity, [], [ComponentMetadata<Damage>.Id],
            static view => view.WriteSparse(new Damage { Amount = 4 }));

        Assert.NotEqual(sparseVersion, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(3, world.Read<Position>(entity).X);
        Assert.Equal(4, world.ReadSparse<Damage>(entity).Amount);
    }

    [Theory]
    [InlineData("RowWrite")]
    [InlineData("RowReadWrite")]
    [InlineData("ChunkWrite")]
    [InlineData("ChunkReadWrite")]
    [InlineData("ChunkRowWrite")]
    [InlineData("ChunkRowReadWrite")]
    public void TableWriteBorrowAdvancesVersion(string access)
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        uint version = world.WriteVersion<Position>(entity);
        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());

        world.ExecuteQuery(query, cursor =>
        {
            if (access == "RowWrite" || access == "RowReadWrite")
            {
                foreach (var row in cursor.Rows)
                {
                    if (access == "RowWrite")
                        row.Write<Position>().X = 2;
                    else
                        row.ReadWrite<Position>().X = 2;
                }
                return;
            }

            foreach (var chunk in cursor.Chunks)
            {
                switch (access)
                {
                    case "ChunkWrite": chunk.Write<Position>()[0].X = 2; break;
                    case "ChunkReadWrite": chunk.ReadWrite<Position>()[0].X = 2; break;
                    case "ChunkRowWrite": chunk.Write<Position>(0).X = 2; break;
                    case "ChunkRowReadWrite": chunk.ReadWrite<Position>(0).X = 2; break;
                }
            }
        });

        Assert.NotEqual(version, world.WriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReadsPreserveTableAndSparseWriteVersions()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        world.AddSparse(entity, new Damage { Amount = 2 });
        uint tableVersion = world.WriteVersion<Position>(entity);
        uint sparseVersion = world.SparseWriteVersion<Damage>(entity);
        var query = world.Query(world.QueryDefinition().Read<Position>());
        uint tick = world.CurrentTick;

        world.ExecuteQuery(query, static cursor =>
        {
            foreach (var row in cursor.Rows)
                Assert.Equal(1, row.Read<Position>().X);
            foreach (var chunk in cursor.Chunks)
                Assert.Equal(1, chunk.Read<Position>()[0].X);
        });
        world.ExecuteSparseRead<Damage>(
            static (entities, values) => Assert.Equal(2, values[0].Amount));
        int amount = 0;
        world.ExecuteSparseRead<Damage, int>(ref amount,
            static (ReadOnlySpan<Entity> entities, ReadOnlySpan<Damage> values, ref int state)
                => state = values[0].Amount);
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
        Assert.Equal(2, amount);
        Assert.Equal(tableVersion, world.WriteVersion<Position>(entity));
        Assert.Equal(sparseVersion, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(tick, world.CurrentTick);
    }

    [Fact]
    public void CommandReplayAddsAndReplacesTableValuesWithNewVersions()
    {
        var world = new World();
        var entity = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        commands.Add(entity, new Position { X = 1 });
        commands.Playback();
        uint version = world.WriteVersion<Position>(entity);
        commands.Clear();

        commands.Replace(entity, new Position { X = 2 });
        commands.Playback();

        Assert.NotEqual(version, world.WriteVersion<Position>(entity));
        Assert.Equal(2, world.Read<Position>(entity).X);
    }

    [Fact]
    public void SparseCommandsResolveDeferredEntities()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        var deferred = commands.CreateEntity();
        commands.AddSparse(deferred, new Damage { Amount = 1 });
        commands.ReplaceSparse(deferred, new Damage { Amount = 2 });

        commands.Playback();
        var entity = deferred.Resolve();
        uint version = world.SparseWriteVersion<Damage>(entity);
        commands.Clear();
        commands.ReplaceSparse(entity, new Damage { Amount = 3 });
        commands.Playback();

        Assert.NotEqual(version, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(3, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void FailedCommandReplayRetainsValuesAndWriteVersions()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        world.AddSparse(entity, new Damage { Amount = 2 });
        uint tableVersion = world.WriteVersion<Position>(entity);
        uint sparseVersion = world.SparseWriteVersion<Damage>(entity);
        using var commands = new CommandBuffer(world);
        commands.Replace(entity, new Position { X = 3 });
        commands.ReplaceSparse(entity, new Damage { Amount = 4 });
        commands.Remove<Health>(entity);

        Assert.Throws<InvalidOperationException>(commands.Playback);

        Assert.Equal(tableVersion, world.WriteVersion<Position>(entity));
        Assert.Equal(sparseVersion, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(2, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void MovingComponentStorageRetainsExistingWriteVersions()
    {
        var world = new World();
        var first = world.CreateEntity(new Position { X = 1 });
        var second = world.CreateEntity(new Position { X = 2 });
        world.AddSparse(first, new Damage { Amount = 3 });
        world.AddSparse(second, new Damage { Amount = 4 });
        uint firstVersion = world.WriteVersion<Position>(first);
        uint tableVersion = world.WriteVersion<Position>(second);
        uint sparseVersion = world.SparseWriteVersion<Damage>(second);

        world.Add(first, new Velocity { X = 5 });
        world.RemoveSparse<Damage>(first);

        Assert.Equal(firstVersion, world.WriteVersion<Position>(first));
        Assert.Equal(tableVersion, world.WriteVersion<Position>(second));
        Assert.Equal(sparseVersion, world.SparseWriteVersion<Damage>(second));
        Assert.Equal(2, world.Read<Position>(second).X);
        Assert.Equal(4, world.ReadSparse<Damage>(second).Amount);
    }

    [Fact]
    public void FaultingSparseWriteKeepsItsValueAndWriteVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddSparse(entity, new Damage { Amount = 1 });
        uint version = world.SparseWriteVersion<Damage>(entity);

        Assert.Throws<ArithmeticException>(() => world.ExecuteSparseWrite<Damage>(
            static (entities, values) =>
            {
                values[0].Amount = 3;
                throw new ArithmeticException();
            }));

        Assert.NotEqual(version, world.SparseWriteVersion<Damage>(entity));
        Assert.Equal(3, world.ReadSparse<Damage>(entity).Amount);
    }

    [Fact]
    public void DestroyedEntitiesUseTheExistingReadGuardForVersions()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        world.AddSparse(entity, new Damage { Amount = 2 });
        world.DestroyEntity(entity);

        var tableReadError = Assert.Throws<InvalidOperationException>(
            () => world.Read<Position>(entity));
        var tableVersionError = Assert.Throws<InvalidOperationException>(
            () => world.WriteVersion<Position>(entity));
        var sparseReadError = Assert.Throws<InvalidOperationException>(
            () => world.ReadSparse<Damage>(entity));
        var sparseVersionError = Assert.Throws<InvalidOperationException>(
            () => world.SparseWriteVersion<Damage>(entity));

        Assert.Equal(tableReadError.Message, tableVersionError.Message);
        Assert.Equal(sparseReadError.Message, sparseVersionError.Message);
    }

#if DEBUG
    [Fact]
    public void ReadingMissingTableComponentVersionReportsItsType()
    {
        var world = new World();
        var entity = world.CreateEntity();

        var error = Assert.Throws<InvalidOperationException>(
            () => world.WriteVersion<Position>(entity));

        Assert.Contains(nameof(Position), error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingMissingSparseComponentVersionReportsItsType(bool typeAlreadyUsed)
    {
        var world = new World();
        var entity = world.CreateEntity();
        if (typeAlreadyUsed)
            world.AddSparse(world.CreateEntity(), new Damage { Amount = 1 });

        var error = Assert.Throws<InvalidOperationException>(
            () => world.SparseWriteVersion<Damage>(entity));

        Assert.Contains(nameof(Damage), error.Message);
    }
#endif
}
