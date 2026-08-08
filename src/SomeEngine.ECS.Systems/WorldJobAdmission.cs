using System.Buffers;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Bridges synchronous World calls into the same resource frontier used by scheduled work.
/// Per-thread nesting keeps compound owner operations admitted without reacquiring resources that
/// the active owner already covers.
/// </summary>
internal sealed class WorldJobAdmission : IWorldJobAdmission
{
    internal static WorldJobAdmission Instance { get; } = new();

    [ThreadStatic]
    private static Dictionary<World, AdmissionFrame>? s_frames;

    [ThreadStatic]
    private static Stack<AdmissionFrame>? s_framePool;

    private WorldJobAdmission()
    {
    }

    public bool HasCurrentJobScope => JobExecutionContext.IsActive;

    public bool HasCurrentThreadScope(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_frames is { } frames &&
            frames.TryGetValue(world, out AdmissionFrame? frame) &&
            frame.Depth > 0;
    }

    public void Enter(World world, in WorldJobAdmissionRequest request)
    {
        RequestedStorage[]? rented = null;
        int scratchLength = Math.Max(1, request.QueryStorageAccesses.Length);
        Span<RequestedStorage> scratch = scratchLength <= 64
            ? stackalloc RequestedStorage[scratchLength]
            : (rented = ArrayPool<RequestedStorage>.Shared.Rent(scratchLength))
                .AsSpan(0, scratchLength);
        try
        {
            int storageCount = CollectStorage(in request, scratch);
            EnterCore(world, in request, scratch[..storageCount]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<RequestedStorage>.Shared.Return(rented);
        }
    }

    public void Exit(World world, in WorldJobAdmissionRequest request)
    {
        Dictionary<World, AdmissionFrame>? frames = s_frames;
        if (frames is null || !frames.TryGetValue(world, out AdmissionFrame? frame))
            throw new InvalidOperationException("World Job admission scope is not active.");
        if (frame.Depth <= 0 || frame.Owners.Count != frame.Depth)
            throw new InvalidOperationException("World Job admission scope depth is unbalanced.");
        if (frame.Topology == WorldTopologyAccess.Read &&
            request.Topology == WorldTopologyAccess.Write)
        {
            throw new InvalidOperationException("World Job admission topology mode is unbalanced.");
        }

        RequestedStorage[]? rented = null;
        int scratchLength = Math.Max(1, request.QueryStorageAccesses.Length);
        Span<RequestedStorage> scratch = scratchLength <= 64
            ? stackalloc RequestedStorage[scratchLength]
            : (rented = ArrayPool<RequestedStorage>.Shared.Rent(scratchLength))
                .AsSpan(0, scratchLength);
        try
        {
            int storageCount = CollectStorage(in request, scratch);
            RemoveStorage(frame, scratch[..storageCount]);

            SynchronousResourceOwner owner = frame.Owners.Pop();
            frame.Depth--;
            if (frame.Depth == 0)
            {
                if (frame.Storage.Count != 0)
                    throw new InvalidOperationException("World storage admission depth is unbalanced.");
                frames.Remove(world);
                ReturnFrame(frame);
            }

            owner.Dispose();
        }
        finally
        {
            if (rented is not null)
                ArrayPool<RequestedStorage>.Shared.Return(rented);
        }
    }

    public void ValidateCommandBufferAccess(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!JobExecutionContext.IsActive)
            return;

        throw new InvalidOperationException(
            "CommandBuffer recording, playback, clearing, and disposal are not job-safe APIs. " +
            "Perform structural command work outside the Job callback or use a dedicated typed " +
            "job adapter that owns its producer storage.");
    }

    private static void EnterCore(
        World world,
        in WorldJobAdmissionRequest request,
        ReadOnlySpan<RequestedStorage> storage)
    {
        Dictionary<World, AdmissionFrame> frames = s_frames ??= new Dictionary<World, AdmissionFrame>();
        frames.TryGetValue(world, out AdmissionFrame? frame);
        bool runningJob = JobExecutionContext.IsActive;
        ValidateNesting(frame, in request, storage, runningJob);

        int maximumResourceCount = storage.Length + 1;
        JobResourceAccess[]? rented = null;
        Span<JobResourceAccess> resourceScratch = maximumResourceCount <= 65
            ? stackalloc JobResourceAccess[maximumResourceCount]
            : (rented = ArrayPool<JobResourceAccess>.Shared.Rent(maximumResourceCount))
                .AsSpan(0, maximumResourceCount);
        try
        {
            int resourceCount = CollectRequiredResources(
                world,
                frame,
                in request,
                storage,
                runningJob,
                resourceScratch);
            ReadOnlySpan<JobResourceAccess> required = resourceScratch[..resourceCount];

            SynchronousResourceOwner owner = default;
            if (runningJob)
            {
                // Whole-World writes are ordinary scalar APIs. A Write capability serializes
                // different owners, but does not partition multiple work items belonging to the
                // same parallel owner. Only runtime-owned packet/range adapters may write from a
                // multi-work-item owner because they carry a separate aliasing proof.
                bool requireSingleWorkItem =
                    request.Topology == WorldTopologyAccess.Write ||
                    request.CanWrite;
                for (int i = 0; i < required.Length; i++)
                {
                    JobSystem.RequireCurrentAccess(
                        required[i],
                        requireSingleWorkItem);
                }
            }
            else
            {
                owner = JobSystem.AcquireSynchronousAccess(required);
            }

            CommitOwner(frames, world, frame, in request, storage, owner);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<JobResourceAccess>.Shared.Return(rented);
        }
    }

    private static void CommitOwner(
        Dictionary<World, AdmissionFrame> frames,
        World world,
        AdmissionFrame? existing,
        in WorldJobAdmissionRequest request,
        ReadOnlySpan<RequestedStorage> storage,
        SynchronousResourceOwner owner)
    {
        AdmissionFrame frame = existing ?? RentFrame(request.Topology);
        int storageAdded = 0;
        bool ownerPushed = false;
        bool depthIncremented = false;
        bool frameAdded = false;
        try
        {
            frame.Owners.Push(owner);
            ownerPushed = true;
            for (; storageAdded < storage.Length; storageAdded++)
                AddStorage(frame, storage[storageAdded]);

            frame.Depth++;
            depthIncremented = true;
            if (existing is null)
            {
                frames.Add(world, frame);
                frameAdded = true;
            }
        }
        catch
        {
            for (int i = storageAdded - 1; i >= 0; i--)
                RemoveStorage(frame, storage[i]);
            if (depthIncremented)
                frame.Depth--;
            if (frameAdded)
                frames.Remove(world);
            if (ownerPushed)
                frame.Owners.Pop();
            owner.Dispose();
            if (existing is null)
                ReturnFrame(frame);
            throw;
        }
    }

    private static void ValidateNesting(
        AdmissionFrame? frame,
        in WorldJobAdmissionRequest request,
        ReadOnlySpan<RequestedStorage> storage,
        bool runningJob)
    {
        if (frame is null)
            return;

        if (frame.Topology == WorldTopologyAccess.Read &&
            request.Topology == WorldTopologyAccess.Write)
        {
            throw new InvalidOperationException(
                "Cannot upgrade a nested World topology admission from read to write.");
        }

        for (int i = 0; i < storage.Length; i++)
        {
            RequestedStorage requested = storage[i];
            if (frame.Storage.TryGetValue(requested.Key, out StorageFrame existing))
            {
                if (existing.Access == WorldStorageAccess.Read &&
                    requested.Access == WorldStorageAccess.Write)
                {
                    throw new InvalidOperationException(
                        "Cannot upgrade a nested World storage admission from read to write.");
                }

                continue;
            }

            if (!runningJob &&
                frame.Topology == WorldTopologyAccess.Read &&
                !frame.Storage.ContainsKey(requested.Key))
            {
                throw new InvalidOperationException(
                    "Cannot expand a nested World topology-read owner with a new storage resource. " +
                    "Declare the storage on the outer query/callback owner.");
            }
        }
    }

    private static int CollectRequiredResources(
        World world,
        AdmissionFrame? frame,
        in WorldJobAdmissionRequest request,
        ReadOnlySpan<RequestedStorage> storage,
        bool runningJob,
        Span<JobResourceAccess> destination)
    {
        int count = 0;
        // A running Job verifies every request, even if an ECS owner scope is nested. For a
        // synchronous caller, an existing owner already covers its topology resource.
        if (runningJob || frame is null)
        {
            destination[count++] = request.Topology == WorldTopologyAccess.Write
                ? RelationshipJobAccess.TopologyWrite(world)
                : RelationshipJobAccess.TopologyRead(world);
        }

        for (int i = 0; i < storage.Length; i++)
        {
            RequestedStorage requested = storage[i];
            // A synchronous topology writer owns all World storage. A running Job must still
            // declare each logical storage resource explicitly.
            if (!runningJob &&
                (request.Topology == WorldTopologyAccess.Write ||
                 frame?.Topology == WorldTopologyAccess.Write))
            {
                continue;
            }
            if (!runningJob && frame is not null && frame.Storage.ContainsKey(requested.Key))
            {
                continue;
            }

            destination[count++] = requested.Access == WorldStorageAccess.Write
                ? WorldStorageJobResources.Write(world, requested.Key)
                : WorldStorageJobResources.Read(world, requested.Key);
        }

        return count;
    }

    private static int CollectStorage(
        in WorldJobAdmissionRequest request,
        Span<RequestedStorage> destination)
    {
        if (request.StorageAccess != WorldStorageAccess.None)
        {
            destination[0] = new RequestedStorage(
                new WorldStorageResourceKey(
                    request.StorageKind,
                    request.StorageComponentId),
                request.StorageAccess);
            return 1;
        }

        ReadOnlySpan<WorldJobStorageAccess> queryAccesses =
            request.QueryStorageAccesses.Span;
        if (queryAccesses.IsEmpty)
            return 0;

        for (int i = 0; i < queryAccesses.Length; i++)
        {
            WorldJobStorageAccess entry = queryAccesses[i];
            destination[i] = new RequestedStorage(
                new WorldStorageResourceKey(entry.Kind, entry.ComponentId),
                entry.Access);
        }

        return queryAccesses.Length;
    }

    private static void AddStorage(AdmissionFrame frame, RequestedStorage requested)
    {
        if (frame.Storage.TryGetValue(requested.Key, out StorageFrame existing))
        {
            existing.Depth++;
            frame.Storage[requested.Key] = existing;
        }
        else
        {
            frame.Storage.Add(
                requested.Key,
                new StorageFrame(requested.Access, depth: 1));
        }
    }

    private static void RemoveStorage(
        AdmissionFrame frame,
        ReadOnlySpan<RequestedStorage> storage)
    {
        for (int i = 0; i < storage.Length; i++)
            RemoveStorage(frame, storage[i]);
    }

    private static void RemoveStorage(AdmissionFrame frame, RequestedStorage requested)
    {
        if (!frame.Storage.TryGetValue(requested.Key, out StorageFrame existing) ||
            existing.Depth <= 0)
        {
            throw new InvalidOperationException("World storage admission scope is not active.");
        }
        if (existing.Access == WorldStorageAccess.Read &&
            requested.Access == WorldStorageAccess.Write)
        {
            throw new InvalidOperationException("World storage admission mode is unbalanced.");
        }

        existing.Depth--;
        if (existing.Depth == 0)
            frame.Storage.Remove(requested.Key);
        else
            frame.Storage[requested.Key] = existing;
    }

    private static AdmissionFrame RentFrame(WorldTopologyAccess topology)
    {
        Stack<AdmissionFrame>? pool = s_framePool;
        AdmissionFrame frame = pool is { Count: > 0 }
            ? pool.Pop()
            : new AdmissionFrame();
        frame.Reset(topology);
        return frame;
    }

    private static void ReturnFrame(AdmissionFrame frame)
    {
        frame.Clear();
        (s_framePool ??= new Stack<AdmissionFrame>()).Push(frame);
    }

    private sealed class AdmissionFrame
    {
        internal WorldTopologyAccess Topology { get; private set; }

        internal int Depth { get; set; }

        internal Dictionary<WorldStorageResourceKey, StorageFrame> Storage { get; } = new();

        internal Stack<SynchronousResourceOwner> Owners { get; } = new();

        internal void Reset(WorldTopologyAccess topology)
        {
            Topology = topology;
        }

        internal void Clear()
        {
            Depth = 0;
            Storage.Clear();
            Owners.Clear();
        }
    }

    private struct StorageFrame
    {
        internal StorageFrame(WorldStorageAccess access, int depth)
        {
            Access = access;
            Depth = depth;
        }

        internal WorldStorageAccess Access;
        internal int Depth;
    }

    private readonly record struct RequestedStorage(
        WorldStorageResourceKey Key,
        WorldStorageAccess Access);
}
