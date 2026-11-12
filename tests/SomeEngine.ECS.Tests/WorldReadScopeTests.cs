using ManualResetEventSlim = SomeEngine.Testing.TestSignal;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Tests;

public sealed class WorldReadScopeTests
{
    [Fact]
    public void ReadScopeWaitsForADeclaredWriterAndReadsItsCompletedValue()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity entity = world.Create(new ReadValue(1));
        Query query = world.Query<ReadValue>();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var readAttempted = new ManualResetEventSlim();
        releaseWriter.WaitCallback = () => events.Enqueue("released");
        Exception? writerFault = null, readFault = null;
        int observed = 0;
        var writer = new Thread(() =>
        {
            try
            {
                using QueryScope scope = query.Open();
                writerEntered.Set();
                if (!releaseWriter.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                foreach (QueryChunk chunk in scope) chunk.Write<ReadValue>(0) = new(2);
            }
            catch (Exception fault) { writerFault = fault; }
        });
        var reader = new Thread(() =>
        {
            readAttempted.Set();
            try
            {
                using QueryScope scope = query.OpenRead();
                events.Enqueue("readEntered");
                foreach (QueryChunk chunk in scope) observed = chunk.Read<ReadValue>()[0].Value;
            }
            catch (Exception fault) { readFault = fault; }
        });
        writer.Start();
        Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)));
        reader.Start();
        try { Assert.True(readAttempted.Wait(TimeSpan.FromSeconds(5))); }
        finally { releaseWriter.Set(); }
        writer.Join();
        reader.Join();
        Assert.Null(writerFault);
        Assert.Null(readFault);
        Assert.Equal(2, observed);
        Assert.Equal(2, world.ReadComponentData<ReadValue>(entity).Value);
        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("readEntered", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "readEntered"));
    }

    [Fact]
    public void ReadScopeHoldsItsValueUntilTheReaderFinishes()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity entity = world.Create(new ReadValue(1));
        Query query = world.Query<ReadValue>();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var readEntered = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        using var writerAttempted = new ManualResetEventSlim();
        releaseRead.WaitCallback = () => events.Enqueue("released");
        Exception? writerFault = null, readFault = null;
        int observed = 0;
        var reader = new Thread(() =>
        {
            try
            {
                using QueryScope scope = query.OpenRead();
                foreach (QueryChunk chunk in scope) observed = chunk.Read<ReadValue>()[0].Value;
                readEntered.Set();
                if (!releaseRead.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            catch (Exception fault) { readFault = fault; }
        });
        var writer = new Thread(() =>
        {
            writerAttempted.Set();
            try { world.Set(entity, new ReadValue(2)); }
            catch (Exception fault) { writerFault = fault; }
            finally { events.Enqueue("writerCompleted"); }
        });
        reader.Start();
        Assert.True(readEntered.Wait(TimeSpan.FromSeconds(5)));
        writer.Start();
        try { Assert.True(writerAttempted.Wait(TimeSpan.FromSeconds(5))); }
        finally { releaseRead.Set(); }
        reader.Join();
        writer.Join();
        Assert.Null(readFault);
        Assert.Null(writerFault);
        Assert.Equal(1, observed);
        Assert.Equal(2, world.ReadComponentData<ReadValue>(entity).Value);
        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("writerCompleted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "writerCompleted"));
    }

    [Fact]
    public void ReadScopeAllowsDeclaredNestedReadsAndKeepsWritesInTheirOwnWorld()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var source = new World(execution: new WorldJobs(scheduler));
        using var other = new World(execution: new WorldJobs(scheduler));
        Entity entity = source.Create(new ReadValue(1), new SideValue(2), new UnqueriedValue(3));
        Entity otherEntity = other.Create(new UnqueriedValue(4));
        Query outer = source.Query(new QueryDescription().WithStored<ReadValue>().Optional<SideValue>());
        Query inner = source.Query<SideValue>();
        int observed = 0, side = 0;
        using (QueryScope scope = outer.OpenRead())
        {
            foreach (QueryChunk chunk in scope) observed = chunk.Read<ReadValue>()[0].Value;
            using (QueryScope nested = inner.OpenRead())
                foreach (QueryChunk chunk in nested) side = chunk.Read<SideValue>()[0].Value;
            Assert.ThrowsAny<InvalidOperationException>(() => source.Set(entity, new UnqueriedValue(30)));
            other.Set(otherEntity, new UnqueriedValue(40));
        }
        Assert.Equal(1, observed);
        Assert.Equal(2, side);
        Assert.Equal(3, source.ReadComponentData<UnqueriedValue>(entity).Value);
        Assert.Equal(40, other.ReadComponentData<UnqueriedValue>(otherEntity).Value);
    }

    private readonly record struct ReadValue(int Value) : IAddedComponent;
    private readonly record struct SideValue(int Value) : IAddedComponent;
    private readonly record struct UnqueriedValue(int Value) : IAddedComponent;
}
