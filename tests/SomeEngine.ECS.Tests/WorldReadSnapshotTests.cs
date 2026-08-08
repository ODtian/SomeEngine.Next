using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

public sealed class WorldReadSnapshotTests
{
    [Fact]
    public void ReadSnapshot_WaitsForAnAdmittedWriterAndReadsItsPublishedValue()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new SnapshotValue(1));
        QueryHandle readQuery = world.Query(world.QueryDefinition().Read<SnapshotValue>());
        QueryHandle writeQuery = world.Query(world.QueryDefinition().Write<SnapshotValue>());
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var snapshotAttempted = new ManualResetEventSlim();
        using var snapshotEntered = new ManualResetEventSlim();
        using var snapshotCompleted = new ManualResetEventSlim();
        Exception? writerFault = null;
        Exception? snapshotFault = null;
        int observed = 0;

        var writer = new Thread(() =>
        {
            try
            {
                world.ExecuteQuery(writeQuery, cursor =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                    {
                        writerEntered.Set();
                        if (!releaseWriter.Wait(TimeSpan.FromSeconds(10)))
                            throw new TimeoutException("Test did not release the admitted writer.");
                        chunk.Write<SnapshotValue>()[0] = new SnapshotValue(2);
                    }
                });
            }
            catch (Exception exception)
            {
                writerFault = exception;
            }
        });
        var snapshot = new Thread(() =>
        {
            snapshotAttempted.Set();
            try
            {
                world.ExecuteReadSnapshot(readQuery, cursor =>
                {
                    snapshotEntered.Set();
                    foreach (QueryChunkView chunk in cursor.Chunks)
                        observed = chunk.Read<SnapshotValue>()[0].Value;
                });
            }
            catch (Exception exception)
            {
                snapshotFault = exception;
            }
            finally
            {
                snapshotCompleted.Set();
            }
        });

        writer.Start();
        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)));
        snapshot.Start();
        try
        {
            Assert.True(snapshotAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(snapshotEntered.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.False(snapshotCompleted.IsSet);
        }
        finally
        {
            releaseWriter.Set();
        }

        Assert.True(snapshotCompleted.Wait(TimeSpan.FromSeconds(5)));
        writer.Join();
        snapshot.Join();
        Assert.Null(writerFault);
        Assert.Null(snapshotFault);
        Assert.Equal(2, observed);
        Assert.Equal(2, world.Read<SnapshotValue>(entity).Value);
    }

    [Fact]
    public void ReadSnapshot_BlocksAWriterUntilItsCallbackReturns()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new SnapshotValue(1));
        QueryHandle readQuery = world.Query(world.QueryDefinition().Read<SnapshotValue>());
        using var snapshotEntered = new ManualResetEventSlim();
        using var releaseSnapshot = new ManualResetEventSlim();
        using var writerAttempted = new ManualResetEventSlim();
        using var writerCompleted = new ManualResetEventSlim();
        Exception? snapshotFault = null;
        Exception? writerFault = null;
        int observed = 0;

        var snapshot = new Thread(() =>
        {
            try
            {
                world.ExecuteReadSnapshot(readQuery, cursor =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                        observed = chunk.Read<SnapshotValue>()[0].Value;
                    snapshotEntered.Set();
                    if (!releaseSnapshot.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Test did not release the read snapshot.");
                });
            }
            catch (Exception exception)
            {
                snapshotFault = exception;
            }
        });
        var writer = new Thread(() =>
        {
            writerAttempted.Set();
            try
            {
                world.Replace(entity, new SnapshotValue(2));
            }
            catch (Exception exception)
            {
                writerFault = exception;
            }
            finally
            {
                writerCompleted.Set();
            }
        });

        snapshot.Start();
        Assert.True(snapshotEntered.Wait(TimeSpan.FromSeconds(5)));
        writer.Start();
        try
        {
            Assert.True(writerAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(writerCompleted.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            releaseSnapshot.Set();
        }

        Assert.True(writerCompleted.Wait(TimeSpan.FromSeconds(5)));
        snapshot.Join();
        writer.Join();
        Assert.Null(snapshotFault);
        Assert.Null(writerFault);
        Assert.Equal(1, observed);
        Assert.Equal(2, world.Read<SnapshotValue>(entity).Value);
    }

    [Fact]
    public void ReadSnapshot_RejectsExactWorldMutationAndAllowsNestedReads()
    {
        var source = new World();
        var other = new World();
        Entity sourceEntity = source.CreateEntity();
        source.Add(sourceEntity, new SnapshotValue(1));
        source.Add(sourceEntity, new SnapshotSideValue(2));
        source.Add(sourceEntity, new UnqueriedValue(3));
        Entity otherEntity = other.CreateEntity(new UnqueriedValue(4));
        QueryHandle outerQuery = source.Query(source.QueryDefinition().Read<SnapshotValue>());
        QueryHandle innerQuery = source.Query(source.QueryDefinition().Read<SnapshotSideValue>());
        int outerObserved = 0;
        int innerObserved = 0;
        InvalidOperationException? valueMutation = null;
        InvalidOperationException? clockMutation = null;
        InvalidOperationException? queryMutation = null;

        source.ExecuteReadSnapshot(outerQuery, outer =>
        {
            foreach (QueryChunkView chunk in outer.Chunks)
                outerObserved = chunk.Read<SnapshotValue>()[0].Value;

            source.ExecuteReadSnapshot(innerQuery, inner =>
            {
                foreach (QueryChunkView chunk in inner.Chunks)
                    innerObserved = chunk.Read<SnapshotSideValue>()[0].Value;
            });

            try
            {
                source.Replace(sourceEntity, new UnqueriedValue(30));
            }
            catch (InvalidOperationException exception)
            {
                valueMutation = exception;
            }

            try
            {
                source.AcquireSystemVersion();
            }
            catch (InvalidOperationException exception)
            {
                clockMutation = exception;
            }

            try
            {
                source.Query(source.QueryDefinition().Read<UnqueriedValue>());
            }
            catch (InvalidOperationException exception)
            {
                queryMutation = exception;
            }

            other.Replace(otherEntity, new UnqueriedValue(40));
        });

        Assert.Equal(1, outerObserved);
        Assert.Equal(2, innerObserved);
        Assert.NotNull(valueMutation);
        Assert.NotNull(clockMutation);
        Assert.NotNull(queryMutation);
        Assert.Contains("read-snapshot", valueMutation.Message, StringComparison.Ordinal);
        Assert.Contains("read-snapshot", clockMutation.Message, StringComparison.Ordinal);
        Assert.Contains("read-snapshot", queryMutation.Message, StringComparison.Ordinal);
        Assert.Equal(3, source.Read<UnqueriedValue>(sourceEntity).Value);
        Assert.Equal(40, other.Read<UnqueriedValue>(otherEntity).Value);
    }

    private readonly record struct SnapshotValue(int Value) : SomeEngine.ECS.IComponent;

    private readonly record struct SnapshotSideValue(int Value) : SomeEngine.ECS.IComponent;

    private readonly record struct UnqueriedValue(int Value) : SomeEngine.ECS.IComponent;
}
