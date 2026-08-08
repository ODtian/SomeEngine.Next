using System.Reflection;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal static class JobTraits
{
    internal static JobPayloadLane GetPayloadLane<T>()
        where T : struct
    {
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? JobPayloadLane.RefContaining
            : JobPayloadLane.RefFree;
    }

    /// <summary>
    /// Rejects compiler-generated async state machines before scheduling creates a completion
    /// state, registers resources, attaches scope children, updates counters, or queues work.
    /// Reflection is paid once per closed job type by the generic static cache below.
    /// </summary>
    internal static void RequireSynchronousJob<T>()
        where T : struct, IJob
    {
        if (JobExecuteTraits<T>.IsAsyncStateMachine)
            throw CreateAsyncExecuteException<T, IJob>();
    }

    /// <inheritdoc cref="RequireSynchronousJob{T}"/>
    internal static void RequireSynchronousParallelJob<T>()
        where T : struct, IJobParallelFor
    {
        if (ParallelJobExecuteTraits<T>.IsAsyncStateMachine)
            throw CreateAsyncExecuteException<T, IJobParallelFor>();
    }

    /// <summary>
    /// Shared validation hook for scheduling adapters whose user callback contract lives in a
    /// higher-level assembly. The adapter computes <paramref name="isAsyncStateMachine"/> once
    /// per closed job type, so the warmed scheduling path does not construct or box a delegate.
    /// </summary>
    internal static void RequireSynchronousCallback<T, TContract>(bool isAsyncStateMachine)
        where T : struct
    {
        if (isAsyncStateMachine)
            throw CreateAsyncExecuteException<T, TContract>();
    }

    /// <summary>
    /// Classifies an adapter callback implementation. Callers cache the result in a generic
    /// static field keyed by the closed job type.
    /// </summary>
    internal static bool IsAsyncCallback(MethodInfo implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        return IsAsyncImplementation(implementation);
    }

    private static InvalidOperationException CreateAsyncExecuteException<T, TContract>()
    {
        return new InvalidOperationException(
            $"Job type '{typeof(T).FullName ?? typeof(T).Name}' implements " +
            $"{typeof(TContract).Name}.Execute " +
            "as an async state machine. Job callbacks must complete synchronously; async void " +
            "would return before its work and resource ownership are complete.");
    }

    private static bool IsAsyncImplementation(MethodInfo implementation)
    {
        return implementation.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false);
    }

    private static class JobExecuteTraits<T>
        where T : struct, IJob
    {
        internal static readonly bool IsAsyncStateMachine = Create();

        private static bool Create()
        {
            T target = default;
            Action callback = target.Execute;
            return IsAsyncImplementation(callback.Method);
        }
    }

    private static class ParallelJobExecuteTraits<T>
        where T : struct, IJobParallelFor
    {
        internal static readonly bool IsAsyncStateMachine = Create();

        private static bool Create()
        {
            T target = default;
            Action<int> callback = target.Execute;
            return IsAsyncImplementation(callback.Method);
        }
    }

}

