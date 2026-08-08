using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class WorldControlPlaneAdmissionTests
{
    [Fact]
    public void AcquireSystemTickWaitsForCandidatePublicationAndCannotBeLost()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity();
        _ = RelationshipJobAccess.TopologyWrite(world);
        using var hookStarted = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        using var tickCompleted = new ManualResetEventSlim();
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

            Assert.False(tickCompleted.Wait(TimeSpan.FromMilliseconds(150)));
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
    }

    [Fact]
    public void ConvenienceQueryCapturesCurrentTickFromItsAdmittedPublishedRoot()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity();
        QueryHandle query = world.Query(world.QueryDefinition().Read<QueryTickTrigger>());
        _ = RelationshipJobAccess.TopologyWrite(world);
        using var hookStarted = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        using var queryCompleted = new ManualResetEventSlim();
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

            Assert.False(queryCompleted.Wait(TimeSpan.FromMilliseconds(150)));
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
    }

    private sealed class JobRuntimeScope : IDisposable
    {
        private readonly JobSafetyMode _safety = JobSystem.SafetyMode;
        private readonly ManagedPayloadPolicy _payload = JobSystem.ManagedPayloadPolicy;

        internal JobRuntimeScope(int workerCount)
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = workerCount,
                SafetyMode = _safety,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = _safety,
                ManagedPayloadPolicy = _payload,
            });
        }
    }

    private struct TickTrigger : IComponent;

    private struct QueryTickTrigger : IComponent;
}
