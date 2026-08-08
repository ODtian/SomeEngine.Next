using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Render.Cluster.Tests;

internal delegate ValueTask BeforeControlledRead(
    long offset,
    Memory<byte> destination,
    CancellationToken cancellationToken);

internal delegate void AfterControlledRead(long offset, Memory<byte> destination);

internal sealed class ControlledRangeSource : IRangeSource
{
    private readonly IRangeSource _inner;
    private readonly int _targetLength;
    private int _armed;
    private int _targetReadCount;

    internal ControlledRangeSource(IRangeSource inner, int targetLength)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _targetLength = targetLength;
    }

    internal BeforeControlledRead? BeforeRead { get; set; }

    internal AfterControlledRead? AfterRead { get; set; }

    internal int TargetReadCount => Volatile.Read(ref _targetReadCount);

    internal void Arm()
    {
        Volatile.Write(ref _targetReadCount, 0);
        Volatile.Write(ref _armed, 1);
    }

    public long Length => _inner.Length;

    public string Generation => _inner.Generation;

    public bool LeasesAreImmutable => _inner.LeasesAreImmutable;

    public bool RetainsResidentBacking => _inner.RetainsResidentBacking;

    public async ValueTask ReadExactlyAsync(
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        bool target = Volatile.Read(ref _armed) != 0 && destination.Length == _targetLength;
        if (target)
        {
            Interlocked.Increment(ref _targetReadCount);
            BeforeControlledRead? before = BeforeRead;
            if (before is not null)
                await before(offset, destination, cancellationToken).ConfigureAwait(false);
        }

        await _inner.ReadExactlyAsync(offset, destination, cancellationToken).ConfigureAwait(false);

        if (target)
            AfterRead?.Invoke(offset, destination);
    }

    public ValueTask<RangeLease> AcquireAsync(
        long offset,
        int length,
        CancellationToken cancellationToken = default)
        => _inner.AcquireAsync(offset, length, cancellationToken);

    public ValueTask DisposeAsync()
        => _inner.DisposeAsync();
}

internal sealed class ControlledRuntimeMesh : IDisposable
{
    internal ControlledRuntimeMesh(Mesh mesh, ControlledRangeSource source)
    {
        Mesh = mesh;
        Source = source;
    }

    internal Mesh Mesh { get; }

    internal ControlledRangeSource Source { get; }

    public void Dispose() => Mesh.Dispose();
}
