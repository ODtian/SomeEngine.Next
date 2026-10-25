using SomeEngine.Graphics.Direct3D12;
using Xunit;
using Xunit.Abstractions;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class AdapterEnumerationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enumeration_preserves_the_destination_capacity_contract(bool includeSoftware)
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: includeSoftware);
        AdapterInfo sentinel = default(AdapterInfo) with
        {
            Name = "untouched adapter destination",
            DriverVersion = "untouched driver",
        };
        AdapterInfo[] emptyStorage = [sentinel, sentinel];

        bool emptySucceeded = backend.TryEnumerateAdapters(
            options, emptyStorage.AsSpan(1, 0), out int count);

        Assert.Equal(count == 0, emptySucceeded);
        Assert.All(emptyStorage, value => Assert.Equal(sentinel, value));

        // These independent queries assume the host adapter set stays stable.
        // The API does not promise an atomic snapshot across calls.
        AdapterInfo[] exact = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, exact, out int exactCount));
        Assert.Equal(count, exactCount);
        Assert.All(exact, adapter =>
        {
            Assert.False(string.IsNullOrEmpty(adapter.Name));
            Assert.False(string.IsNullOrEmpty(adapter.DriverVersion));
            if (!includeSoftware)
                Assert.True(adapter.HardwareAccelerated);
        });

        bool repeatedEmptySucceeded = backend.TryEnumerateAdapters(
            options, [], out int repeatedCount);
        Assert.Equal(count, repeatedCount);
        Assert.Equal(repeatedCount == 0, repeatedEmptySucceeded);

        AdapterInfo[] oversized = Enumerable.Repeat(sentinel, count + 2).ToArray();
        Assert.True(backend.TryEnumerateAdapters(options, oversized, out int oversizedCount));
        Assert.Equal(count, oversizedCount);
        Assert.Equal(exact, oversized.AsSpan(0, count).ToArray());
        Assert.Equal(sentinel, oversized[count]);
        Assert.Equal(sentinel, oversized[count + 1]);

        if (count > 1)
        {
            AdapterInfo[] undersized = Enumerable.Repeat(sentinel, count - 1).ToArray();
            Assert.False(backend.TryEnumerateAdapters(options, undersized, out int requiredCount));
            Assert.Equal(count, requiredCount);
            Assert.All(undersized, value => Assert.Equal(sentinel, value));
        }

        output.WriteLine(
            $"IncludeSoftware={includeSoftware}; Count={count}; " +
            $"ZeroAdapterBranch={count == 0}; NonemptyUndersizedBranch={count > 1}");
    }
}
