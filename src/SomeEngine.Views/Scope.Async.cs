using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

public static partial class Scope
{
    /// <summary>Runs a child task until its key changes or its creating view is released.</summary>
    /// <remarks>The view generator supplies the call-site identity. Prefer a static callback.</remarks>
    public static void Launch<TKey>(TKey key, Func<TKey, CancellationToken, BuildTask> run)
    {
        DevelopmentCheck.Fail("Launch calls require source-generator interception. Use the callKey overload for an explicit identity.");
        Launch(key, run, 0);
    }

    /// <summary>Runs a keyed child task at an explicit call-site identity.</summary>
    public static void Launch<TKey>(TKey key, Func<TKey, CancellationToken, BuildTask> run, ulong callKey)
    {
        CheckBuild(Current);
        DevelopmentCheck.Require(run is not null, "Scope.Launch requires a task callback.");
        var identity = AsyncIdentity(callKey);
        LaunchComputation<TKey> task = FindAsync<LaunchComputation<TKey>>(identity.Key, identity.Occurrence)
            ?? new LaunchComputation<TKey>(identity.Key, identity.Occurrence);
        task.Update(key, run);
    }

    /// <summary>Retains a fetched value while requesting a changed source.</summary>
    /// <remarks>The view generator supplies the call-site identity. Prefer a static callback.</remarks>
    public static Resource<T> Resource<TSource, T>(TSource source, Func<TSource, CancellationToken, ValueTask<T>> fetch)
    {
        DevelopmentCheck.Fail("Resource calls require source-generator interception. Use the callKey overload for an explicit identity.");
        return Resource(source, fetch, 0);
    }

    /// <summary>Retains a resource at an explicit call-site identity.</summary>
    public static Resource<T> Resource<TSource, T>(TSource source, Func<TSource, CancellationToken, ValueTask<T>> fetch, ulong callKey)
    {
        CheckBuild(Current);
        DevelopmentCheck.Require(fetch is not null, "Scope.Resource requires a fetch callback.");
        var identity = AsyncIdentity(callKey);
        ResourceComputation<TSource, T> request = FindAsync<ResourceComputation<TSource, T>>(identity.Key, identity.Occurrence)
            ?? new ResourceComputation<TSource, T>(identity.Key, identity.Occurrence);
        request.Update(source, fetch);
        return request.Resource;
    }

    private static (ulong Key, int Occurrence) AsyncIdentity(ulong key)
    {
        ScopeData current = Current;
        if (current.KeyPrefix is ulong prefix)
            key = CombineKeys(prefix, key);
        var occurrences = current.ViewData.AsyncOccurrences ??= new();
        occurrences.TryGetValue(key, out int occurrence);
        occurrences[key] = occurrence + 1;
        return (key, occurrence);
    }

    private static T? FindAsync<T>(ulong key, int occurrence) where T : AsyncComputation
    {
        ScopeData current = Current;
        if (current.ViewData.PreviousComputations is not { } previous)
            return null;
        foreach (Computation computation in previous.Items)
            if (computation.Parent == current.Computation && computation.Async is T task &&
                task.Key == key && task.Occurrence == occurrence)
            {
                RetainComputation(computation);
                foreach (Computation child in previous.Items)
                    if (child.IsChildOf(computation))
                        RetainComputation(child);
                return task;
            }
        return null;
    }
}
