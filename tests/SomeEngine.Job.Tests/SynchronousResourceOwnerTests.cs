namespace SomeEngine.Job.Tests;

/// <summary>Tests caller-thread Resource Owner admission against the asynchronous frontier.</summary>
public sealed class SynchronousResourceOwnerTests
{
    public SynchronousResourceOwnerTests()
    {
        JobSystem.ResetForTesting(workerCount: 2);
        JobSystem.SafetyMode = JobSafetyMode.Checked;
    }

    [Fact]
    public void CallerOwnerWaitsForPriorOwnerAndBlocksLaterConflictUntilDispose()
    {
        JobResource resource = JobSystem.CreateResource("synchronous-owner-order");
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();
        using var acquired = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        using var laterWriterStarted = new ManualResetEventSlim();

        JobHandle priorWriter = JobSystem.Schedule(
            new BlockingJob(writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

        Exception? acquireFault = null;
        var acquiringThread = new Thread(() =>
        {
            try
            {
                using SynchronousResourceOwner owner = JobSystem.AcquireSynchronousAccess(
                    JobResourceAccess.Read(resource));
                acquired.Set();
                releaseOwner.Wait();
            }
            catch (Exception exception)
            {
                acquireFault = exception;
                acquired.Set();
            }
        });
        acquiringThread.Start();

        Assert.False(acquired.Wait(TimeSpan.FromMilliseconds(100)));
        writerGate.Set();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(acquireFault);
        priorWriter.Complete();

        JobHandle laterWriter = JobSystem.Schedule(
            new SignalJob(laterWriterStarted),
            JobResourceAccess.Write(resource));
        Assert.False(laterWriterStarted.Wait(TimeSpan.FromMilliseconds(100)));

        releaseOwner.Set();
        Assert.True(acquiringThread.Join(TimeSpan.FromSeconds(5)));
        laterWriter.Complete();
        Assert.True(laterWriterStarted.IsSet);
    }

    [Fact]
    public void FaultedResourceHazardDoesNotCancelCallerOwnerOrLeakTheFrontier()
    {
        JobResource resource = JobSystem.CreateResource("synchronous-owner-fault");
        JobHandle faulted = JobSystem.Schedule(
            new ThrowJob(),
            JobResourceAccess.Write(resource));

        using (JobSystem.AcquireSynchronousAccess(JobResourceAccess.Read(resource)))
        {
            Assert.True(faulted.IsCompleted);
        }

        Assert.Throws<OwnerTestException>(() => faulted.Complete());

        JobSystem.Schedule(new NoOpJob(), JobResourceAccess.Write(resource)).Complete();
    }

    [Fact]
    public void RunningJobCannotAcquireANestedSynchronousOwner()
    {
        JobResource resource = JobSystem.CreateResource("synchronous-owner-nested");
        JobHandle handle = JobSystem.Schedule(
            new NestedAcquireJob(JobResourceAccess.Write(resource)),
            JobResourceAccess.Write(resource));

        var error = Assert.Throws<InvalidOperationException>(() => handle.Complete());
        Assert.Contains("running Job", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroWorkerCallerPumpsThePriorOwnerBeforeAdmission()
    {
        JobSystem.ResetForTesting(workerCount: 0);
        JobResource resource = JobSystem.CreateResource("synchronous-owner-zero-worker");
        using var priorRan = new ManualResetEventSlim();
        JobHandle prior = JobSystem.Schedule(
            new SignalJob(priorRan),
            JobResourceAccess.Write(resource));

        using (SynchronousResourceOwner owner = JobSystem.AcquireSynchronousAccess(
                   JobResourceAccess.Read(resource)))
        {
            Assert.True(priorRan.IsSet);
            Assert.True(prior.IsCompleted);
        }

        JobSystem.Schedule(new NoOpJob(), JobResourceAccess.Write(resource)).Complete();
    }

    private readonly struct BlockingJob : IJob
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _gate;

        internal BlockingJob(ManualResetEventSlim started, ManualResetEventSlim gate)
        {
            _started = started;
            _gate = gate;
        }

        public void Execute()
        {
            _started.Set();
            _gate.Wait();
        }
    }

    private readonly struct SignalJob : IJob
    {
        private readonly ManualResetEventSlim _started;

        internal SignalJob(ManualResetEventSlim started)
        {
            _started = started;
        }

        public void Execute()
        {
            _started.Set();
        }
    }

    private readonly struct ThrowJob : IJob
    {
        public void Execute()
        {
            throw new OwnerTestException();
        }
    }

    private readonly struct NoOpJob : IJob
    {
        public void Execute()
        {
        }
    }

    private readonly struct NestedAcquireJob : IJob
    {
        private readonly JobResourceAccess _access;

        internal NestedAcquireJob(JobResourceAccess access)
        {
            _access = access;
        }

        public void Execute()
        {
            using SynchronousResourceOwner owner = JobSystem.AcquireSynchronousAccess(_access);
        }
    }

    private sealed class OwnerTestException : Exception
    {
    }
}
