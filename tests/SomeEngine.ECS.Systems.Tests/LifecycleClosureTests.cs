using SomeEngine.ECS.Components;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Registry;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class LifecycleClosureTests
{
    [Fact]
    public async Task Dispose_WaitsRootAndDescendantScopeBeforeOnDestroy()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new SpawnDescendantSystem());
        group.Update();
        Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> disposal = Task.Run(() => Capture(group.Dispose));
        Assert.True(SpinWait.SpinUntil(
            () => ThrowsDisposed(group.Update),
            TimeSpan.FromSeconds(5)));

        state.AllowDescendant.Set();
        Assert.True(state.DescendantStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(disposal.IsCompleted);
        Assert.False(state.Destroyed.IsSet);

        state.ReleaseDescendant.Set();
        Assert.Null(await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(state.Destroyed.IsSet);
        group.Dispose();
    }

    [Fact]
    public async Task Disable_WaitsOutstandingScopeAndEnableCreatesFreshLifetime()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        int slot = group.Add(new BlockingSystem());
        group.Update();
        Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> disable = Task.Run(() => Capture(() => group.Disable(slot)));
        Assert.False(disable.IsCompleted);
        state.ReleaseRoot.Set();
        Assert.Null(await disable.WaitAsync(TimeSpan.FromSeconds(5)));

        int updates = Volatile.Read(ref state.UpdateCount);
        group.Update();
        Assert.Equal(updates, Volatile.Read(ref state.UpdateCount));

        state.RootStarted.Reset();
        state.ReleaseRoot.Set();
        group.Enable(slot);
        group.Update();
        Assert.Equal(updates + 1, Volatile.Read(ref state.UpdateCount));
        group.Dispose();
    }

    [Fact]
    public async Task Remove_WaitsOutstandingScopeDestroysSystemAndShiftsSlots()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new BlockingSystem());
        group.Add(new PassiveSystem());
        group.Update();
        Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> removal = Task.Run(() => Capture(() => group.Remove(0)));
        Assert.False(removal.IsCompleted);
        state.ReleaseRoot.Set();

        Assert.Null(await removal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(state.Destroyed.IsSet);
        Assert.Equal(1, group.Count);
        Assert.Equal(0, group.GetSlot(0).Index);
        group.Dispose();
    }

    [Theory]
    [InlineData(PendingSystemTeardown.Disable)]
    [InlineData(PendingSystemTeardown.Remove)]
    public async Task PendingSlotTeardown_ReleasesGroupGateAndRejectsJobControl(
        PendingSystemTeardown operation)
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        int slot = group.Add(new BlockingSystem());
        state.PendingControl = () => _ = group.Count;
        group.Update();
        Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> teardown = Task.Run(() => Capture(() =>
        {
            if (operation == PendingSystemTeardown.Disable)
                group.Disable(slot);
            else
                group.Remove(slot);
        }));
        Assert.True(SpinWait.SpinUntil(
            () => group.IsLifecycleControlPending,
            TimeSpan.FromSeconds(5)));

        using var externalCountStarted = new ManualResetEventSlim();
        Task<int> externalCount = Task.Run(() =>
        {
            externalCountStarted.Set();
            return group.Count;
        });
        Assert.True(externalCountStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(externalCount.IsCompleted);

        state.ReleaseRoot.Set();
        Assert.True(state.PendingControlStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(state.PendingControlFinished.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(state.PendingControlError);
        Assert.Null(await teardown.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            operation == PendingSystemTeardown.Disable ? 1 : 0,
            await externalCount.WaitAsync(TimeSpan.FromSeconds(5)));
        group.Dispose();
    }

    [Theory]
    [InlineData(PendingSystemTeardown.Disable)]
    [InlineData(PendingSystemTeardown.Remove)]
    public async Task PendingSlotTeardown_JobFaultStillPulsesExternalWaiter(
        PendingSystemTeardown operation)
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        using var waiterStarted = new ManualResetEventSlim();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        Task<Exception?>? teardown = null;
        Task<int>? waiter = null;
        try
        {
            int slot = group.Add(new BlockingFaultingSystem());
            group.Update();
            Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

            teardown = StartLongRunning(() => Capture(() =>
            {
                if (operation == PendingSystemTeardown.Disable)
                    group.Disable(slot);
                else
                    group.Remove(slot);
            }));
            Assert.True(SpinWait.SpinUntil(
                () => group.IsLifecycleControlPending,
                TimeSpan.FromSeconds(5)));

            waiter = StartLongRunning(() =>
            {
                waiterStarted.Set();
                return group.Count;
            });
            Assert.True(waiterStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(waiter.IsCompleted);

            state.ReleaseRoot.Set();
            Exception? teardownError =
                await teardown.WaitAsync(TimeSpan.FromSeconds(5));
            AggregateException aggregate =
                Assert.IsType<AggregateException>(teardownError).Flatten();
            Assert.Contains(
                aggregate.InnerExceptions,
                exception => exception.Message == "teardown-job-fault");
            Assert.Equal(
                operation == PendingSystemTeardown.Disable ? 1 : 0,
                await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            state.ReleaseRoot.Set();
            if (teardown is not null)
            {
                try
                {
                    await teardown.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Preserve the assertion failure while ensuring the blocked root can unwind.
                }
            }

            _ = Capture(group.Dispose);
            if (waiter is not null)
            {
                try
                {
                    await waiter.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // A failing implementation may close the group while waking the waiter.
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentDispose_WaitsForClosureAndPendingJobControlFailsFast()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new BlockingSystem());
        state.PendingControl = () => _ = group.Count;
        group.Update();
        Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> first = Task.Run(() => Capture(group.Dispose));
        Assert.True(SpinWait.SpinUntil(
            () => ThrowsDisposed(group.Update),
            TimeSpan.FromSeconds(5)));

        using var secondStarted = new ManualResetEventSlim();
        Task<Exception?> second = Task.Run(() =>
        {
            secondStarted.Set();
            return Capture(group.Dispose);
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(second.IsCompleted);

        state.ReleaseRoot.Set();
        Assert.True(state.PendingControlStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(state.PendingControlFinished.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(state.PendingControlError);
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => _ = group.Count);
    }

    [Fact]
    public void Dispose_AggregatesJobAndDestroyFaultsAndContinuesTeardown()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new FaultingLifecycleSystem("destroy-one"));
        group.Add(new FaultingLifecycleSystem("destroy-two"));
        group.Update();

        AggregateException error = Assert.Throws<AggregateException>(group.Dispose).Flatten();

        Assert.Contains(error.InnerExceptions, exception => exception.Message == "job-fault");
        Assert.Contains(error.InnerExceptions, exception => exception.Message == "destroy-one");
        Assert.Contains(error.InnerExceptions, exception => exception.Message == "destroy-two");
        Assert.Equal(2, Volatile.Read(ref state.DestroyCount));
        group.Dispose();
    }

    [Fact]
    public void OnDestroy_LaunchesCleanupRootAndDisposeDrainsIt()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new SchedulingDestroySystem());
        group.Update();

        group.Dispose();

        Assert.Equal(1, Volatile.Read(ref state.DestroyScheduledJobRuns));
        group.Dispose();
    }

    [Fact]
    public void OnDestroy_CanScheduleParallelCleanupAndDisposeDrainsEveryWorkItem()
    {
        const int WorkItemCount = 37;
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new ParallelSchedulingDestroySystem(WorkItemCount));
        group.Update();

        group.Dispose();

        Assert.Equal(
            WorkItemCount,
            Volatile.Read(ref state.DestroyParallelJobRuns));
        group.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveAndDispose_WaitForOnDestroyCleanupJobs(bool remove)
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new BlockingDestroySystem(fault: false));
        group.Update();

        Task<Exception?> teardown = Task.Run(() => Capture(() =>
        {
            if (remove)
                group.Remove(0);
            else
                group.Dispose();
        }));

        Assert.True(state.DestroyJobStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(teardown.IsCompleted);
        Assert.False(state.DestroyJobFinished.IsSet);

        state.ReleaseDestroyJob.Set();
        Assert.Null(await teardown.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(state.DestroyJobFinished.IsSet);
        Assert.Equal(1, Volatile.Read(ref state.DestroyScheduledJobRuns));

        group.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnDestroyFault_StillDrainsCleanupJobAndAggregatesBothFaults(bool remove)
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new BlockingDestroySystem(fault: true));
        group.Update();

        Task<Exception?> teardown = Task.Run(() => Capture(() =>
        {
            if (remove)
                group.Remove(0);
            else
                group.Dispose();
        }));

        Assert.True(state.DestroyJobStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(teardown.IsCompleted);
        state.ReleaseDestroyJob.Set();

        AggregateException error = Assert.IsType<AggregateException>(
            await teardown.WaitAsync(TimeSpan.FromSeconds(5))).Flatten();
        Assert.Contains(
            error.InnerExceptions,
            exception => exception.Message == "destroy-callback-fault");
        Assert.Contains(
            error.InnerExceptions,
            exception => exception.Message == "destroy-job-fault");
        Assert.True(state.DestroyJobFinished.IsSet);

        group.Dispose();
    }

    [Fact]
    public void SuccessfulRoots_AreRetiredDuringLongLivedSystemAndWorldUse()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var world = new World();
        world.CreateEntity(new LifetimeComponent());
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new ManyRootsSystem(world));

        for (int i = 0; i < 2_000; i++)
            group.Update();

        Assert.True(SpinWait.SpinUntil(
            () => group.GetSlot(0).TrackedJobRootCount == 0 && world.TrackedJobRootCount == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(0, group.GetSlot(0).TrackedJobRootCount);
        Assert.Equal(0, world.TrackedJobRootCount);
        group.Dispose();
        world.Dispose();
    }

    [Fact]
    public void ObservedFaultRoots_AreRetiredDuringLongLivedSystemAndWorldUse()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var world = new World();
        world.CreateEntity(new LifetimeComponent());
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new ManyObservedFaultRootsSystem(world));

        for (int i = 0; i < 2_000; i++)
        {
            group.Update();
            Assert.Throws<InvalidOperationException>(() => state.LastHandle.Complete());
        }

        Assert.Equal(0, group.GetSlot(0).TrackedJobRootCount);
        Assert.Equal(0, world.TrackedJobRootCount);
        group.Dispose();
        world.Dispose();
    }

    [Fact]
    public void DisposeFromJobCallback_RejectsBeforeMutatingSystemGroupState()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new PassiveSystem());

        JobHandle disposer = JobSystem.Schedule(new DisposeSystemGroupJob(group));
        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => disposer.Complete());

        Assert.Contains("Job callback", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, group.Count);
        Assert.True(group.GetSlot(0).Enabled);
        group.Update();
        group.Dispose();
    }

    [Fact]
    public void CreateAndUpdateCallbacks_RejectReentrantLifecycleControlWithoutMutation()
    {
        using var runtime = new JobRuntimeScope();
        foreach (bool invokeOnCreate in new[] { true, false })
        {
            foreach (ReentrantGroupOperation operation in Enum.GetValues<ReentrantGroupOperation>())
            {
                using var state = new LifetimeState
                {
                    InvokeReentrantControlOnCreate = invokeOnCreate,
                };
                var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
                group.Add(new ReentrantControlSystem());
                state.ReentrantControl = () =>
                    InvokeReentrantGroupOperation(group, operation);

                group.Update();

                Assert.True(
                    state.ReentrantError is InvalidOperationException,
                    $"{operation} was not rejected during " +
                    (invokeOnCreate ? "OnCreate." : "OnUpdate."));
                Assert.Equal(1, group.Count);
                Assert.True(group.GetSlot(0).Enabled);
                group.Update();
                group.Dispose();
            }
        }
    }

    public static IEnumerable<object[]> DriverLifecycleReentryCases()
    {
        foreach (DriverLifecycleCallback callback in Enum.GetValues<DriverLifecycleCallback>())
        {
            foreach (ReentrantGroupOperation operation in Enum.GetValues<ReentrantGroupOperation>())
                yield return [callback, operation];
        }
    }

    [Theory]
    [MemberData(nameof(DriverLifecycleReentryCases))]
    public void DriverLifecycleCallbacks_RejectControlBeforeMutatingGroupState(
        DriverLifecycleCallback callback,
        ReentrantGroupOperation operation)
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState
        {
            DriverReentryPoint = callback,
        };
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        int activeSlot = group.Add(new CountingSystem());
        int disabledSlot = group.Add(new PassiveSystem());
        group.Disable(disabledSlot);
        state.ReentrantControl = () =>
            InvokeReentrantGroupOperation(group, operation, disabledSlot);

        try
        {
            group.Update();

            Assert.IsType<InvalidOperationException>(state.ReentrantError);
            Assert.Equal(2, group.Count);
            Assert.True(group.GetSlot(activeSlot).Enabled);
            Assert.False(group.GetSlot(disabledSlot).Enabled);
            Assert.Equal(1, Volatile.Read(ref state.UpdateCount));

            group.Update();
            Assert.Equal(2, Volatile.Read(ref state.UpdateCount));
        }
        finally
        {
            _ = Capture(group.Dispose);
        }
    }

    [Fact]
    public async Task ExternalDispose_WaitsForActiveUpdateCallback()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        group.Add(new BlockingCallbackSystem());

        Task update = Task.Run(group.Update);
        Assert.True(state.CallbackStarted.Wait(TimeSpan.FromSeconds(5)));
        Task<Exception?> disposal = Task.Run(() => Capture(group.Dispose));
        Assert.False(disposal.IsCompleted);

        state.ReleaseCallback.Set();
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    public static IEnumerable<object[]> ReentrantGroupOperationCases()
    {
        foreach (ReentrantGroupOperation operation in Enum.GetValues<ReentrantGroupOperation>())
            yield return [operation];
    }

    [Theory]
    [MemberData(nameof(ReentrantGroupOperationCases))]
    public void OnDestroy_RejectsEveryReentrantControlWithoutMutatingSlots(
        ReentrantGroupOperation operation)
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var group = new SystemGroup<LifetimeContext>(new LifetimeDriver(state));
        int destroyedSlot = group.Add(new ReentrantDestroySystem());
        int disabledSlot = group.Add(new PassiveSystem());
        group.Disable(disabledSlot);
        group.Update();
        state.ReentrantControl = () =>
            InvokeReentrantGroupOperation(group, operation, disabledSlot);

        try
        {
            group.Remove(destroyedSlot);

            Assert.IsType<InvalidOperationException>(state.ReentrantError);
            Assert.Equal(1, group.Count);
            Assert.False(group.GetSlot(0).Enabled);
        }
        finally
        {
            _ = Capture(group.Dispose);
        }
    }

    [Fact]
    public async Task WorldDispose_RejectsNewWorkAndWaitsTypedRootAndDescendant()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var world = new World();
        world.CreateEntity(new LifetimeComponent { Value = 1 });

        JobHandle root = JobSystem.Schedule(
            new WorldRootJob(world, state),
            ComponentJobAccess<LifetimeComponent>.Read(world));
        Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> disposal = Task.Run(() => Capture(world.Dispose));
        Assert.True(SpinWait.SpinUntil(
            () => ThrowsDisposed(() => _ = world.CreateEntity()),
            TimeSpan.FromSeconds(5)));

        state.AllowDescendant.Set();
        Assert.True(state.DescendantStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(disposal.IsCompleted);
        state.ReleaseDescendant.Set();

        Assert.Null(await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(root.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => world.CreateEntity());
        Assert.Throws<ObjectDisposedException>(
            () => ComponentJobAccess<LifetimeComponent>.Read(world));
        world.Dispose();
    }

    [Fact]
    public async Task WorldClosing_RejectsUnrelatedJobCallback()
    {
        using var runtime = new JobRuntimeScope();
        using var state = new LifetimeState();
        var world = new World();
        world.CreateEntity(new LifetimeComponent());
        JobSystem.Schedule(
            new BlockingRootJob(state),
            ComponentJobAccess<LifetimeComponent>.Read(world));
        Assert.True(state.RootStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> disposal = StartLongRunning(() => Capture(world.Dispose));
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ThrowsDisposed(() => _ = world.EntityCount),
                TimeSpan.FromSeconds(5)));

            JobHandle unrelated = JobSystem.Schedule(new ReadWorldJob(world));
            Assert.Throws<ObjectDisposedException>(() => unrelated.Complete());
        }
        finally
        {
            state.ReleaseRoot.Set();
        }

        Assert.Null(await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void WorldDispose_AggregatesFaultedJobAndRemainsIdempotent()
    {
        using var runtime = new JobRuntimeScope();
        var world = new World();
        world.CreateEntity(new LifetimeComponent());
        JobSystem.Schedule(
            new ThrowingJob(),
            ComponentJobAccess<LifetimeComponent>.Read(world));

        AggregateException error = Assert.Throws<AggregateException>(world.Dispose).Flatten();

        Assert.Contains(error.InnerExceptions, exception => exception.Message == "job-fault");
        world.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = world.EntityCount);
    }

    [Fact]
    public void WorldDispose_FromBoundQueryCallbackRejectsBeforeClosing()
    {
        using var runtime = new JobRuntimeScope();
        var world = new World();
        world.BindJobAdmission(WorldJobAdmission.Instance);
        var entity = world.CreateEntity(new LifetimeComponent { Value = 7 });
        var query = world.Query(world.QueryDefinition().Read<LifetimeComponent>());
        Exception? disposeError = null;

        world.ExecuteQuery(query, _ => disposeError = Capture(world.Dispose));

        Assert.IsType<InvalidOperationException>(disposeError);
        Assert.Equal(1, world.EntityCount);
        Assert.Equal(7, world.Read<LifetimeComponent>(entity).Value);
        world.Dispose();
    }

    [Fact]
    public void WorldDispose_FromBoundBundleCallbackRejectsBeforePublication()
    {
        using var runtime = new JobRuntimeScope();
        var world = new World();
        world.BindJobAdmission(WorldJobAdmission.Instance);
        int[] componentIds = [ComponentMetadata<LifetimeComponent>.Id];
        Exception? disposeError = null;

        var entity = world.ExecuteBundleSpawn(
            componentIds,
            view =>
            {
                disposeError = Capture(world.Dispose);
                var component = new LifetimeComponent { Value = 11 };
                view.Write(in component);
            });

        Assert.IsType<InvalidOperationException>(disposeError);
        Assert.True(world.IsAlive(entity));
        Assert.Equal(11, world.Read<LifetimeComponent>(entity).Value);
        world.Dispose();
    }

    [Fact]
    public void WorldDispose_FromBoundHookCallbackRejectsBeforeClosing()
    {
        using var runtime = new JobRuntimeScope();
        var world = new World();
        world.BindJobAdmission(WorldJobAdmission.Instance);
        Exception? hookError = null;
        world.Hooks<LifetimeComponent>().OnAdd(
            (SomeEngine.ECS.Hooks.DeferredWorld _,
                SomeEngine.ECS.Entities.Entity _,
                in LifetimeComponent _) => hookError = Capture(world.Dispose));

        world.CreateEntity(new LifetimeComponent());

        Assert.IsType<InvalidOperationException>(hookError);
        Assert.Equal(1, world.EntityCount);
        world.Dispose();
    }

    [Fact]
    public void WorldDispose_ContinuesHookAndStorageTeardownAfterCommandCleanupFault()
    {
        using var runtime = new JobRuntimeScope();
        var world = new World();
        world.Hooks<LifetimeComponent>().OnAdd(
            static (
                SomeEngine.ECS.Hooks.DeferredWorld deferred,
                SomeEngine.ECS.Entities.Entity entity,
                in LifetimeComponent component) => { });
        world.CreateEntity(new LifetimeComponent());
        CommandBuffer commands = world.Commands();
        using (CommandBuffer.RecordAccessScope access = commands.EnterRecordAccess())
            commands.RecordTypedRelationshipUnderGate(new ThrowingCancelCommand());

        AggregateException error = Assert.Throws<AggregateException>(world.Dispose).Flatten();

        Assert.Contains(
            error.InnerExceptions,
            exception => exception.Message == "command-cleanup-fault");
        Assert.False(world.HookStore.Any);
        Assert.Equal(0, world.PublishedStructureRoot.Entities.Count);
        world.Dispose();
    }

    [Fact]
    public async Task WorldAdmission_RevalidatesLifetimeAfterBoundAdmissionReturns()
    {
        using var runtime = new JobRuntimeScope();
        using var admission = new BlockingWorldAdmission();
        var world = new World();
        world.CreateEntity();
        world.BindJobAdmission(admission);

        Task<Exception?> lateRead = Task.Run(() =>
            Capture(() => _ = world.EntityCount));
        Assert.True(admission.FirstEnterStarted.Wait(TimeSpan.FromSeconds(5)));

        Task<Exception?> disposal = Task.Run(() => Capture(world.Dispose));
        Assert.Null(await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        admission.ReleaseFirstEnter.Set();

        Assert.IsType<ObjectDisposedException>(
            await lateRead.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, Volatile.Read(ref admission.EnterCount));
        Assert.Equal(2, Volatile.Read(ref admission.ExitCount));
    }

    [Fact]
    public async Task WorldDispose_DrainsUnboundScopeAndRejectsLateMutationAdmission()
    {
        using var runtime = new JobRuntimeScope();
        using var holderEntered = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        using var lateStarted = new ManualResetEventSlim();
        var world = new World();
        world.CreateEntity(new LifetimeComponent());
        var query = world.Query(
            world.QueryDefinition().ReadWrite<LifetimeComponent>());

        Task holder = Task.Run(() =>
            world.ExecuteQuery(query, _ =>
            {
                holderEntered.Set();
                releaseHolder.Wait();
            }));
        Assert.True(holderEntered.Wait(TimeSpan.FromSeconds(5)));

        var lateResult = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateThread = new Thread(() =>
        {
            lateStarted.Set();
            lateResult.SetResult(Capture(() => world.CreateEntity()));
        })
        {
            IsBackground = true,
        };
        lateThread.Start();
        Assert.True(lateStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(
            () => (lateThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5)));

        Task<Exception?> disposal = Task.Run(() => Capture(world.Dispose));
        Assert.True(SpinWait.SpinUntil(
            () => ThrowsDisposed(() => _ = world.EntityCount),
            TimeSpan.FromSeconds(5)));

        releaseHolder.Set();
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<ObjectDisposedException>(
            await lateResult.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await disposal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(lateThread.Join(TimeSpan.FromSeconds(5)));
    }

    private static bool ThrowsDisposed(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Task<T> StartLongRunning<T>(Func<T> action) =>
        Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private sealed class LifetimeDriver : ISystemDriver<LifetimeContext>
    {
        private readonly LifetimeState _state;
        private uint _version;

        internal LifetimeDriver(LifetimeState state)
        {
            _state = state;
        }

        public uint AcquireSystemVersion(ref SystemSlot slot)
        {
            _state.InvokeDriverReentrantControl(
                DriverLifecycleCallback.AcquireSystemVersion);
            return ++_version;
        }

        public LifetimeContext CreateContext(ref SystemSlot slot)
        {
            _state.InvokeDriverReentrantControl(DriverLifecycleCallback.CreateContext);
            return new LifetimeContext(_state);
        }

        public void BeforeUpdate(ref SystemSlot slot, ref LifetimeContext context)
        {
            _state.InvokeDriverReentrantControl(DriverLifecycleCallback.BeforeUpdate);
        }

        public void AfterUpdate(ref SystemSlot slot, ref LifetimeContext context)
        {
            _state.InvokeDriverReentrantControl(DriverLifecycleCallback.AfterUpdate);
        }
    }

    private readonly struct LifetimeContext
    {
        internal LifetimeContext(LifetimeState state)
        {
            State = state;
        }

        internal LifetimeState State { get; }
    }

    private sealed class LifetimeState : IDisposable
    {
        internal ManualResetEventSlim RootStarted { get; } = new();
        internal ManualResetEventSlim ReleaseRoot { get; } = new();
        internal ManualResetEventSlim AllowDescendant { get; } = new();
        internal ManualResetEventSlim DescendantStarted { get; } = new();
        internal ManualResetEventSlim ReleaseDescendant { get; } = new();
        internal ManualResetEventSlim Destroyed { get; } = new();
        internal ManualResetEventSlim DestroyJobStarted { get; } = new();
        internal ManualResetEventSlim ReleaseDestroyJob { get; } = new();
        internal ManualResetEventSlim DestroyJobFinished { get; } = new();
        internal int UpdateCount;
        internal int DestroyCount;
        internal int DestroyScheduledJobRuns;
        internal int DestroyParallelJobRuns;
        internal JobHandle LastHandle;
        internal Action? ReentrantControl;
        internal Exception? ReentrantError;
        internal Action? PendingControl;
        internal Exception? PendingControlError;
        internal bool InvokeReentrantControlOnCreate;
        internal DriverLifecycleCallback? DriverReentryPoint;
        internal ManualResetEventSlim CallbackStarted { get; } = new();
        internal ManualResetEventSlim ReleaseCallback { get; } = new();
        internal ManualResetEventSlim PendingControlStarted { get; } = new();
        internal ManualResetEventSlim PendingControlFinished { get; } = new();

        internal void InvokePendingControl()
        {
            Action? control = Interlocked.Exchange(ref PendingControl, null);
            if (control is null)
                return;

            PendingControlStarted.Set();
            try
            {
                control();
            }
            catch (Exception exception)
            {
                PendingControlError = exception;
            }
            finally
            {
                PendingControlFinished.Set();
            }
        }

        internal void InvokeDriverReentrantControl(DriverLifecycleCallback callback)
        {
            if (DriverReentryPoint != callback)
                return;

            Action? control = Interlocked.Exchange(ref ReentrantControl, null);
            if (control is null)
                return;

            try
            {
                control();
            }
            catch (Exception exception)
            {
                ReentrantError = exception;
            }
        }

        public void Dispose()
        {
            RootStarted.Dispose();
            ReleaseRoot.Dispose();
            AllowDescendant.Dispose();
            DescendantStarted.Dispose();
            ReleaseDescendant.Dispose();
            Destroyed.Dispose();
            DestroyJobStarted.Dispose();
            ReleaseDestroyJob.Dispose();
            DestroyJobFinished.Dispose();
            CallbackStarted.Dispose();
            ReleaseCallback.Dispose();
            PendingControlStarted.Dispose();
            PendingControlFinished.Dispose();
        }
    }

    private readonly struct SpawnDescendantSystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
            JobSystem.Schedule(new SpawnDescendantJob(context.State));
        }

        public void OnDestroy(ref LifetimeContext context)
        {
            context.State.Destroyed.Set();
        }
    }

    private readonly struct BlockingSystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
            Interlocked.Increment(ref context.State.UpdateCount);
            JobSystem.Schedule(new BlockingRootJob(context.State));
        }

        public void OnDestroy(ref LifetimeContext context)
        {
            context.State.Destroyed.Set();
        }
    }

    private readonly struct BlockingFaultingSystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
            JobSystem.Schedule(new BlockingFaultingRootJob(context.State));
        }
    }

    private readonly struct CountingSystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
            Interlocked.Increment(ref context.State.UpdateCount);
        }
    }

    private readonly struct PassiveSystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
        }
    }

    private readonly struct FaultingLifecycleSystem : ISystem<LifetimeContext>
    {
        private readonly string _destroyMessage;

        internal FaultingLifecycleSystem(string destroyMessage)
        {
            _destroyMessage = destroyMessage;
        }

        public void OnUpdate(ref LifetimeContext context)
        {
            JobSystem.Schedule(new ThrowingJob());
        }

        public void OnDestroy(ref LifetimeContext context)
        {
            Interlocked.Increment(ref context.State.DestroyCount);
            throw new InvalidOperationException(_destroyMessage);
        }
    }

    private readonly struct SchedulingDestroySystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
        }

        public void OnDestroy(ref LifetimeContext context)
        {
            JobSystem.Schedule(new CountJob(context.State));
        }
    }

    private readonly struct ParallelSchedulingDestroySystem(int workItemCount) :
        ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
        }

        public void OnDestroy(ref LifetimeContext context)
        {
            JobSystem.ScheduleParallel(
                new CountParallelDestroyJob(context.State),
                workItemCount,
                batchSize: 1);
        }
    }

    private readonly struct BlockingDestroySystem(bool fault) : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
        }

        public void OnDestroy(ref LifetimeContext context)
        {
            JobSystem.Schedule(new BlockingDestroyJob(context.State, fault));
            if (fault)
                throw new InvalidOperationException("destroy-callback-fault");
        }
    }

    private readonly struct ManyRootsSystem : ISystem<LifetimeContext>
    {
        private readonly World _world;

        internal ManyRootsSystem(World world)
        {
            _world = world;
        }

        public void OnUpdate(ref LifetimeContext context)
        {
            ComponentJobAccess<LifetimeComponent>.ScheduleRead(_world, new NoOpJob());
        }
    }

    private readonly struct ManyObservedFaultRootsSystem : ISystem<LifetimeContext>
    {
        private readonly World _world;

        internal ManyObservedFaultRootsSystem(World world)
        {
            _world = world;
        }

        public void OnUpdate(ref LifetimeContext context)
        {
            context.State.LastHandle =
                ComponentJobAccess<LifetimeComponent>.ScheduleRead(
                    _world,
                    new ThrowingJob());
        }
    }

    private readonly struct DisposeSystemGroupJob : IJob
    {
        private readonly SystemGroup<LifetimeContext> _group;

        internal DisposeSystemGroupJob(SystemGroup<LifetimeContext> group)
        {
            _group = group;
        }

        public void Execute()
        {
            _group.Dispose();
        }
    }

    private readonly struct ReentrantControlSystem : ISystem<LifetimeContext>
    {
        public void OnCreate(ref LifetimeContext context)
        {
            if (context.State.InvokeReentrantControlOnCreate)
                Invoke(ref context);
        }

        public void OnUpdate(ref LifetimeContext context)
        {
            if (!context.State.InvokeReentrantControlOnCreate)
                Invoke(ref context);
        }

        private static void Invoke(ref LifetimeContext context)
        {
            Action? control = Interlocked.Exchange(
                ref context.State.ReentrantControl,
                null);
            if (control is null)
                return;

            try
            {
                control();
            }
            catch (Exception exception)
            {
                context.State.ReentrantError = exception;
            }
        }
    }

    private readonly struct BlockingCallbackSystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
            context.State.CallbackStarted.Set();
            context.State.ReleaseCallback.Wait();
        }
    }

    private readonly struct ReentrantDestroySystem : ISystem<LifetimeContext>
    {
        public void OnUpdate(ref LifetimeContext context)
        {
        }

        public void OnDestroy(ref LifetimeContext context)
        {
            Action? control = Interlocked.Exchange(
                ref context.State.ReentrantControl,
                null);
            if (control is null)
                return;

            try
            {
                control();
            }
            catch (Exception exception)
            {
                context.State.ReentrantError = exception;
            }
        }
    }

    private readonly struct SpawnDescendantJob : IJob
    {
        private readonly LifetimeState _state;

        internal SpawnDescendantJob(LifetimeState state)
        {
            _state = state;
        }

        public void Execute()
        {
            _state.RootStarted.Set();
            _state.AllowDescendant.Wait();
            JobSystem.Schedule(new BlockingDescendantJob(_state));
        }
    }

    private readonly struct WorldRootJob : IJob
    {
        private readonly World _world;
        private readonly LifetimeState _state;

        internal WorldRootJob(World world, LifetimeState state)
        {
            _world = world;
            _state = state;
        }

        public void Execute()
        {
            _state.RootStarted.Set();
            _state.AllowDescendant.Wait();
            ComponentJobAccess<LifetimeComponent>.ScheduleRead(
                _world,
                new WorldDescendantJob(_world, _state));
        }
    }

    private readonly struct WorldDescendantJob : IJob
    {
        private readonly World _world;
        private readonly LifetimeState _state;

        internal WorldDescendantJob(World world, LifetimeState state)
        {
            _world = world;
            _state = state;
        }

        public void Execute()
        {
            _ = _world.EntityCount;
            _state.DescendantStarted.Set();
            _state.ReleaseDescendant.Wait();
        }
    }

    private readonly struct BlockingRootJob : IJob
    {
        private readonly LifetimeState _state;

        internal BlockingRootJob(LifetimeState state)
        {
            _state = state;
        }

        public void Execute()
        {
            _state.RootStarted.Set();
            _state.ReleaseRoot.Wait();
            _state.InvokePendingControl();
        }
    }

    private readonly struct BlockingFaultingRootJob : IJob
    {
        private readonly LifetimeState _state;

        internal BlockingFaultingRootJob(LifetimeState state)
        {
            _state = state;
        }

        public void Execute()
        {
            _state.RootStarted.Set();
            _state.ReleaseRoot.Wait();
            throw new InvalidOperationException("teardown-job-fault");
        }
    }

    private readonly struct ReadWorldJob : IJob
    {
        private readonly World _world;

        internal ReadWorldJob(World world)
        {
            _world = world;
        }

        public void Execute()
        {
            _ = _world.EntityCount;
        }
    }

    private readonly struct BlockingDescendantJob : IJob
    {
        private readonly LifetimeState _state;

        internal BlockingDescendantJob(LifetimeState state)
        {
            _state = state;
        }

        public void Execute()
        {
            _state.DescendantStarted.Set();
            _state.ReleaseDescendant.Wait();
        }
    }

    private readonly struct CountJob : IJob
    {
        private readonly LifetimeState _state;

        internal CountJob(LifetimeState state)
        {
            _state = state;
        }

        public void Execute()
        {
            Interlocked.Increment(ref _state.DestroyScheduledJobRuns);
        }
    }

    private readonly struct CountParallelDestroyJob(LifetimeState state) : IJobParallelFor
    {
        public void Execute(int index)
        {
            Interlocked.Increment(ref state.DestroyParallelJobRuns);
        }
    }

    private readonly struct BlockingDestroyJob(LifetimeState state, bool fault) : IJob
    {
        public void Execute()
        {
            state.DestroyJobStarted.Set();
            state.ReleaseDestroyJob.Wait();
            Interlocked.Increment(ref state.DestroyScheduledJobRuns);
            state.DestroyJobFinished.Set();
            if (fault)
                throw new InvalidOperationException("destroy-job-fault");
        }
    }

    private readonly struct ThrowingJob : IJob
    {
        public void Execute()
        {
            throw new InvalidOperationException("job-fault");
        }
    }

    private readonly struct NoOpJob : IJob
    {
        public void Execute()
        {
        }
    }

    private sealed class ThrowingCancelCommand : ITypedRelationshipCommand
    {
        public void Playback(World world, CommandPlaybackContext context)
        {
        }

        public void Cancel()
        {
            throw new InvalidOperationException("command-cleanup-fault");
        }

        public void PlaybackFailed()
        {
        }
    }

    private struct LifetimeComponent : IComponent
    {
        public int Value;
    }

    public enum DriverLifecycleCallback
    {
        AcquireSystemVersion,
        CreateContext,
        BeforeUpdate,
        AfterUpdate,
    }

    public enum ReentrantGroupOperation
    {
        Add,
        Enable,
        Disable,
        Remove,
        Dispose,
        Update,
    }

    public enum PendingSystemTeardown
    {
        Disable,
        Remove,
    }

    private static void InvokeReentrantGroupOperation(
        SystemGroup<LifetimeContext> group,
        ReentrantGroupOperation operation,
        int enableIndex = 0)
    {
        switch (operation)
        {
            case ReentrantGroupOperation.Add:
                group.Add(new PassiveSystem());
                break;
            case ReentrantGroupOperation.Enable:
                group.Enable(enableIndex);
                break;
            case ReentrantGroupOperation.Disable:
                group.Disable(0);
                break;
            case ReentrantGroupOperation.Remove:
                group.Remove(0);
                break;
            case ReentrantGroupOperation.Dispose:
                group.Dispose();
                break;
            case ReentrantGroupOperation.Update:
                group.Update();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class BlockingWorldAdmission : IWorldJobAdmission, IDisposable
    {
        internal ManualResetEventSlim FirstEnterStarted { get; } = new();
        internal ManualResetEventSlim ReleaseFirstEnter { get; } = new();
        internal int EnterCount;
        internal int ExitCount;

        public bool HasCurrentJobScope => false;

        public bool HasCurrentThreadScope(World world) => false;

        public void Enter(World world, in WorldJobAdmissionRequest request)
        {
            if (Interlocked.Increment(ref EnterCount) != 1)
                return;

            FirstEnterStarted.Set();
            if (!ReleaseFirstEnter.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The first admission was not released.");
        }

        public void Exit(World world, in WorldJobAdmissionRequest request)
        {
            Interlocked.Increment(ref ExitCount);
        }

        public void ValidateCommandBufferAccess(World world)
        {
        }

        public void Dispose()
        {
            ReleaseFirstEnter.Set();
            FirstEnterStarted.Dispose();
            ReleaseFirstEnter.Dispose();
        }
    }

    private sealed class JobRuntimeScope : IDisposable
    {
        private readonly ManagedPayloadPolicy _payloadPolicy = JobSystem.ManagedPayloadPolicy;
        private readonly JobSafetyMode _safetyMode = JobSystem.SafetyMode;

        internal JobRuntimeScope()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = 2,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
                SafetyMode = JobSafetyMode.Checked,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = 2,
                ManagedPayloadPolicy = _payloadPolicy,
                SafetyMode = _safetyMode,
            });
        }
    }
}
