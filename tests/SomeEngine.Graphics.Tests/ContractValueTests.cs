using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class ContractValueTests
{
    [Fact]
    public void Whole_buffer_range_resolves_to_the_resource_size()
    {
        BufferRange resolved = BufferRange.Whole.Resolve(4096);

        Assert.Equal(new BufferRange(0, 4096), resolved);
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(17UL, 1UL)]
    [InlineData(8UL, 9UL)]
    public void Invalid_buffer_ranges_fail_before_reaching_a_backend(ulong offset, ulong size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BufferRange(offset, size).Resolve(16));
    }

    [Fact]
    public void Texture_metadata_owns_its_permitted_format_sequence()
    {
        Format[] permitted = [Format.R8G8B8A8UNorm, Format.R8G8B8A8UNormSrgb];
        var info = new TextureInfo(
            TextureDimension.Texture2D,
            64,
            32,
            1,
            4,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled,
            MemoryType.DeviceLocal,
            permitted,
            0,
            64 * 32 * 4);

        permitted[0] = Format.R32Float;

        Assert.Equal(
            new[] { Format.R8G8B8A8UNorm, Format.R8G8B8A8UNormSrgb },
            info.PermittedViewFormats.ToArray());
    }

    [Fact]
    public void Caller_dispose_is_terminal_and_idempotent_even_when_native_release_fails()
    {
        var value = new TrackingObject(throwDuringRelease: true);

        value.Dispose();
        value.Dispose();

        Assert.Equal(1, value.ReleaseCount);
        Assert.Equal(1, value.RecordedFailureCount);
        Assert.True(value.IsDisposed);
    }

    [Fact]
    public void Default_completion_has_no_usable_queue_or_value()
    {
        QueueCompletion completion = default;

        Assert.Throws<InvalidOperationException>(() => completion.Queue);
        Assert.Throws<InvalidOperationException>(() => completion.Value);
    }

    private sealed class TrackingObject(bool throwDuringRelease) : GraphicsObject("tracking")
    {
        internal int ReleaseCount { get; private set; }
        internal int RecordedFailureCount { get; private set; }

        internal override void Release(bool fromParent)
        {
            ReleaseCount++;
            if (throwDuringRelease)
                throw new InvalidOperationException("injected release failure");
        }

        internal override void RecordReleaseFailure(Exception exception)
        {
            Assert.IsType<InvalidOperationException>(exception);
            RecordedFailureCount++;
        }
    }
}
