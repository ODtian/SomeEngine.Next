namespace SomeEngine.RenderGraph.Diagnostics;

using System.Collections.Immutable;
using System.Text.Json.Serialization;

/// <summary>
/// The sole durable diagnostics document for one render-graph invocation. Every contained row is
/// detached from invocation storage and can safely cross the graph lifetime.
/// </summary>
public sealed partial class RenderGraphSnapshot
{
    public const int CurrentVersion = 8;

    [JsonConstructor]
    public RenderGraphSnapshot(
        int version = CurrentVersion,
        bool succeeded = false,
        ImmutableArray<Resource> resources = default,
        ImmutableArray<string> bufferViews = default,
        ImmutableArray<string> textureViews = default,
        ImmutableArray<string> accelerationStructures = default,
        ImmutableArray<Pass> passes = default,
        ImmutableArray<Access> accesses = default,
        ImmutableArray<string> shaderArguments = default,
        ImmutableArray<int> dependencies = default,
        ImmutableArray<Barrier> barriers = default,
        ImmutableArray<Command> units = default,
        ImmutableArray<Task> tasks = default,
        ImmutableArray<Batch> batches = default,
        ImmutableArray<Timing> timings = default)
    {
        Version = version;
        Succeeded = succeeded;
        Resources = OrEmpty(resources);
        BufferViews = OrEmpty(bufferViews);
        TextureViews = OrEmpty(textureViews);
        AccelerationStructures = OrEmpty(accelerationStructures);
        Passes = OrEmpty(passes);
        Accesses = OrEmpty(accesses);
        ShaderArguments = OrEmpty(shaderArguments);
        Dependencies = OrEmpty(dependencies);
        Barriers = OrEmpty(barriers);
        Units = OrEmpty(units);
        Tasks = OrEmpty(tasks);
        Batches = OrEmpty(batches);
        Timings = OrEmpty(timings);

        ImmutableArray<string> errors = ValidateRows(this);
        if (!errors.IsEmpty)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    public int Version { get; }
    public bool Succeeded { get; }
    public ImmutableArray<Resource> Resources { get; }
    public ImmutableArray<string> BufferViews { get; }
    public ImmutableArray<string> TextureViews { get; }
    public ImmutableArray<string> AccelerationStructures { get; }
    public ImmutableArray<Pass> Passes { get; }
    public ImmutableArray<Access> Accesses { get; }
    public ImmutableArray<string> ShaderArguments { get; }
    public ImmutableArray<int> Dependencies { get; }
    public ImmutableArray<Barrier> Barriers { get; }
    public ImmutableArray<Command> Units { get; }
    public ImmutableArray<Task> Tasks { get; }
    public ImmutableArray<Batch> Batches { get; }
    public ImmutableArray<Timing> Timings { get; }

    private static ImmutableArray<T> OrEmpty<T>(ImmutableArray<T> rows) =>
        rows.IsDefault ? ImmutableArray<T>.Empty : rows;

    public readonly record struct Resource(
        int Ordinal,
        string Kind,
        string? Name,
        bool Imported,
        bool Live,
        ulong LogicalSize,
        ulong PhysicalSize,
        ulong Alignment,
        string MemoryType,
        string HeapFlags,
        ulong CompatibilityClass,
        int Heap,
        ulong HeapOffset);

    public readonly record struct Pass(
        int Ordinal,
        int ExecutionOrdinal,
        string Name,
        QueueType Queue,
        PassFlags Flags,
        bool Live,
        bool Root,
        int AccessOffset,
        int AccessCount,
        int ShaderArgumentOffset,
        int ShaderArgumentCount,
        int DependencyOffset,
        int DependencyCount);

    public readonly record struct Access(
        int Ordinal,
        int PassOrdinal,
        int ResourceOrdinal,
        int ViewOrdinal,
        string ResourceKind,
        GraphAccess Flags,
        GraphResourceUsage State,
        ulong BufferOffset,
        ulong BufferSize,
        int FirstMip,
        int MipCount,
        int FirstLayer,
        int LayerCount,
        TextureAspects Planes);

    public readonly record struct Barrier(
        string Location,
        int OwnerOrdinal,
        int ResourceOrdinal,
        GraphResourceUsage? Before,
        GraphResourceUsage? After,
        TextureSubresourceRange? Range,
        BarrierKind Kind,
        QueueType? OtherQueue,
        TransitionOrigin Origin,
        int? AliasingBeforeResourceOrdinal = null);

    public enum BarrierKind : byte
    {
        Resource,
        QueueRelease,
        QueueAcquire,
        Aliasing,
    }

    public readonly record struct Command(
        int Ordinal,
        string Name,
        QueueType Queue,
        ImmutableArray<int> PassOrdinals,
        ImmutableArray<int> Dependencies,
        int AliasBarrierCount,
        int BarrierCount);

    public readonly record struct Task(
        int Ordinal,
        QueueType Queue,
        int RecordLane,
        bool RequiresCoordinator,
        bool Exclusive,
        ImmutableArray<int> UnitOrdinals,
        int BarrierCount);

    public readonly record struct Batch(
        int Ordinal,
        QueueType Queue,
        ImmutableArray<int> Dependencies,
        ImmutableArray<int> UnitOrdinals,
        ImmutableArray<int> TaskOrdinals,
        ImmutableArray<Fence> ExternalWaits,
        Fence? Position);

    public readonly record struct Fence(QueueType Queue, ulong Value);

    /// <summary>Exact monotonic coordinates in the explicitly named clock domain and unit.</summary>
    public readonly record struct Timing(
        string Name,
        ClockDomain ClockDomain,
        TimeUnit Unit,
        long Start,
        long Close)
    {
        public long Duration => checked(Close - Start);
    }
}

public enum ClockDomain : byte
{
    ProcessMonotonic,
}

public enum TransitionOrigin : byte
{
    TrackedResourceState,
    PlacementInitialState,
}

public enum TimeUnit : byte
{
    Nanosecond,
}
