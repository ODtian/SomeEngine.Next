using System.Runtime.ExceptionServices;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>The complete encoded arrays of one detailed tile.</summary>
public sealed class NavigationTileBytes(ReadOnlyMemory<byte>[] chunks)
{
    public ReadOnlyMemory<ReadOnlyMemory<byte>> Chunks { get; } = chunks;
}

/// <summary>A separately retained source whose directory is available before detailed data is read.</summary>
public abstract class NavigationTileSource<TTile> : IDisposable where TTile : class
{
    public abstract ReadOnlyMemory<NavigationRegionTile> Directory { get; }
    public abstract NavigationTileSource<TTile> Retain();
    public abstract ValueTask<NavigationTileBytes> ReadAsync(NavigationTileCoordinate tile, CancellationToken cancellationToken = default);
    /// <summary>Decodes already acquired bytes on the caller's worker.</summary>
    public abstract TTile Decode(NavigationTileCoordinate tile, NavigationTileBytes bytes);
    public abstract void Dispose();

    public NavigationTileRead<TTile> ScheduleRead(NavigationTileCoordinate tile, Scheduler? scheduler = null,
        CancellationToken cancellationToken = default) => new(Retain(), tile, scheduler ?? JobSystem.Default, cancellationToken);
}

/// <summary>Bridges a complete I/O result into a decoding job and retains its source through completion.</summary>
public sealed class NavigationTileRead<TTile> where TTile : class
{
    private TTile _tile = null!;
    private ExceptionDispatchInfo? _failure;
    public JobHandle Completion { get; }
    public TTile Complete() { Completion.Complete(); _failure?.Throw(); return _tile; }

    internal NavigationTileRead(NavigationTileSource<TTile> source, NavigationTileCoordinate coordinate,
        Scheduler scheduler, CancellationToken cancellationToken)
    {
        Task<NavigationTileBytes> bytes;
        try { bytes = source.ReadAsync(coordinate, cancellationToken).AsTask(); }
        catch { source.Dispose(); throw; }
        JobHandle ready = scheduler.CreateExternalFenceHandle(new ReadFence(bytes));
        JobHandle decoded = scheduler.Schedule(new ScheduleDecode(this, source, coordinate, bytes, scheduler), ready);
        JobHandle released = scheduler.ScheduleFinally(new ReleaseSource(source), JobScheduleOptions.Default, decoded);
        Completion = scheduler.CombineDependencies([decoded, released]);
    }

    internal static void CompleteAll(IEnumerable<NavigationTileRead<TTile>> reads, JobHandle applied)
    {
        ExceptionDispatchInfo? failure = null;
        foreach (NavigationTileRead<TTile> read in reads)
        {
            try { read.Complete(); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }
        try { applied.Complete(); }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        failure?.Throw();
    }

    private sealed class ReadFence(Task task) : IJobExternalFence
    {
        public bool IsSignaled => task.IsCompleted;
        public void OnSignaled(Action<object?> continuation, object? state)
        {
            if (task.IsCompleted) continuation(state);
            else task.ConfigureAwait(false).GetAwaiter().OnCompleted(() => continuation(state));
        }
    }

    private readonly struct ScheduleDecode(NavigationTileRead<TTile> result, NavigationTileSource<TTile> source,
        NavigationTileCoordinate coordinate, Task<NavigationTileBytes> bytes, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationTileBytes acquired;
            try { acquired = bytes.GetAwaiter().GetResult(); }
            catch (Exception error) { result._failure = ExceptionDispatchInfo.Capture(error); throw; }
            var accesses = new List<JobResourceAccess>();
            foreach (ReadOnlyMemory<byte> chunk in acquired.Chunks.Span)
                accesses.Add(NavigationJobAccess.Read(scheduler, chunk));
            scheduler.Schedule(new DecodeTile(result, source, coordinate, acquired), accesses.ToArray());
        }
    }

    private readonly struct DecodeTile(NavigationTileRead<TTile> result, NavigationTileSource<TTile> source,
        NavigationTileCoordinate coordinate, NavigationTileBytes bytes) : IJob
    {
        public void Execute()
        {
            try { result._tile = source.Decode(coordinate, bytes); }
            catch (Exception error) { result._failure = ExceptionDispatchInfo.Capture(error); throw; }
        }
    }

    private readonly struct ReleaseSource(NavigationTileSource<TTile> source) : IJob
    {
        public void Execute() => source.Dispose();
    }
}
