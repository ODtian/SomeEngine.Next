using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;
using Xunit.Abstractions;

namespace SomeEngine.Serialization.Tests;

public sealed class AssetPackCatalogTests
{
    private readonly ITestOutputHelper _output;

    public AssetPackCatalogTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    public async Task LoadedCatalogReturnsEveryEntryInWrittenOrder(int count)
    {
        int[] numbers = Enumerable.Range(1, count).ToArray();
        byte[] bytes = await WritePackAsync(numbers.Reverse().ToArray());
        await using AssetPack pack = await AssetPack.OpenAsync(
            new MemoryRangeSource(bytes), ownsSource: true);

        Assert.Equal(count, pack.Count);
        Assert.Equal(numbers.Select(AssetId), pack.Entries.Select(entry => entry.AssetId));
        foreach (int number in numbers)
        {
            Guid id = AssetId(number);
            Assert.True(pack.TryGetEntry(id, out AssetPackEntry? entry));
            Assert.Equal("mesh", entry!.AssetType);
            Assert.Equal(TestRoot.SchemaFingerprint, entry.SchemaFingerprint);
            await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
                await pack.OpenAssetSourceAsync(id), ownsSource: true);
            Assert.Equal(number, document.Root.Int32Value);
            Assert.Equal(document.Generation, entry.DocumentVersion);
            using ChunkLease chunk = await document.AcquireChunkAsync(1001);
            Assert.True(chunk.Memory.Span.SequenceEqual(new byte[] { 1, 4, 9, 16 }));
        }
    }

    [Fact]
    public async Task DifferentInsertionOrdersProduceTheSamePack()
    {
        byte[] first = await WritePackAsync(3, 1, 2);
        byte[] second = await WritePackAsync(2, 3, 1);
        Assert.Equal(first, second);
        await using AssetPack firstPack = await AssetPack.OpenAsync(
            new MemoryRangeSource(first), ownsSource: true);
        await using AssetPack secondPack = await AssetPack.OpenAsync(
            new MemoryRangeSource(second), ownsSource: true);
        Assert.Equal(firstPack.Generation, secondPack.Generation);
        Assert.Equal(firstPack.Entries.Select(entry => entry.AssetId),
            secondPack.Entries.Select(entry => entry.AssetId));
        _output.WriteLine($"A06C_PUBLIC_PACK SHA256={Convert.ToHexString(SHA256.HashData(first))} " +
            $"GENERATION={firstPack.Generation:D} " +
            $"ENTRIES={string.Join(",", firstPack.Entries.Select(entry => entry.AssetId.ToString("D")))}");
    }

    [Fact]
    public async Task MissingAssetKeepsThePublicLookupAndOpenFailures()
    {
        byte[] bytes = await WritePackAsync(1);
        await using AssetPack pack = await AssetPack.OpenAsync(
            new MemoryRangeSource(bytes), ownsSource: true);
        Assert.False(pack.TryGetEntry(AssetId(2), out AssetPackEntry? missing));
        Assert.Null(missing);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            pack.OpenAssetSourceAsync(AssetId(2)).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            pack.ReadVerifiedAssetBytesAsync(AssetId(2)).AsTask());
    }

    [Fact]
    public async Task ClosingPackKeepsAnOpenedAssetReadableUntilItCloses()
    {
        byte[] bytes = await WritePackAsync(1);
        await using var source = new MemoryRangeSource(bytes);
        await using AssetPack pack = await AssetPack.OpenAsync(source, ownsSource: true);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            await pack.OpenAssetSourceAsync(AssetId(1)), ownsSource: true);
        await pack.DisposeAsync();
        Assert.Equal(1, document.Root.Int32Value);
        using (ChunkLease chunk = await document.AcquireChunkAsync(1001))
            Assert.True(chunk.Memory.Span.SequenceEqual(new byte[] { 1, 4, 9, 16 }));
        await document.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.ReadExactlyAsync(0, new byte[1]).AsTask());
    }

    [Fact]
    public async Task CollectionLimitRejectsAValidPackWithTooManyEntries()
    {
        byte[] bytes = await WritePackAsync(1, 2);
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AssetPack.OpenAsync(source, limits: new BinaryReadLimits
            {
                MaxCollectionCount = 1,
            }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
    }

    [Fact]
    public async Task CollectionLimitAcceptsAValidPackAtItsEntryCount()
    {
        byte[] bytes = await WritePackAsync(1, 2);
        await using AssetPack pack = await AssetPack.OpenAsync(
            new MemoryRangeSource(bytes), ownsSource: true,
            limits: new BinaryReadLimits { MaxCollectionCount = 2 });
        Assert.Equal(new[] { AssetId(1), AssetId(2) },
            pack.Entries.Select(entry => entry.AssetId));
    }

    [Fact]
    public async Task ConfiguredBudgetRejectsTheSameValidSingleton()
    {
        byte[] bytes = await WritePackAsync(1);
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AssetPack.OpenAsync(source, limits: new BinaryReadLimits
            {
                MaxAllocationBytes = 543,
            }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
    }

    [Fact]
    public async Task ConfiguredBudgetAcceptsTheSameValidSingleton()
    {
        byte[] bytes = await WritePackAsync(1);
        await using AssetPack pack = await AssetPack.OpenAsync(
            new MemoryRangeSource(bytes), ownsSource: true,
            limits: new BinaryReadLimits { MaxAllocationBytes = 544 });
        Assert.True(pack.TryGetEntry(AssetId(1), out AssetPackEntry? entry));
        Assert.Equal("mesh", entry!.AssetType);
    }

    [Fact]
    public async Task FailedOwnedPackOpenClosesTheSuppliedSource()
    {
        byte[] bytes = await WritePackAsync(1);
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AssetPack.OpenAsync(source, ownsSource: true,
                limits: new BinaryReadLimits { MaxAllocationBytes = 543 }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            source.ReadExactlyAsync(0, new byte[1]).AsTask());
    }

    [Fact]
    public async Task FailedBorrowedPackOpenLeavesTheSuppliedSourceUsable()
    {
        byte[] bytes = await WritePackAsync(1);
        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AssetPack.OpenAsync(source, ownsSource: false,
                limits: new BinaryReadLimits { MaxAllocationBytes = 543 }).AsTask());
        Assert.Equal(SerializationError.LimitExceeded, SerializationErrors.GetError(error));
        byte[] first = new byte[1];
        await source.ReadExactlyAsync(0, first);
        Assert.Equal(bytes[0], first[0]);
    }

    private static Guid AssetId(int number)
        => Guid.Parse($"{number:X8}-0000-0000-0000-000000000000");

    private static async Task<byte[]> WritePackAsync(params int[] numbers)
    {
        var builder = new AssetPackBuilder();
        foreach (int number in numbers)
        {
            using var documentBytes = new MemoryStream();
            await BinaryDocumentWriter.Create(TestRoots.Canonical(text: "catalog", number: number))
                .AddChunk(1001, new byte[] { 1, 4, 9, 16 }, typeFingerprint: 0xA55E7UL)
                .WriteAsync(documentBytes);
            builder.AddAsset(AssetId(number), "mesh", documentBytes.ToArray(), TestRoot.SchemaFingerprint);
        }
        using var packBytes = new MemoryStream();
        await builder.WriteAsync(packBytes);
        return packBytes.ToArray();
    }
}
