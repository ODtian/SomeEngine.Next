namespace SomeEngine.Job;

public readonly partial struct JobResourceAccess
{
    public static JobResourceAccess Read<T>(Scheduler scheduler, T[] array, long start, long length) =>
        CreateContainerRangeAccess(array, array.LongLength, new IndexRange(start, length), JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, T[] array, Span<T> span) =>
        CreateArraySpanRangeAccess<T>(array, span, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, T[] array, ReadOnlySpan<T> span) =>
        CreateArraySpanRangeAccess(array, span, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, List<T> list) =>
        CreateContainerAccess(list, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, List<T> list, long start, long length) =>
        CreateContainerRangeAccess(list, list.Count, new IndexRange(start, length), JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<TKey, TValue>(Scheduler scheduler, Dictionary<TKey, TValue> dictionary)
        where TKey : notnull => CreateContainerAccess(dictionary, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, Stack<T> stack) =>
        CreateContainerAccess(stack, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, Queue<T> queue) =>
        CreateContainerAccess(queue, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, Memory<T> memory) =>
        CreateMemoryAccess<T>(memory, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, Memory<T> memory, long start, long length) =>
        CreateMemoryRangeAccess<T>(memory, new IndexRange(start, length), JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, ReadOnlyMemory<T> memory) =>
        CreateMemoryAccess(memory, JobAccessMode.Read, scheduler);

    public static JobResourceAccess Read<T>(Scheduler scheduler, ReadOnlyMemory<T> memory, long start, long length) =>
        CreateMemoryRangeAccess(memory, new IndexRange(start, length), JobAccessMode.Read, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, T[] array, long start, long length) =>
        CreateContainerRangeAccess(array, array.LongLength, new IndexRange(start, length), JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, T[] array, Span<T> span) =>
        CreateArraySpanRangeAccess<T>(array, span, JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, List<T> list) =>
        CreateContainerAccess(list, JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, List<T> list, long start, long length) =>
        CreateContainerRangeAccess(list, list.Count, new IndexRange(start, length), JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<TKey, TValue>(Scheduler scheduler, Dictionary<TKey, TValue> dictionary)
        where TKey : notnull => CreateContainerAccess(dictionary, JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, Stack<T> stack) =>
        CreateContainerAccess(stack, JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, Queue<T> queue) =>
        CreateContainerAccess(queue, JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, Memory<T> memory) =>
        CreateMemoryAccess<T>(memory, JobAccessMode.Write, scheduler);

    public static JobResourceAccess Write<T>(Scheduler scheduler, Memory<T> memory, long start, long length) =>
        CreateMemoryRangeAccess<T>(memory, new IndexRange(start, length), JobAccessMode.Write, scheduler);

    public static JobResourceAccess Exclusive<T>(Scheduler scheduler, T[] array) =>
        CreateContainerAccess(array, JobAccessMode.Exclusive, scheduler);

    public static JobResourceAccess Exclusive<T>(Scheduler scheduler, List<T> list) =>
        CreateContainerAccess(list, JobAccessMode.Exclusive, scheduler);

    public static JobResourceAccess Exclusive<TKey, TValue>(Scheduler scheduler, Dictionary<TKey, TValue> dictionary)
        where TKey : notnull => CreateContainerAccess(dictionary, JobAccessMode.Exclusive, scheduler);

    public static JobResourceAccess Exclusive<T>(Scheduler scheduler, Stack<T> stack) =>
        CreateContainerAccess(stack, JobAccessMode.Exclusive, scheduler);

    public static JobResourceAccess Exclusive<T>(Scheduler scheduler, Queue<T> queue) =>
        CreateContainerAccess(queue, JobAccessMode.Exclusive, scheduler);

    public static JobResourceAccess Exclusive<T>(Scheduler scheduler, Memory<T> memory) =>
        CreateMemoryAccess<T>(memory, JobAccessMode.Exclusive, scheduler);

    public static JobResourceAccess Exclusive<T>(Scheduler scheduler, Memory<T> memory, long start, long length) =>
        CreateMemoryRangeAccess<T>(memory, new IndexRange(start, length), JobAccessMode.Exclusive, scheduler);

    public static JobResourceAccess Read<TContainer, TProvider>(Scheduler scheduler, ref TContainer container)
        where TProvider : struct, IJobResourceProvider<TContainer, WholeAccess> =>
        TProvider.Read(scheduler, ref container, default);

    public static JobResourceAccess Read<TContainer, TProvider, TAccess>(
        Scheduler scheduler, ref TContainer container, TAccess access)
        where TProvider : struct, IJobResourceProvider<TContainer, TAccess> =>
        TProvider.Read(scheduler, ref container, access);

    public static JobResourceAccess Write<TContainer, TProvider>(Scheduler scheduler, ref TContainer container)
        where TProvider : struct, IJobResourceProvider<TContainer, WholeAccess> =>
        TProvider.Write(scheduler, ref container, default);

    public static JobResourceAccess Write<TContainer, TProvider, TAccess>(
        Scheduler scheduler, ref TContainer container, TAccess access)
        where TProvider : struct, IJobResourceProvider<TContainer, TAccess> =>
        TProvider.Write(scheduler, ref container, access);

    public static JobResourceAccess Exclusive<TContainer, TProvider>(Scheduler scheduler, ref TContainer container)
        where TProvider : struct, IJobResourceProvider<TContainer, WholeAccess> =>
        TProvider.Exclusive(scheduler, ref container, default);

    public static JobResourceAccess Exclusive<TContainer, TProvider, TAccess>(
        Scheduler scheduler, ref TContainer container, TAccess access)
        where TProvider : struct, IJobResourceProvider<TContainer, TAccess> =>
        TProvider.Exclusive(scheduler, ref container, access);
}
