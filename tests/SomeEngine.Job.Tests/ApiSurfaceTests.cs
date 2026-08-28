namespace SomeEngine.Job.Tests;

using System.Reflection;

public sealed class ApiSurfaceTests
{
    public ApiSurfaceTests()
    {
        JobSystem.ResetForTesting();
    }

    [Fact]
    public void PublicSurfaceStaysSmall()
    {
        var publicTypes = typeof(JobSystem).Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "SomeEngine.Job.IJob",
                "SomeEngine.Job.IJobExternalFence",
                "SomeEngine.Job.IJobParallelFor",
                "SomeEngine.Job.IJobResourceProvider`2",
                "SomeEngine.Job.IndexRange",
                "SomeEngine.Job.JobHandle",
                "SomeEngine.Job.JobPayloadLane",
                "SomeEngine.Job.JobPriority",
                "SomeEngine.Job.JobResource",
                "SomeEngine.Job.JobResourceAccess",
                "SomeEngine.Job.JobResourceSafetyException",
                "SomeEngine.Job.JobResourceToken",
                "SomeEngine.Job.JobRuntimeConfig",
                "SomeEngine.Job.JobRuntimeStats",
                "SomeEngine.Job.JobSafetyMode",
                "SomeEngine.Job.JobScheduleOptions",
                "SomeEngine.Job.JobSystem",
                "SomeEngine.Job.ManagedPayloadPolicy",
                "SomeEngine.Job.WholeAccess"
            ],
            publicTypes);
    }

    [Fact]
    public void PublicJobSystemMemberSurfaceIsExplicit()
    {
        var properties = typeof(JobSystem)
            .GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["ManagedPayloadPolicy", "SafetyMode"],
            properties);

        var methods = typeof(JobSystem)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(MethodShape)
            .OrderBy(shape => shape, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "CombineDependencies(ReadOnlySpan`1)",
                "CreateExternalFenceHandle(IJobExternalFence)",
                "CreateExternalFenceHandle(IJobExternalFence,JobResourceAccess)",
                "CreateExternalFenceHandle(IJobExternalFence,ReadOnlySpan`1)",
                "CreateResource(String)",
                "CreateResourceToken(String)",
                "CreateScopeResource(String)",
                "CreateScopeResourceToken(String)",
                "GetPayloadLane()",
                "GetStats()",
                "Initialize(JobRuntimeConfig)",
                "OnCompleted(JobHandle,Action`2,Object)",
                "ReleaseResource(JobResource)",
                "ReleaseResourceToken(JobResourceToken)",
                "Schedule(T,JobHandle)",
                "Schedule(T,JobResourceAccess,JobHandle)",
                "Schedule(T,JobResourceAccess,JobScheduleOptions,JobHandle)",
                "Schedule(T,JobScheduleOptions,JobHandle)",
                "Schedule(T,ReadOnlySpan`1,JobHandle)",
                "Schedule(T,ReadOnlySpan`1,JobScheduleOptions,JobHandle)",
                "ScheduleParallel(T,Int32,Int32,JobHandle)",
                "ScheduleParallel(T,Int32,Int32,JobResourceAccess,JobHandle)",
                "ScheduleParallel(T,Int32,Int32,JobResourceAccess,JobScheduleOptions,JobHandle)",
                "ScheduleParallel(T,Int32,Int32,JobScheduleOptions,JobHandle)",
                "ScheduleParallel(T,Int32,Int32,ReadOnlySpan`1,JobHandle)",
                "ScheduleParallel(T,Int32,Int32,ReadOnlySpan`1,JobScheduleOptions,JobHandle)",
                "Shutdown()"
            ],
            methods);
    }

    [Fact]
    public void PublicJobResourceAccessMemberSurfaceIsExplicit()
    {
        var methods = typeof(JobResourceAccess)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(MethodShape)
            .OrderBy(shape => shape, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Exclusive(Dictionary`2)",
                "Exclusive(JobResource)",
                "Exclusive(JobResourceToken)",
                "Exclusive(List`1)",
                "Exclusive(Memory`1)",
                "Exclusive(Queue`1)",
                "Exclusive(Stack`1)",
                "Exclusive(TContainer)",
                "Exclusive(TContainer,TAccess)",
                "Exclusive(T[])",
                "Read(Dictionary`2)",
                "Read(JobResource)",
                "Read(JobResource,Int64,Int64)",
                "Read(JobResourceToken)",
                "Read(List`1)",
                "Read(List`1,Int64,Int64)",
                "Read(Memory`1)",
                "Read(Memory`1,Int64,Int64)",
                "Read(Queue`1)",
                "Read(ReadOnlyMemory`1)",
                "Read(ReadOnlyMemory`1,Int64,Int64)",
                "Read(Stack`1)",
                "Read(TContainer)",
                "Read(TContainer,TAccess)",
                "Read(T[])",
                "Read(T[],Int64,Int64)",
                "Read(T[],ReadOnlySpan`1)",
                "Read(T[],Span`1)",
                "Write(Dictionary`2)",
                "Write(JobResource)",
                "Write(JobResource,Int64,Int64)",
                "Write(JobResourceToken)",
                "Write(List`1)",
                "Write(List`1,Int64,Int64)",
                "Write(Memory`1)",
                "Write(Memory`1,Int64,Int64)",
                "Write(Queue`1)",
                "Write(Stack`1)",
                "Write(TContainer)",
                "Write(TContainer,TAccess)",
                "Write(T[])",
                "Write(T[],Int64,Int64)",
                "Write(T[],Span`1)"
            ],
            methods);

        Assert.DoesNotContain(methods, shape => shape.Contains("Object", StringComparison.Ordinal));
    }

    [Fact]
    public void InternalSchedulerKeepsSingleTypedWorkStreamPath()
    {
        var internalTypes = typeof(JobSystem).Assembly
            .GetTypes()
            .Where(type => !type.IsPublic && !type.IsNestedPublic)
            .ToArray();

        Assert.Contains(internalTypes, type => type.Name == "WorkStream");
        Assert.Contains(internalTypes, type => type.Name == "WorkStream`1");
        Assert.Contains(internalTypes, type => type.Name == "IWorkStreamItem`1");
        Assert.Contains(internalTypes, type => type.Name == "ScheduledJob`1");
        Assert.Contains(internalTypes, type => type.Name == "ScheduledParallelToken`1");
        Assert.Contains(internalTypes, type => type.Name == "ParallelJobGroup`1");
    }

    [Fact]
    public void IJobCanBeScheduledThroughPublicApi()
    {
        PublicApiJobs.Counter = 0;

        var handle = JobSystem.Schedule(new PublicApiJobs.IncrementJob());
        handle.Complete();

        Assert.Equal(1, PublicApiJobs.Counter);
    }

    [Fact]
    public void IJobParallelForCanBeScheduledThroughPublicApi()
    {
        var values = new int[7];

        var handle = JobSystem.ScheduleParallel(new PublicApiJobs.WriteIndexJob(values), values.Length, 3);
        handle.Complete();

        Assert.Equal([1, 1, 1, 1, 1, 1, 1], values);
    }

    [Fact]
    public void GenericIJobCanBeScheduledThroughGenericCallsiteWithoutRegistration()
    {
        PublicApiJobs.GenericIncrementJob<int>.Counter = 0;

        var handle = ScheduleGenericJob<int>(3);
        handle.Complete();

        Assert.Equal(3, PublicApiJobs.GenericIncrementJob<int>.Counter);
    }

    private static JobHandle ScheduleGenericJob<T>(int amount)
    {
        return JobSystem.Schedule(new PublicApiJobs.GenericIncrementJob<T>(amount));
    }

    private static string MethodShape(MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Select(parameter => parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!.Name
                : parameter.ParameterType.Name);

        return $"{method.Name}({string.Join(",", parameters)})";
    }

    private static class PublicApiJobs
    {
        internal static int Counter;

        internal struct IncrementJob : IJob
        {
            public void Execute()
            {
                Interlocked.Increment(ref Counter);
            }
        }

        internal readonly struct GenericIncrementJob<T> : IJob
        {
            internal static int Counter;

            private readonly int _amount;

            internal GenericIncrementJob(int amount)
            {
                _amount = amount;
            }

            public void Execute()
            {
                Interlocked.Add(ref Counter, _amount);
            }
        }

        internal readonly struct WriteIndexJob : IJobParallelFor
        {
            private readonly int[] _values;

            internal WriteIndexJob(int[] values)
            {
                _values = values;
            }

            public void Execute(int index)
            {
                Interlocked.Increment(ref _values[index]);
            }
        }
    }
}
