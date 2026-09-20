using System.Runtime.InteropServices;

namespace SomeEngine.Job;

public readonly struct JobResourceAccess
{
    private const string SpanSliceMessage = "Span must be a slice of the supplied array.";
    private const string UnsupportedMemoryOwnerMessage = "Only array-backed Memory<T> and ReadOnlyMemory<T> can be bound automatically. Use a custom job resource provider for other memory owners.";
    private const string RangeOutsideContainerMessage = "Range must stay within the container.";

    internal readonly ResourceKind Kind;
    internal readonly int Id;
    internal readonly int Version;
    internal readonly long Generation;
    internal readonly JobAccessMode Mode;
    internal readonly bool HasRange;
    internal readonly long RangeStart;
    internal readonly long RangeLength;

    internal bool Covers(JobResourceAccess required)
    {
        if (!CoversIdentityAndMode(required))
        {
            return false;
        }

        if (!HasRange)
        {
            return true;
        }

        if (!required.HasRange)
        {
            return false;
        }

        long end = RangeStart + RangeLength;
        long requiredEnd = required.RangeStart + required.RangeLength;
        return RangeStart <= required.RangeStart && end >= requiredEnd;
    }

    internal bool CoversIdentityAndMode(JobResourceAccess required)
    {
        return Kind == required.Kind
            && Id == required.Id
            && Version == required.Version
            && Generation == required.Generation
            && ModeCovers(Mode, required.Mode);
    }

    private static bool ModeCovers(JobAccessMode declared, JobAccessMode required)
    {
        return declared switch
        {
            JobAccessMode.Read => required == JobAccessMode.Read,
            JobAccessMode.Write => required is JobAccessMode.Read or JobAccessMode.Write,
            JobAccessMode.Exclusive => true,
            _ => false,
        };
    }

    private JobResourceAccess(
        ResourceKind kind,
        int id,
        int version,
        long generation,
        JobAccessMode mode,
        bool hasRange,
        long rangeStart,
        long rangeLength)
    {
        Kind = kind;
        Id = id;
        Version = version;
        Generation = generation;
        Mode = mode;
        HasRange = hasRange;
        RangeStart = rangeStart;
        RangeLength = rangeLength;
    }

    public static JobResourceAccess Read(JobResource resource)
    {
        return Create(ResourceKind.Resource, resource.Id, resource.Version, resource.Generation, JobAccessMode.Read);
    }

    public static JobResourceAccess Read(JobResource resource, long start, long length)
    {
        return CreateRange(
            ResourceKind.Resource,
            resource.Id,
            resource.Version,
            resource.Generation,
            JobAccessMode.Read,
            new IndexRange(start, length));
    }

    internal static JobResourceAccess Read(JobResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        JobResourceToken token = JobSystem.GetContainerResourceToken(key);
        return Create(ResourceKind.Token, token.Id, token.Version, token.Generation, JobAccessMode.Read);
    }

    internal static JobResourceAccess Read(JobResourceKey key, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(key);
        JobResourceToken token = JobSystem.GetContainerResourceToken(key);
        return CreateRange(
            ResourceKind.Token,
            token.Id,
            token.Version,
            token.Generation,
            JobAccessMode.Read,
            new IndexRange(start, length));
    }

    public static JobResourceAccess Read(JobResourceToken token)
    {
        return Create(ResourceKind.Token, token.Id, token.Version, token.Generation, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(T[] array)
    {
        return CreateContainerAccess(array, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(T[] array, long start, long length)
    {
        return CreateContainerRangeAccess(array, array.LongLength, new IndexRange(start, length), JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(T[] array, Span<T> span)
    {
        return CreateArraySpanRangeAccess(array, span, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(T[] array, ReadOnlySpan<T> span)
    {
        return CreateArraySpanRangeAccess(array, span, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(List<T> list)
    {
        return CreateContainerAccess(list, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(List<T> list, long start, long length)
    {
        return CreateContainerRangeAccess(list, list.Count, new IndexRange(start, length), JobAccessMode.Read);
    }

    public static JobResourceAccess Read<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        return CreateContainerAccess(dictionary, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(Stack<T> stack)
    {
        return CreateContainerAccess(stack, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(Queue<T> queue)
    {
        return CreateContainerAccess(queue, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(Memory<T> memory)
    {
        return CreateMemoryAccess<T>(memory, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(Memory<T> memory, long start, long length)
    {
        return CreateMemoryRangeAccess<T>(memory, new IndexRange(start, length), JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(ReadOnlyMemory<T> memory)
    {
        return CreateMemoryAccess(memory, JobAccessMode.Read);
    }

    public static JobResourceAccess Read<T>(ReadOnlyMemory<T> memory, long start, long length)
    {
        return CreateMemoryRangeAccess(memory, new IndexRange(start, length), JobAccessMode.Read);
    }

    public static JobResourceAccess Read<TContainer, TProvider>(ref TContainer container)
        where TProvider : struct, IJobResourceProvider<TContainer, WholeAccess>
    {
        return TProvider.Read(ref container, default);
    }

    public static JobResourceAccess Read<TContainer, TProvider, TAccess>(ref TContainer container, TAccess access)
        where TProvider : struct, IJobResourceProvider<TContainer, TAccess>
    {
        return TProvider.Read(ref container, access);
    }

    public static JobResourceAccess Write(JobResource resource)
    {
        return Create(ResourceKind.Resource, resource.Id, resource.Version, resource.Generation, JobAccessMode.Write);
    }

    public static JobResourceAccess Write(JobResource resource, long start, long length)
    {
        return CreateRange(
            ResourceKind.Resource,
            resource.Id,
            resource.Version,
            resource.Generation,
            JobAccessMode.Write,
            new IndexRange(start, length));
    }

    internal static JobResourceAccess Write(JobResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        JobResourceToken token = JobSystem.GetContainerResourceToken(key);
        return Create(ResourceKind.Token, token.Id, token.Version, token.Generation, JobAccessMode.Write);
    }

    internal static JobResourceAccess Write(JobResourceKey key, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(key);
        JobResourceToken token = JobSystem.GetContainerResourceToken(key);
        return CreateRange(
            ResourceKind.Token,
            token.Id,
            token.Version,
            token.Generation,
            JobAccessMode.Write,
            new IndexRange(start, length));
    }

    public static JobResourceAccess Write(JobResourceToken token)
    {
        return Create(ResourceKind.Token, token.Id, token.Version, token.Generation, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(T[] array)
    {
        return CreateContainerAccess(array, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(T[] array, long start, long length)
    {
        return CreateContainerRangeAccess(array, array.LongLength, new IndexRange(start, length), JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(T[] array, Span<T> span)
    {
        return CreateArraySpanRangeAccess(array, span, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(List<T> list)
    {
        return CreateContainerAccess(list, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(List<T> list, long start, long length)
    {
        return CreateContainerRangeAccess(list, list.Count, new IndexRange(start, length), JobAccessMode.Write);
    }

    public static JobResourceAccess Write<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        return CreateContainerAccess(dictionary, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(Stack<T> stack)
    {
        return CreateContainerAccess(stack, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(Queue<T> queue)
    {
        return CreateContainerAccess(queue, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(Memory<T> memory)
    {
        return CreateMemoryAccess<T>(memory, JobAccessMode.Write);
    }

    public static JobResourceAccess Write<T>(Memory<T> memory, long start, long length)
    {
        return CreateMemoryRangeAccess<T>(memory, new IndexRange(start, length), JobAccessMode.Write);
    }

    public static JobResourceAccess Write<TContainer, TProvider>(ref TContainer container)
        where TProvider : struct, IJobResourceProvider<TContainer, WholeAccess>
    {
        return TProvider.Write(ref container, default);
    }

    public static JobResourceAccess Write<TContainer, TProvider, TAccess>(ref TContainer container, TAccess access)
        where TProvider : struct, IJobResourceProvider<TContainer, TAccess>
    {
        return TProvider.Write(ref container, access);
    }

    public static JobResourceAccess Exclusive(JobResource resource)
    {
        return Create(ResourceKind.Resource, resource.Id, resource.Version, resource.Generation, JobAccessMode.Exclusive);
    }

    internal static JobResourceAccess Exclusive(JobResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        JobResourceToken token = JobSystem.GetContainerResourceToken(key);
        return Create(ResourceKind.Token, token.Id, token.Version, token.Generation, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive(JobResourceToken token)
    {
        return Create(ResourceKind.Token, token.Id, token.Version, token.Generation, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive<T>(T[] array)
    {
        return CreateContainerAccess(array, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive<T>(List<T> list)
    {
        return CreateContainerAccess(list, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        return CreateContainerAccess(dictionary, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive<T>(Stack<T> stack)
    {
        return CreateContainerAccess(stack, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive<T>(Queue<T> queue)
    {
        return CreateContainerAccess(queue, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive<T>(Memory<T> memory)
    {
        return CreateMemoryAccess<T>(memory, JobAccessMode.Exclusive);
    }

    public static JobResourceAccess Exclusive<TContainer, TProvider>(ref TContainer container)
        where TProvider : struct, IJobResourceProvider<TContainer, WholeAccess>
    {
        return TProvider.Exclusive(ref container, default);
    }

    public static JobResourceAccess Exclusive<TContainer, TProvider, TAccess>(ref TContainer container, TAccess access)
        where TProvider : struct, IJobResourceProvider<TContainer, TAccess>
    {
        return TProvider.Exclusive(ref container, access);
    }

    private static JobResourceAccess Create(
        ResourceKind kind,
        int id,
        int version,
        long generation,
        JobAccessMode mode)
    {
        return new JobResourceAccess(
            kind,
            id,
            version,
            generation,
            mode,
            hasRange: false,
            rangeStart: 0,
            rangeLength: 0);
    }

    private static JobResourceAccess CreateContainerAccess(object container, JobAccessMode mode)
    {
        ArgumentNullException.ThrowIfNull(container);
        JobResourceToken token = JobSystem.GetContainerResourceToken(container);
        return Create(ResourceKind.Token, token.Id, token.Version, token.Generation, mode);
    }

    private static JobResourceAccess CreateContainerRangeAccess(
        object container,
        long count,
        IndexRange range,
        JobAccessMode mode)
    {
        ArgumentNullException.ThrowIfNull(container);
        ValidateContainerRange(range, count);
        JobResourceToken token = JobSystem.GetContainerResourceToken(container);
        return CreateRange(ResourceKind.Token, token.Id, token.Version, token.Generation, mode, range);
    }

    private static JobResourceAccess CreateArraySpanRangeAccess<T>(
        T[] array,
        ReadOnlySpan<T> span,
        JobAccessMode mode)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (span.Length == 0)
        {
            return CreateContainerAccess(array, mode);
        }

        Span<T> ownerSpan = array.AsSpan();
        if (!ownerSpan.Overlaps(span, out int elementOffset)
            || elementOffset < 0
            || elementOffset > ownerSpan.Length - span.Length)
        {
            throw new ArgumentException(SpanSliceMessage, nameof(span));
        }

        return CreateContainerRangeAccess(
            array,
            array.LongLength,
            new IndexRange(elementOffset, span.Length),
            mode);
    }

    private static JobResourceAccess CreateMemoryAccess<T>(ReadOnlyMemory<T> memory, JobAccessMode mode)
    {
        ArraySegment<T> segment = GetArraySegment(memory);
        JobResourceToken token = JobSystem.GetContainerResourceToken(segment.Array!);
        return segment.Count == 0
            ? Create(ResourceKind.Token, token.Id, token.Version, token.Generation, mode)
            : CreateRange(
                ResourceKind.Token,
                token.Id,
                token.Version,
                token.Generation,
                mode,
                new IndexRange(segment.Offset, segment.Count));
    }

    private static JobResourceAccess CreateMemoryRangeAccess<T>(
        ReadOnlyMemory<T> memory,
        IndexRange range,
        JobAccessMode mode)
    {
        ArraySegment<T> segment = GetArraySegment(memory);
        ValidateContainerRange(range, segment.Count);
        JobResourceToken token = JobSystem.GetContainerResourceToken(segment.Array!);
        return CreateRange(
            ResourceKind.Token,
            token.Id,
            token.Version,
            token.Generation,
            mode,
            new IndexRange(segment.Offset + range.Start, range.Length));
    }

    private static ArraySegment<T> GetArraySegment<T>(ReadOnlyMemory<T> memory)
    {
        if (!MemoryMarshal.TryGetArray(memory, out ArraySegment<T> segment) || segment.Array is null)
        {
            throw new NotSupportedException(
                UnsupportedMemoryOwnerMessage);
        }

        return segment;
    }

    private static void ValidateContainerRange(IndexRange range, long count)
    {
        if (range.Start > count - range.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(range), RangeOutsideContainerMessage);
        }
    }

    private static JobResourceAccess CreateRange(
        ResourceKind kind,
        int id,
        int version,
        long generation,
        JobAccessMode mode,
        IndexRange range)
    {
        return new JobResourceAccess(
            kind,
            id,
            version,
            generation,
            mode,
            hasRange: true,
            rangeStart: range.Start,
            rangeLength: range.Length);
    }
}



