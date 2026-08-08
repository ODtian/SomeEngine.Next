namespace SomeEngine.Job.Tests;

public sealed class CurrentAccessCapabilityTests
{
    public CurrentAccessCapabilityTests()
    {
        JobSystem.ResetForTesting(workerCount: 4);
        JobSystem.SafetyMode = JobSafetyMode.Checked;
    }

    [Fact]
    public void RequireCurrentAccess_RejectsCallsOutsideAJobInEverySafetyMode()
    {
        JobResource resource = JobSystem.CreateResource("outside-current-owner");
        JobResourceAccess required = JobResourceAccess.Read(resource);

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.RequireCurrentAccess(required));

        JobSystem.SafetyMode = JobSafetyMode.Fast;
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.RequireCurrentAccess(required));
    }

    [Fact]
    public void RequireCurrentAccess_UsesReadWriteExclusiveCapabilityOrdering()
    {
        JobResource resource = JobSystem.CreateResource("mode-capability");

        JobSystem.Schedule(
                new RequireAccessJob(JobResourceAccess.Read(resource)),
                JobResourceAccess.Write(resource))
            .Complete();
        JobSystem.Schedule(
                new RequireAccessJob(JobResourceAccess.Write(resource)),
                JobResourceAccess.Exclusive(resource))
            .Complete();

        JobHandle readOnly = JobSystem.Schedule(
            new RequireAccessJob(JobResourceAccess.Write(resource)),
            JobResourceAccess.Read(resource));
        Assert.Throws<JobResourceSafetyException>(() => readOnly.Complete());

        JobHandle nonExclusive = JobSystem.Schedule(
            new RequireAccessJob(JobResourceAccess.Exclusive(resource)),
            JobResourceAccess.Write(resource));
        Assert.Throws<JobResourceSafetyException>(() => nonExclusive.Complete());
    }

    [Fact]
    public void RequireCurrentAccess_RequiresTheSameIdentityAndACoveringRange()
    {
        JobResource resource = JobSystem.CreateResource("range-capability");
        JobResource other = JobSystem.CreateResource("other-capability");
        JobResourceAccess declared = JobResourceAccess.Write(resource, start: 10, length: 10);

        JobSystem.Schedule(
                new RequireAccessJob(JobResourceAccess.Read(resource, start: 12, length: 3)),
                declared)
            .Complete();

        JobHandle outsideRange = JobSystem.Schedule(
            new RequireAccessJob(JobResourceAccess.Read(resource, start: 9, length: 3)),
            declared);
        Assert.Throws<JobResourceSafetyException>(() => outsideRange.Complete());

        JobHandle wholeResource = JobSystem.Schedule(
            new RequireAccessJob(JobResourceAccess.Read(resource)),
            declared);
        Assert.Throws<JobResourceSafetyException>(() => wholeResource.Complete());

        JobHandle wrongIdentity = JobSystem.Schedule(
            new RequireAccessJob(JobResourceAccess.Read(other)),
            declared);
        Assert.Throws<JobResourceSafetyException>(() => wrongIdentity.Complete());
    }

    [Fact]
    public void RequireCurrentAccess_AcceptsAContiguousUnionOfDeclaredRanges()
    {
        JobResource resource = JobSystem.CreateResource("range-union-capability");
        JobResourceAccess[] contiguous =
        [
            JobResourceAccess.Read(resource, start: 10, length: 5),
            JobResourceAccess.Write(resource, start: 15, length: 5),
        ];

        JobSystem.Schedule(
                new RequireAccessJob(
                    JobResourceAccess.Read(resource, start: 12, length: 6)),
                contiguous)
            .Complete();

        JobResourceAccess[] withGap =
        [
            JobResourceAccess.Write(resource, start: 10, length: 5),
            JobResourceAccess.Write(resource, start: 16, length: 4),
        ];
        JobHandle missingSegment = JobSystem.Schedule(
            new RequireAccessJob(
                JobResourceAccess.Read(resource, start: 12, length: 6)),
            withGap);

        Assert.Throws<JobResourceSafetyException>(() => missingSegment.Complete());
    }

    [Fact]
    public void LargeCapabilityIndex_PreservesModeAndContiguousUnionSemantics()
    {
        JobResource resource = JobSystem.CreateResource("indexed-range-union");
        JobResourceAccess[] declared =
        [
            JobResourceAccess.Read(resource, start: 0, length: 5),
            JobResourceAccess.Write(resource, start: 5, length: 5),
            JobResourceAccess.Write(resource, start: 20, length: 2),
            JobResourceAccess.Read(resource, start: 30, length: 2),
            JobResourceAccess.Write(resource, start: 40, length: 2),
            JobResourceAccess.Write(resource, start: 50, length: 2),
            JobResourceAccess.Read(resource, start: 60, length: 2),
            JobResourceAccess.Write(resource, start: 70, length: 2),
        ];

        JobSystem.Schedule(
                new RequireAccessJob(
                    JobResourceAccess.Read(resource, start: 2, length: 6)),
                declared)
            .Complete();
        JobSystem.Schedule(
                new RequireAccessJob(
                    JobResourceAccess.Read(resource, start: 70, length: 2)),
                declared)
            .Complete();

        JobHandle readCannotCoverWrite = JobSystem.Schedule(
            new RequireAccessJob(
                JobResourceAccess.Write(resource, start: 2, length: 6)),
            declared);
        Assert.Throws<JobResourceSafetyException>(() => readCannotCoverWrite.Complete());

        JobHandle gapCannotBeBridged = JobSystem.Schedule(
            new RequireAccessJob(
                JobResourceAccess.Read(resource, start: 8, length: 13)),
            declared);
        Assert.Throws<JobResourceSafetyException>(() => gapCannotBeBridged.Complete());
    }

    [Fact]
    public void StableResourceKey_ProvidesWholeAndRangeAccessAcrossRuntimeReinitialization()
    {
        var key = new JobResourceKey();
        JobResourceAccess old = JobResourceAccess.Write(key, start: 32, length: 16);
        JobSystem.Schedule(
                new RequireAccessJob(JobResourceAccess.Read(key, start: 36, length: 4)),
                old)
            .Complete();

        JobSystem.ResetForTesting(workerCount: 4);
        JobSystem.SafetyMode = JobSafetyMode.Checked;

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new RequireAccessJob(old), old));

        JobResourceAccess current = JobResourceAccess.Write(key, start: 32, length: 16);
        JobSystem.Schedule(
                new RequireAccessJob(JobResourceAccess.Read(key, start: 36, length: 4)),
                current)
            .Complete();
        JobSystem.Schedule(
                new RequireAccessJob(JobResourceAccess.Read(key)),
                JobResourceAccess.Write(key))
            .Complete();
    }

    [Fact]
    public void SingleWorkItemRequirement_RejectsAMultiBatchParallelOwner()
    {
        JobResource resource = JobSystem.CreateResource("single-owner-capability");
        JobResourceAccess write = JobResourceAccess.Write(resource);

        JobHandle parallel = JobSystem.ScheduleParallel(
            new RequireSingleOwnerAccessJob(write),
            length: 2,
            batchSize: 1,
            write);

        var error = Assert.Throws<JobResourceSafetyException>(() => parallel.Complete());
        Assert.Contains("single-work-item", error.Message, StringComparison.OrdinalIgnoreCase);

        JobSystem.ScheduleParallel(
                new RequireSingleOwnerAccessJob(write),
                length: 1,
                batchSize: 1,
                write)
            .Complete();
    }

    private readonly struct RequireAccessJob : IJob
    {
        private readonly JobResourceAccess _required;

        internal RequireAccessJob(JobResourceAccess required)
        {
            _required = required;
        }

        public void Execute()
        {
            JobSystem.RequireCurrentAccess(_required);
        }
    }

    private readonly struct RequireSingleOwnerAccessJob : IJobParallelFor
    {
        private readonly JobResourceAccess _required;

        internal RequireSingleOwnerAccessJob(JobResourceAccess required)
        {
            _required = required;
        }

        public void Execute(int index)
        {
            _ = index;
            JobSystem.RequireCurrentAccess(_required, requireSingleWorkItem: true);
        }
    }
}
