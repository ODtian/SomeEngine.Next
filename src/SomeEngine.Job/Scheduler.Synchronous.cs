using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    /// <summary>Executes borrowed work synchronously under its declared resource ranges.</summary>
    public void Execute<TWork>(scoped ref TWork work, scoped ReadOnlySpan<JobResourceAccess> accesses)
        where TWork : struct, IJob, allows ref struct
    {
        using Submission submission = EnterSubmission();
        EnsureCurrentScopeBelongsToThisRuntime();
        if (JobThreads.IsActive)
        {
            foreach (JobResourceAccess access in accesses) RequireCurrentAccess(access, requireSingleWorkItem: false);
            work.Execute();
            return;
        }

        SynchronousResourceAccess resources = AcquireSynchronousAccess(accesses);
        if (resources.Handle.Index == 0)
            resources = new(this, CreateState(pendingWork: 1, pendingDependencies: 0));
        ScopeToken previousScope = s_currentScope;
        Scheduler? previousScheduler = s_executing;
        s_currentScope = ScopeToken.FromHandle(resources.Handle);
        s_executing = this;
        JobThreads.Enter();
        ExceptionDispatchInfo? failure = null;
        try
        {
            work.Execute();
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
            RecordFault(resources.Handle, failure);
        }
        finally
        {
            s_currentScope = previousScope;
            s_executing = previousScheduler;
            JobThreads.Exit();
            resources.Dispose();
        }
        try { Complete(resources.Handle); }
        catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        failure?.Throw();
    }
}
