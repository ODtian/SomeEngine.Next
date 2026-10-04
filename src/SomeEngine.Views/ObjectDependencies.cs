using System.Buffers;
using SomeEngine.ECS;

namespace SomeEngine.Views;

internal readonly record struct ObjectDependencies(ObjectDependencyList Reads) : IComponent;

/// <summary>The view's object reads, retained by its managed component.</summary>
internal sealed class ObjectDependencyList
{
    private ObjectDependency[] _reads = ArrayPool<ObjectDependency>.Shared.Rent(4);
    private int _count;
    private ulong _readFrame;

    internal ObjectDependencyList(ulong frame) => _readFrame = frame;

    internal ReadOnlySpan<ObjectDependency> Reads => _reads.AsSpan(0, _count);

    internal void Add(VersionCell cell, uint version)
    {
        for (int index = 0; index < _count; index++)
            if (ReferenceEquals(_reads[index].Cell, cell))
                return;

        if (_count == _reads.Length)
        {
            ObjectDependency[] reads = ArrayPool<ObjectDependency>.Shared.Rent(_reads.Length * 2);
            _reads.AsSpan(0, _count).CopyTo(reads);
            ArrayPool<ObjectDependency>.Shared.Return(_reads, clearArray: true);
            _reads = reads;
        }
        _reads[_count++] = new ObjectDependency(cell, version, cell.Error);
    }

    internal void Clear(ulong frame)
    {
        _reads.AsSpan(0, _count).Clear();
        _count = 0;
        _readFrame = frame;
    }

    internal void BeginFrame(ulong frame) => _readFrame = frame;

    internal bool HasChanges(ulong frame)
    {
        for (int index = 0; index < _count; index++)
            if (HasChanges(_reads[index], frame))
                return true;
        return false;
    }

    internal bool HasChanges(ObjectDependency read, ulong frame)
    {
        try
        {
            read.Cell.Refresh(frame);
        }
        catch (Exception exception) when (ReferenceEquals(exception, read.Cell.Error))
        {
            // The consuming calculation reports the failure when it reads the Memo.
        }
        uint version = Volatile.Read(ref read.Cell.Version);
        return ((version & 1) != 0 && _readFrame != frame) || version != read.Version ||
            !ReferenceEquals(read.Error, read.Cell.Error);
    }

    // ECS retains this object for the component's lifetime, including component moves.
    // Returning a cleared array here also covers direct entity and World disposal.
    ~ObjectDependencyList() => ArrayPool<ObjectDependency>.Shared.Return(_reads, clearArray: true);

    internal readonly record struct ObjectDependency(VersionCell Cell, uint Version, Exception? Error);
}
