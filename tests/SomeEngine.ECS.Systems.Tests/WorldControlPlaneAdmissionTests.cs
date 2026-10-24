using ManualResetEventSlim = SomeEngine.Testing.TestSignal;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class WorldControlPlaneAdmissionTests : JobTest
{
    [Fact]
    public void AcquireSystemTickWaitsForCandidatePublicationAndCannotBeLost()
    {
        using var attempted = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.CreateEntity();
        _ = RelationshipJobAccess.TopologyWrite(world);
        using var hookStarted = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        releaseHook.WaitCallback = () => events.Enqueue("released");
        using var tickCompleted = new ManualResetEventSlim();
        tickCompleted.SetCallback = () => events.Enqueue("tickCompleted");
        Exception? flushFault = null;
        Exception? tickFault = null;
        uint acquired = 0;

        world.Hooks<TickTrigger>().OnAdd(
            (DeferredWorld _, Entity _, in TickTrigger _) =>
            {
                hookStarted.Set();
                if (!releaseHook.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out waiting to publish the tick candidate.");
            });
        world.Commands().Add(entity, new TickTrigger());

        var flushThread = new Thread(() =>
        {
            try
            {
                world.Flush();
            }
            catch (Exception exception)
            {
                flushFault = exception;
            }
        });
        var tickThread = new Thread(() =>
        {
            attempted.Set();
            try
            {
                acquired = world.AcquireSystemTick();
            }
            catch (Exception exception)
            {
                tickFault = exception;
            }
            finally
            {
                tickCompleted.Set();
            }
        });

        try
        {
            flushThread.Start();
            Assert.True(hookStarted.Wait(TimeSpan.FromSeconds(5)));
            tickThread.Start();
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));

            releaseHook.Set();
            Assert.True(flushThread.Join(TimeSpan.FromSeconds(5)));
            Assert.True(tickThread.Join(TimeSpan.FromSeconds(5)));

            Assert.Null(flushFault);
            Assert.Null(tickFault);
            Assert.Equal(unchecked(acquired + 1), world.CurrentTick);
        }
        finally
        {
            releaseHook.Set();
            flushThread.Join(TimeSpan.FromSeconds(5));
            tickThread.Join(TimeSpan.FromSeconds(5));
        }

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("tickCompleted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "tickCompleted"));
    }

    [Fact]
    public void ConvenienceQueryCapturesCurrentTickFromItsAdmittedPublishedRoot()
    {
        using var attempted = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.CreateEntity();
        QueryHandle query = world.Query(world.QueryDefinition().Read<QueryTickTrigger>());
        _ = RelationshipJobAccess.TopologyWrite(world);
        using var hookStarted = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        releaseHook.WaitCallback = () => events.Enqueue("released");
        using var queryCompleted = new ManualResetEventSlim();
        queryCompleted.SetCallback = () => events.Enqueue("queryCompleted");
        Exception? flushFault = null;
        Exception? queryFault = null;
        uint cursorCurrentVersion = 0;

        world.Hooks<QueryTickTrigger>().OnAdd(
            (DeferredWorld _, Entity _, in QueryTickTrigger _) =>
            {
                _ = world.AcquireSystemTick();
                hookStarted.Set();
                if (!releaseHook.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out waiting to publish the query candidate.");
            });
        world.Commands().Add(entity, new QueryTickTrigger());

        var flushThread = new Thread(() =>
        {
            try
            {
                world.Flush();
            }
            catch (Exception exception)
            {
                flushFault = exception;
            }
        });
        var queryThread = new Thread(() =>
        {
            attempted.Set();
            try
            {
                world.ExecuteQuery(
                    query,
                    lastSystemVersion: 0,
                    cursor => cursorCurrentVersion = cursor.CurrentSystemVersion);
            }
            catch (Exception exception)
            {
                queryFault = exception;
            }
            finally
            {
                queryCompleted.Set();
            }
        });

        try
        {
            flushThread.Start();
            Assert.True(hookStarted.Wait(TimeSpan.FromSeconds(5)));
            queryThread.Start();
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));

            releaseHook.Set();
            Assert.True(flushThread.Join(TimeSpan.FromSeconds(5)));
            Assert.True(queryThread.Join(TimeSpan.FromSeconds(5)));

            Assert.Null(flushFault);
            Assert.Null(queryFault);
            Assert.Equal(world.CurrentTick, cursorCurrentVersion);
        }
        finally
        {
            releaseHook.Set();
            flushThread.Join(TimeSpan.FromSeconds(5));
            queryThread.Join(TimeSpan.FromSeconds(5));
        }

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("queryCompleted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "queryCompleted"));
    }

    private struct TickTrigger : IComponent;

    private struct QueryTickTrigger : IComponent;
}
