using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class ResourceLifetimeTests
{
    [Fact]
    public void DisposingACheckpointKeepsTheWorldsSharedStreamUsableUntilTheWorldCloses()
    {
        var stream = new CountedStream();
        using var world = new World();
        world.SetResource<MemoryStream>(stream);
        using (var checkpoint = new WorldCheckpoint()) checkpoint.Capture(world);

        world.ReadResource<MemoryStream>().WriteByte(7);

        Assert.Same(stream, world.ReadResource<MemoryStream>());
        Assert.Equal(new byte[] { 7 }, stream.ToArray());
        Assert.Equal(0, stream.DisposeCount);
        world.Dispose();
        Assert.Equal(1, stream.DisposeCount);
        Assert.False(stream.CanWrite);
    }

    [Fact]
    public void DisposingAFailedCandidateKeepsTheSourceStreamAndComponentValues()
    {
        var stream = new CountedStream();
        using var source = new World();
        var entity = source.Create(new Number(3));
        source.SetResource<MemoryStream>(stream);
        using (var checkpoint = new WorldCheckpoint().Capture(source, CheckpointProgress.Preserve))
        {
            using var candidate = new World();
            checkpoint.Restore(candidate);
            using var commands = new CommandBuffer(candidate);
            commands.Set(entity, new Number(9));
            commands.Add(entity, new Number(12));

            Assert.Throws<InvalidOperationException>(commands.Playback);

            Assert.Equal(9, candidate.Read<Number>(entity).Value);
        }

        source.ReadResource<MemoryStream>().WriteByte(11);
        Assert.Equal(3, source.Read<Number>(entity).Value);
        Assert.Equal(new byte[] { 11 }, stream.ToArray());
        Assert.Equal(0, stream.DisposeCount);
        source.Dispose();
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public void SuccessfulRestoreTransfersTheCandidatesIndependentResourceValueAndReleasesEachStreamOnce()
    {
        var first = new CountedStream();
        var second = new CountedStream();
        using var source = new World();
        source.SetResource<MemoryStream>(first);
        using (var checkpoint = new WorldCheckpoint().Capture(source, CheckpointProgress.Preserve))
        {
            using var candidate = new World();
            checkpoint.Restore(candidate);
            candidate.GetResource<MemoryStream>() = second;
            Assert.Same(first, source.ReadResource<MemoryStream>());
            Assert.Same(second, candidate.ReadResource<MemoryStream>());

            checkpoint.Capture(candidate, CheckpointProgress.Preserve);
            checkpoint.Restore(source);

            Assert.Same(second, source.ReadResource<MemoryStream>());
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(0, second.DisposeCount);
        }

        source.ReadResource<MemoryStream>().WriteByte(19);
        Assert.Equal(new byte[] { 19 }, second.ToArray());
        Assert.Equal(0, second.DisposeCount);
        source.Dispose();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    private readonly record struct Number(int Value) : IComponent;

    private sealed class CountedStream : MemoryStream
    {
        public int DisposeCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
