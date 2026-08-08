using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

public sealed class UnboundStructuralMutationIsolationTests
{
    [Fact]
    public void StructuralCandidate_SerializesCrossThreadMutationBeforeSourceCanChange()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new IsolationValue { Value = 1 });
        WorldStructureRoot published = world.PublishedStructureRoot;
        using var rendezvous = new Barrier(2);
        using var writeAttempted = new ManualResetEventSlim();
        using var writeCompleted = new ManualResetEventSlim();
        Exception? writerFault = null;

        var writer = new Thread(() =>
        {
            rendezvous.SignalAndWait();
            writeAttempted.Set();
            try
            {
                world.Replace(entity, new IsolationValue { Value = 3 });
            }
            catch (Exception exception)
            {
                writerFault = exception;
            }
            finally
            {
                writeCompleted.Set();
            }
        });

        WorldStructureRoot candidate;
        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot;
            writer.Start();
            rendezvous.SignalAndWait();
            Assert.True(writeAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(writeCompleted.Wait(TimeSpan.FromMilliseconds(100)));

            world.Replace(entity, new IsolationValue { Value = 2 });
            mutation.Commit();
        }

        Assert.True(writeCompleted.Wait(TimeSpan.FromSeconds(5)));
        writer.Join();
        Assert.Null(writerFault);
        Assert.Equal(1, published.Components.Read<IsolationValue>(entity).Value);
        Assert.Same(candidate, world.PublishedStructureRoot);
        Assert.Equal(3, world.Read<IsolationValue>(entity).Value);
    }

    [Fact]
    public void StructuralCandidate_BlocksClockAllocationAndDoesNotLosePublishedVersion()
    {
        var world = new World();
        long topologyRevisionBefore = world.PublishedTopologyRevision;
        using var rendezvous = new Barrier(2);
        using var allocationAttempted = new ManualResetEventSlim();
        using var allocationCompleted = new ManualResetEventSlim();
        Exception? allocatorFault = null;
        uint allocatedVersion = 0;

        var allocator = new Thread(() =>
        {
            rendezvous.SignalAndWait();
            allocationAttempted.Set();
            try
            {
                allocatedVersion = world.AcquireSystemVersion();
            }
            catch (Exception exception)
            {
                allocatorFault = exception;
            }
            finally
            {
                allocationCompleted.Set();
            }
        });

        WorldStructureRoot candidate;
        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot;
            allocator.Start();
            rendezvous.SignalAndWait();
            Assert.True(allocationAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(allocationCompleted.Wait(TimeSpan.FromMilliseconds(100)));
            mutation.Commit();
        }

        Assert.True(allocationCompleted.Wait(TimeSpan.FromSeconds(5)));
        allocator.Join();
        Assert.Null(allocatorFault);
        Assert.Same(candidate, world.PublishedStructureRoot);
        Assert.NotEqual(0u, allocatedVersion);
        Assert.Equal(unchecked(allocatedVersion + 1), world.AcquireSystemVersion());
        Assert.Equal(topologyRevisionBefore + 1, world.PublishedTopologyRevision);
    }

    [Fact]
    public void StructuralCandidate_BlocksQueryRegistrationAndPublishesUsableHandle()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new IsolationValue { Value = 7 });
        QueryDefinition definition = world.QueryDefinition().Read<IsolationValue>().Build();
        long topologyRevisionBefore = world.PublishedTopologyRevision;
        using var rendezvous = new Barrier(2);
        using var registrationAttempted = new ManualResetEventSlim();
        using var registrationCompleted = new ManualResetEventSlim();
        Exception? registrationFault = null;
        QueryHandle handle = default;

        var registrar = new Thread(() =>
        {
            rendezvous.SignalAndWait();
            registrationAttempted.Set();
            try
            {
                handle = world.Query(definition);
            }
            catch (Exception exception)
            {
                registrationFault = exception;
            }
            finally
            {
                registrationCompleted.Set();
            }
        });

        WorldStructureRoot candidate;
        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot;
            registrar.Start();
            rendezvous.SignalAndWait();
            Assert.True(registrationAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(registrationCompleted.Wait(TimeSpan.FromMilliseconds(100)));
            mutation.Commit();
        }

        Assert.True(registrationCompleted.Wait(TimeSpan.FromSeconds(5)));
        registrar.Join();
        Assert.Null(registrationFault);
        Assert.Same(candidate, world.PublishedStructureRoot);
        Assert.Same(definition, world.GetQueryDefinition(handle));
        Assert.Equal(topologyRevisionBefore + 1, world.PublishedTopologyRevision);

        Entity observed = Entity.Null;
        world.ExecuteQuery(handle, cursor =>
        {
            foreach (var row in cursor.Rows)
                observed = row.Entity;
        });
        Assert.Equal(entity, observed);
    }

    private struct IsolationValue : IComponent
    {
        internal int Value;
    }
}
